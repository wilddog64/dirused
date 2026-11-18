using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using System.Text.Json;
using Xunit;

namespace Dirused.Tests.Portable;

public class PurgeTests
{
    [Theory]
    [InlineData(@"C:\logs\a.log", @"C:\logs", true)]
    [InlineData(@"C:\logs\a.log", @"C:\logs\", true)]
    [InlineData(@"C:\a.log", @"C:\", true)]
    [InlineData(@"\\h\d$\x\a.log", @"\\h\d$", true)]
    [InlineData(@"\\h\d$\x\a.log", @"\\H\D$\", true)]
    [InlineData(@"C:\logs2\a.log", @"C:\logs", false)]
    [InlineData(@"C:\logs", @"C:\logs", false)]
    [InlineData(@"C:\logs\..\x.log", @"C:\logs", false)]
    [InlineData(@"D:\a.log", @"C:\", false)]
    [InlineData(@"C:\a.log", "", false)]
    public void IsStrictlyUnder(string path, string root, bool expected) => Assert.Equal(expected, Purge.IsStrictlyUnder(path, root));

    [Fact]
    public void DryRunNeverOpensAndMarksStatuses()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var target = @"C:\t";
        var files = new List<LargestFile>
        {
            new(@"C:\t\small.log", (1L << 30) - 1, now.AddDays(-2)), new(@"C:\t\exact.log", 1L << 30, now.AddDays(-2)),
            new(@"C:\t\new.log", 20, now), new(@"C:\t\unknown.log", 30, null), new(@"C:\t\equal.log", 40, now.AddDays(-1))
        };
        var result = Result(target, files);
        var fs = new FakeFs();
        var run = Purge.Apply(new[] { result }, 1, 1L << 30, false, fs, () => now, _ => throw new Exception(), CancellationToken.None);
        Assert.Equal(new[] { "skipped: smaller than 1.0 GB", "would delete", "skipped: modified within 1 day(s)", "skipped: modification time unknown", "skipped: modified within 1 day(s)" }, run.Results[0].Largest!.Files.Select(f => f.PurgeStatus));
        Assert.Equal(1L << 30, run.WouldDeleteBytes); Assert.Empty(fs.Opens); Assert.Empty(fs.RootResolutions);
        var lowerThreshold = Purge.Apply(new[] { result }, 1, 100L << 20, false, fs, () => now, null, CancellationToken.None);
        Assert.Equal("would delete", lowerThreshold.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal((1L << 30) - 1 + (1L << 30), lowerThreshold.WouldDeleteBytes);
    }

    [Fact]
    public void ConfirmDeletesEligibleAndContinuesPastFailures()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); var target = @"C:\t";
        var files = new List<LargestFile> { new(@"C:\t\small.log", 100L << 20, now.AddDays(-2)), new(@"C:\t\ok.log", 1L << 30, now.AddDays(-2)), new(@"C:\t\recent.log", 1L << 30, now) };
        var fs = new FakeFs(); fs.Lengths[files[0].Path] = 100L << 20; fs.Lengths[files[1].Path] = 1L << 30; fs.Lengths[files[2].Path] = 1L << 30;
        fs.Times[files[0].Path] = now.AddDays(-2); fs.Times[files[1].Path] = now.AddDays(-2); fs.Times[files[2].Path] = now;
        using var log = new StringWriter();
        var run = Purge.Apply(new[] { Result(target, files) }, 1, 1L << 30, true, fs, () => now, s => log.WriteLine(s), CancellationToken.None);
        Assert.Equal("skipped: smaller than 1.0 GB", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("skipped: modified within 1 day(s)", run.Results[0].Largest!.Files[2].PurgeStatus);
        Assert.Equal(1, run.Deleted); Assert.Equal(1, fs.Deletes);
        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        var line = lines[0];
        Assert.Equal(6, line.Split('\t').Length);
        Assert.Equal(files[1].Path, line.TrimEnd('\r').Split('\t')[3]);
        Assert.EndsWith("deleted", line.TrimEnd('\r').Split('\t')[^1]);
    }

    [Fact]
    public void HandleChecksSkipUnsafeFilesAndDeleteEligibleSibling()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); var target = @"C:\t";
        var names = new[] { "vanished", "directory", "reparse", "linked", "outside", "length", "time", "readonly", "good" };
        var files = names.Select(n => new LargestFile($@"C:\t\{n}.log", 100, now.AddDays(-2))).ToList();
        var fs = new FakeFs();
        fs.Errors[files[0].Path] = new PurgeOpen("gone", notFound: true);
        fs.Info(files[1], new PurgeFileInfo(files[1].Path, 100, now.AddDays(-2), true, false, 1));
        fs.Info(files[2], new PurgeFileInfo(files[2].Path, 100, now.AddDays(-2), false, true, 1));
        fs.Info(files[3], new PurgeFileInfo(files[3].Path, 100, now.AddDays(-2), false, false, 2));
        fs.Info(files[4], new PurgeFileInfo(@"C:\outside\outside.log", 100, now.AddDays(-2), false, false, 1));
        fs.Info(files[5], new PurgeFileInfo(files[5].Path, 101, now.AddDays(-2), false, false, 1));
        fs.Info(files[6], new PurgeFileInfo(files[6].Path, 100, now.AddDays(-3), false, false, 1));
        fs.Info(files[7], new PurgeFileInfo(files[7].Path, 100, now.AddDays(-2), false, false, 1) { IsReadOnly = true });
        fs.Info(files[8], new PurgeFileInfo(files[8].Path, 100, now.AddDays(-2), false, false, 1));
        var run = Purge.Apply(new[] { Result(target, files) }, 1, 1, true, fs, () => now, null, CancellationToken.None);
        Assert.Equal(new[] { "skipped: no longer exists", "skipped: not a regular file", "skipped: not a regular file", "skipped: hard-linked", "skipped: outside target", "skipped: changed since scan", "skipped: changed since scan", "skipped: read-only", "deleted" }, run.Results[0].Largest!.Files.Select(f => f.PurgeStatus));
        Assert.Equal(1, run.Deleted); Assert.Equal(1, fs.Deletes);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("D:")]
    [InlineData("//h/s")]
    public void ScanRootIsTheContainmentAndResolveRootPath(string target)
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var file = new LargestFile(@"C:\t\x.log", 20, now.AddDays(-2));
        var result = Result(target, new() { file }) with { ScanRoot = @"C:\t" };
        var fs = new FakeFs(); fs.Lengths[file.Path] = 20; fs.Times[file.Path] = file.LastWriteUtc!.Value;
        var run = Purge.Apply(new[] { result }, 1, 1, true, fs, () => now, null, CancellationToken.None);
        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal(new[] { @"C:\t" }, fs.RootResolutions);
    }

    [Fact]
    public void TargetFallbackRelativePathAndRootResolutionFailureFailSafe()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var relative = new LargestFile("relative.log", 20, now.AddDays(-2));
        var relativeFs = new FakeFs();
        var relativeRun = Purge.Apply(new[] { Result(".", new() { relative }) }, 1, 1, true, relativeFs, () => now, null, CancellationToken.None);
        Assert.Equal("failed: path is not absolute", relativeRun.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Empty(relativeFs.Opens);

        var targetFallbackFs = new FakeFs();
        var fallback = Purge.Apply(new[] { Result(".", new() { new LargestFile(@"C:\t\x.log", 20, now.AddDays(-2)) }) }, 1, 1, false, targetFallbackFs, () => now, null, CancellationToken.None);
        Assert.Equal("skipped: outside target", fallback.Results[0].Largest!.Files[0].PurgeStatus);

        var file = new LargestFile(@"C:\t\x.log", 20, now.AddDays(-2)); var failingFs = new FakeFs { RootError = "root busy" }; using var log = new StringWriter();
        var failed = Purge.Apply(new[] { Result(@"C:\t", new() { file }) }, 1, 1, true, failingFs, () => now, s => log.WriteLine(s), CancellationToken.None);
        Assert.Equal("failed: cannot open target: root busy", failed.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal(1, failed.Failed); Assert.Empty(failingFs.Opens); Assert.Single(log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void PurgeLogEntryIsSkippedAndLogFieldsAreSanitized()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var logPath = @"C:\t\purge.log";
        var files = new List<LargestFile> { new(logPath, 20, now.AddDays(-2)), new("C:\\t\\odd\nname.log", 20, now.AddDays(-2)) };
        var fs = new FakeFs(); fs.Lengths[files[1].Path] = 20; fs.Times[files[1].Path] = files[1].LastWriteUtc!.Value;
        using var log = new StringWriter();
        var run = Purge.Apply(new[] { Result(@"C:\t", files) }, 1, 1, true, fs, () => now, s => log.WriteLine(s), CancellationToken.None, logPath);
        Assert.Equal("skipped: purge log", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.DoesNotContain(logPath, fs.Opens);
        Assert.DoesNotContain('\n', log.ToString().Split('\t')[3]);
    }

    [Fact]
    public void DeleteStillPresentIsReportedAndLoggedAgain()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); var file = new LargestFile(@"C:\t\pending.log", 20, now.AddDays(-2));
        var fs = new FakeFs { KeepExistingAfterDelete = true }; fs.Lengths[file.Path] = 20; fs.Times[file.Path] = file.LastWriteUtc!.Value; using var log = new StringWriter();
        var run = Purge.Apply(new[] { Result(@"C:\t", new() { file }) }, 1, 1, true, fs, () => now, s => log.WriteLine(s), CancellationToken.None);
        Assert.Equal("failed: delete pending (another process has the file open)", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal(1, run.Failed); Assert.Equal(0, run.Deleted);
        Assert.Equal(new[] { "deleted", "failed: delete pending (another process has the file open)" }, log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('\t')[^1]));
    }

    [Fact]
    public void OutsideAndTrailingDotNamesNeverReachFilesystem()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var files = new List<LargestFile> { new(@"C:\other\x.log", 100, now.AddDays(-2)), new(@"C:\t\app.log.", 100, now.AddDays(-2)) };
        var fs = new FakeFs();
        var run = Purge.Apply(new[] { Result(@"C:\t", files) }, 1, 1, true, fs, () => now, null, CancellationToken.None);
        Assert.Equal(new[] { "skipped: outside target", "skipped: outside target" }, run.Results[0].Largest!.Files.Select(f => f.PurgeStatus));
        Assert.Empty(fs.Opens); Assert.Single(fs.RootResolutions);
    }

    [Fact]
    public void OpenAndDeleteFailuresAreLoggedAndDoNotStopNextFile()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); var target = @"C:\t";
        var files = new[] { "openerror", "throws", "markerror", "good" }.Select(n => new LargestFile($@"C:\t\{n}.log", 100, now.AddDays(-2))).ToList();
        var fs = new FakeFs();
        foreach (var file in files) { fs.Lengths[file.Path] = file.LogicalBytes; fs.Times[file.Path] = file.LastWriteUtc!.Value; }
        fs.Errors[files[0].Path] = new PurgeOpen("used by another process"); fs.OpenThrows[files[1].Path] = new UnauthorizedAccessException("denied"); fs.DeleteThrows.Add(files[2].Path);
        using var log = new StringWriter();
        var run = Purge.Apply(new[] { Result(target, files) }, 1, 1, true, fs, () => now, s => log.WriteLine(s), CancellationToken.None);
        Assert.Equal(3, run.Failed); Assert.Equal(1, run.Deleted); Assert.Equal(2, fs.DeletesAttempted); Assert.Equal(1, fs.Deletes);
        Assert.Equal(new[] { "failed: used by another process", "failed: denied", "failed: delete failed", "deleted" }, run.Results[0].Largest!.Files.Select(f => f.PurgeStatus));
    }

    [Fact]
    public void LogFailureAndCancellationStopFurtherOpens()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); var target = @"C:\t";
        var files = new[] { "one", "two", "three" }.Select(n => new LargestFile($@"C:\t\{n}.log", 100, now.AddDays(-2))).ToList();
        var logFs = new FakeFs(); foreach (var file in files) { logFs.Lengths[file.Path] = file.LogicalBytes; logFs.Times[file.Path] = file.LastWriteUtc!.Value; }
        var logRun = Purge.Apply(new[] { Result(target, files) }, 1, 1, true, logFs, () => now, _ => throw new IOException("log write failed"), CancellationToken.None);
        Assert.Equal(new[] { "failed: log write failed", "not attempted: log write failed", "not attempted: log write failed" }, logRun.Results[0].Largest!.Files.Select(f => f.PurgeStatus));
        Assert.Equal(new[] { files[0].Path }, logFs.Opens); Assert.Equal(1, logFs.DeletesAttempted); Assert.Equal(1, logFs.Unmarks); Assert.Empty(logFs.DeletedPaths);

        var cancelFs = new FakeFs(); foreach (var file in files) { cancelFs.Lengths[file.Path] = file.LogicalBytes; cancelFs.Times[file.Path] = file.LastWriteUtc!.Value; } using var cts = new CancellationTokenSource();
        var cancelRun = Purge.Apply(new[] { Result(target, files) }, 1, 1, true, cancelFs, () => now, _ => cts.Cancel(), cts.Token);
        Assert.Equal(new[] { "deleted", "not attempted: cancelled", "not attempted: cancelled" }, cancelRun.Results[0].Largest!.Files.Select(f => f.PurgeStatus));
        Assert.Equal(new[] { files[0].Path, files[0].Path }, cancelFs.Opens);
    }

    [Fact]
    public void RendererAddsPurgeStatus()
    {
        var r = Result(@"C:\t", new() { new LargestFile(@"C:\t\x.log", 1024, DateTime.UtcNow) { PurgeStatus = "would delete" } }) with { Directories = new() { new Entry("dir", 2, 2) }, Files = new() { new Entry("file", 3, 3) } };
        r = r with { Largest = r.Largest! with { PurgeBytes = 1024, PurgeDryRun = true }, ScanRoot = @"C:\t" };
        using var text = new StringWriter(); new TextRenderer().Render(text, new[] { r });
        Assert.Contains("[would delete]", text.ToString()); Assert.Contains("would free 1.0K", text.ToString());
        using var html = new StringWriter(); new HtmlRenderer(false, 85, 90, 50).Render(html, new[] { r });
        Assert.Contains("<th>Purge</th>", html.ToString()); Assert.Contains("<td class=\"purge\">would delete</td>", html.ToString()); Assert.Contains("colspan=\"5\"", html.ToString()); Assert.Contains("would free 1.0 KB", html.ToString());
        using var csv = new StringWriter(); new CsvRenderer(purge: true).Render(csv, new[] { r });
        Assert.Contains("on_disk_bytes,purge_status", csv.ToString());
        Assert.All(csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => Assert.Equal(6, line.Split(',').Length));
        using var csvWithoutPurge = new StringWriter(); new CsvRenderer().Render(csvWithoutPurge, new[] { r with { Largest = r.Largest! with { PurgeDryRun = null, PurgeBytes = null } } });
        Assert.DoesNotContain("purge_status", csvWithoutPurge.ToString());
        using var json = new StringWriter(); new JsonRenderer().Render(json, new[] { r });
        using var document = JsonDocument.Parse(json.ToString());
        var target = document.RootElement.GetProperty("targets")[0]; Assert.False(target.TryGetProperty("scanRoot", out _));
        Assert.Equal("would delete", target.GetProperty("largest").GetProperty("files")[0].GetProperty("purgeStatus").GetString());
    }

    private static ScanResult Result(string target, List<LargestFile> files) => new(target, new Volume("C:", 0, 1, 1, 0), new(), new(), new(), true) { Largest = new LargestFiles(new[] { "*.log" }, files.Count, files.Sum(f => f.LogicalBytes), files, true) };
    private sealed class FakeFs : IPurgeFileSystem
    {
        private readonly HashSet<string> pending = new();
        public List<string> Opens { get; } = new(); public List<string> RootResolutions { get; } = new(); public Dictionary<string, PurgeOpen> Errors { get; } = new(); public Dictionary<string, Exception> OpenThrows { get; } = new(); public HashSet<string> DeleteThrows { get; } = new(); public Dictionary<string, long> Lengths { get; } = new(); public Dictionary<string, DateTime> Times { get; } = new(); public Dictionary<string, PurgeFileInfo> Infos { get; } = new(); public HashSet<string> DeletedPaths { get; } = new(); public int Deletes; public int DeletesAttempted; public int Unmarks; public string? RootError; public bool KeepExistingAfterDelete;
        public string? ResolveRoot(string path, out string? error) { RootResolutions.Add(path); error = RootError; return RootError == null ? path : null; }
        public void Info(LargestFile file, PurgeFileInfo info) => Infos[file.Path] = info;
        public PurgeOpen Open(string path)
        {
            Opens.Add(path);
            if (OpenThrows.TryGetValue(path, out var thrown)) throw thrown;
            if (Errors.TryGetValue(path, out var error)) return error;
            if (DeletedPaths.Contains(path)) return new PurgeOpen("not found", notFound: true);
            if (Infos.TryGetValue(path, out var scripted)) return new PurgeOpen(scripted, new FakeHandle(() => Closed(path)), () => Delete(path), () => Unmark(path));
            var info = new PurgeFileInfo(path, path == @"C:\t" ? 0 : Lengths.GetValueOrDefault(path, 20), Times.GetValueOrDefault(path, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)), path == @"C:\t", false, 1);
            return new PurgeOpen(info, new FakeHandle(() => Closed(path)), () => Delete(path), () => Unmark(path));
        }
        private void Delete(string path) { DeletesAttempted++; if (DeleteThrows.Contains(path)) throw new IOException("delete failed"); pending.Add(path); }
        private void Unmark(string path) { Unmarks++; pending.Remove(path); }
        private void Closed(string path) { if (pending.Remove(path)) { if (!KeepExistingAfterDelete) DeletedPaths.Add(path); Deletes++; } }
        private sealed class FakeHandle(Action close) : IDisposable { public void Dispose() => close(); }
    }
}
