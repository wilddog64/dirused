using System.Text.Json;
using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Common.Targets;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class TopFoldersTests
{
    private static Target Target(string name) => new(name, TargetKind.LocalPath, "C:\\", name);
    private static ScanResult Result(string target, double percent = 50, bool complete = true) =>
        new(target, new Volume("C:\\", 50, 50, 100, percent), new(), new(), new(), complete);
    private static ScanResult FolderScan(string name, params Entry[] entries) => Result(name) with { Directories = entries.ToList() };

    [Fact]
    public void Test_AttachWalksOnlyWarnAndCritTargets()
    {
        var targets = new[] { Target("ok"), Target("warn"), Target("crit"), Target("null") };
        var results = new[] { Result("ok") with { Level = FullnessLevel.Ok }, Result("warn") with { Level = FullnessLevel.Warn }, Result("crit") with { Level = FullnessLevel.Crit }, Result("null") };
        var walked = new List<string>();
        var attached = TopFolders.Attach(targets, results, (t, _) => { walked.Add(t.Display); return FolderScan(t.Display); }, new(), CancellationToken.None);
        Assert.Equal(new[] { "warn", "crit" }, walked);
        Assert.Empty(attached[0].Directories);
        Assert.Null(attached[0].TopFoldersNote);
        Assert.Empty(attached[3].Directories);
        Assert.Null(attached[3].TopFoldersNote);
    }

    [Fact]
    public void Test_AttachKeepsTop3FoldersByLogicalSize()
    {
        var target = Target("crit");
        var original = Result("crit", 95) with { Level = FullnessLevel.Crit };
        var dirs = new[] { new Entry("e", 5, 0), new Entry("c", 9, 0), new Entry("b", 9, 0), new Entry("d", 7, 0), new Entry("a", 9, 0) };
        var attached = TopFolders.Attach(new[] { target }, new[] { original }, (t, _) => FolderScan(t.Display, dirs) with { Files = new() { new Entry("file", 99, 0) } }, new(), CancellationToken.None)[0];
        Assert.Equal(new[] { "a", "b", "c" }, attached.Directories.Select(e => e.Path));
        Assert.Equal(original.Volume, attached.Volume);
        Assert.Equal(original.Level, attached.Level);
        Assert.Equal(original.Complete, attached.Complete);
        Assert.Equal(original.Errors, attached.Errors);
    }

    [Fact]
    public void Test_AttachNotesIncompleteSecondScan()
    {
        var original = Result("crit", 95) with { Level = FullnessLevel.Crit };
        var attached = TopFolders.Attach(new[] { Target("crit") }, new[] { original }, (t, _) => FolderScan(t.Display, new("x", 2, 0), new("y", 1, 0)) with { Complete = false, Errors = new() { new("X:\\a\\b", "Access is denied.") } }, new(), CancellationToken.None)[0];
        Assert.Equal(2, attached.Directories.Count);
        Assert.Equal("X:\\a\\b: Access is denied.", attached.TopFoldersNote);
        Assert.Equal(original.Complete, attached.Complete);
        Assert.Equal(original.Errors, attached.Errors);
    }

    [Fact]
    public void Test_AttachTimeoutLeavesVolumeLevel()
    {
        var original = Result("crit", 95) with { Level = FullnessLevel.Crit };
        var attached = TopFolders.Attach(new[] { Target("crit") }, new[] { original }, (t, token) => { token.WaitHandle.WaitOne(); throw new OperationCanceledException(token); }, new(Timeout: TimeSpan.FromMilliseconds(200)), CancellationToken.None)[0];
        Assert.Contains("TIMED OUT after", attached.TopFoldersNote);
        Assert.Equal(FullnessLevel.Crit, attached.Level);
        Assert.Empty(attached.Directories);
    }

    [Fact]
    public void Test_SummaryRendererPrintsFoldersUnderTheirVolume()
    {
        var crit = Result("C:\\", 95) with { Level = FullnessLevel.Crit, Directories = new() { new("a", 100, 0), new("long", 200, 0), new("c", 50, 0) }, TopFoldersNote = "x: denied" };
        var ok = Result("D:\\", 40) with { Volume = new("D:\\", 40, 60, 100, 40) };
        using var output = new StringWriter();
        new SummaryRenderer().Render(output, new[] { crit, ok });
        Assert.Equal(string.Join(Environment.NewLine, new[] {
            "C:\\ used 50.0 B, free 50.0 B, total 100.0 B, 95.0% used [CRIT]",
            "    200.0 B  long", "    100.0 B  a", "     50.0 B  c", "    top folders incomplete: x: denied",
            "D:\\ used 40.0 B, free 60.0 B, total 100.0 B, 40.0% used",
            "2 volume(s): 1 at or above 90%, 0 at or above 85%", ""
        }), output.ToString());
    }

    [Fact]
    public void Test_SummaryJsonKeepsTopFolders()
    {
        var crit = Result("C:\\", 95) with { Level = FullnessLevel.Crit, Directories = new() { new("a", 3, 0), new("b", 2, 0), new("c", 1, 0) } };
        using var output = new StringWriter();
        new JsonRenderer(summary: true).Render(output, new[] { crit });
        using var doc = JsonDocument.Parse(output.ToString());
        var target = doc.RootElement.GetProperty("targets")[0];
        Assert.Equal(3, target.GetProperty("directories").GetArrayLength());
        Assert.Equal(0, target.GetProperty("files").GetArrayLength());
        Assert.False(target.TryGetProperty("topFoldersNote", out _));
    }
}
