using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dirused.Common.Update;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class UpdateTests
{
    [Fact] public void U1_NewestNumericPrereleaseWithMatchingAssetsWins()
    {
        var releases = new[] { Release("v0.0.9", "win-x64"), Release("v0.0.10", "win-arm64"), Release("v0.0.11", "win-x64", draft: true), Release("v0.0.12", "win-x64", hash: false), Release("v0.0.10", "win-x64") };
        var picked = ReleaseSelector.Select(releases, "win-x64");
        Assert.Equal("0.0.10", picked?.Version); Assert.Equal("win-x64", picked?.Rid);
        Assert.Equal("0.0.9", ReleaseSelector.Select(new[] { Release("v0.0.9", "win-x64") }, "win-x64")?.Version); // positive control: prereleases are eligible
    }

    [Fact] public async Task U2_NoticeIsStderrOnlyOncePerDayAndPositiveControlShowsIt()
    {
        var scratch = Scratch(); try { var source = new FakeSource { Package = Package() }; var error = new StringWriter(); var stdout = new StringWriter(); var now = DateTimeOffset.UtcNow; var check = Checker(source, error, scratch, () => now);
            await check.CheckAsync(); Assert.Equal(1, source.FindCalls); Assert.Empty(error.ToString()); check.WaitAfterReport(Task.CompletedTask, TimeSpan.FromSeconds(1)); Assert.Contains("dirused 0.0.11 is available", error.ToString()); Assert.Empty(stdout.ToString());
            error.GetStringBuilder().Clear(); await check.CheckAsync(); check.WaitAfterReport(Task.CompletedTask, TimeSpan.FromSeconds(1)); Assert.Equal(1, source.FindCalls); Assert.Empty(error.ToString());
            now = now.AddHours(24).AddSeconds(1); await check.CheckAsync(); check.WaitAfterReport(Task.CompletedTask, TimeSpan.FromSeconds(1)); Assert.Equal(2, source.FindCalls); Assert.Contains("dirused 0.0.11 is available", error.ToString());
            var off = new StringWriter(); var noCheck = Checker(new FakeSource { Package = Package() }, off, Path.Combine(scratch, "off"), () => now); Environment.SetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK", "1"); try { await noCheck.CheckAsync(); } finally { Environment.SetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK", null); }
            Assert.Empty(off.ToString());
        } finally { Directory.Delete(scratch, true); }
    }

    [Fact] public async Task U3_FailedCheckIsSilentAndPreservesLatest()
    {
        var scratch = Scratch(); try { var path = Path.Combine(scratch, "state.json"); new UpdateState(DateTimeOffset.UtcNow.AddDays(-2), "0.0.11", DateTimeOffset.UtcNow).Write(path); var source = new FakeSource { Failure = new HttpRequestException("407") }; var error = new StringWriter(); await Checker(source, error, scratch).CheckAsync(); var state = UpdateState.Read(path, DateTimeOffset.UtcNow); Assert.Equal(1, source.FindCalls); Assert.Equal("0.0.11", state.Latest); Assert.NotNull(state.AttemptedUtc); Assert.Empty(error.ToString()); } finally { Directory.Delete(scratch, true); }
    }

    [Fact] public async Task U4_OptOutAndPositiveControl()
    {
        var scratch = Scratch(); try { var source = new FakeSource { Package = Package() }; var error = new StringWriter();
            var parser = Dirused.Common.ArgParser.Parse(new[] { "C:\\", "/noupdatecheck" }); Assert.Null(parser.Error); Assert.NotNull(parser.Options);
            var off = Checker(source, error, Path.Combine(scratch, "off")); Environment.SetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK", "1"); try { await off.CheckAsync(); } finally { Environment.SetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK", null); }
            Assert.Equal(0, source.FindCalls); await Checker(source, error, Path.Combine(scratch, "on")).CheckAsync(); Assert.Equal(1, source.FindCalls);
        } finally { Directory.Delete(scratch, true); }
    }

    [Theory] [InlineData("0.0.0-dev")] [InlineData("banana")]
    public async Task U5_DevAndInvalidVersionsNeverCheckAndUpdateRefuses(string version)
    {
        var scratch = Scratch(); try { var source = new FakeSource { Package = Package() }; var error = new StringWriter(); await Checker(source, error, scratch, version: version).CheckAsync(); Assert.Equal(0, source.FindCalls); var rc = await SelfUpdater.RunAsync(source, version, "win-x64", Path.Combine(scratch, "dirused.exe"), new StringWriter(), error); Assert.Equal(1, rc); Assert.Equal(0, source.FindCalls); } finally { Directory.Delete(scratch, true); }
    }

    [Fact] public async Task U6_UpdateReplacesExeAndNextStartCleansOld()
    {
        var scratch = Scratch(); try { var exe = Path.Combine(scratch, "dirused.exe"); File.WriteAllBytes(exe, new byte[] { 1, 2, 3 }); Assert.Equal(scratch, Path.GetDirectoryName(exe)); var source = NewSource(Zip(("dirused.exe", new byte[] { 9, 8 }))); var rc = await SelfUpdater.RunAsync(source, "0.0.10", "win-x64", exe, new StringWriter(), new StringWriter()); Assert.Equal(0, rc); Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(exe)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(exe + ".old")); SelfUpdater.Cleanup(exe); Assert.False(File.Exists(exe + ".old")); Assert.False(File.Exists(Path.Combine(scratch, "dirused.update.lock"))); } finally { Directory.Delete(scratch, true); }
    }

    [Theory]
    [InlineData("hash")] [InlineData("filename")] [InlineData("missing")] [InlineData("no-root")] [InlineData("duplicate")] [InlineData("traversal")] [InlineData("oversize")] [InlineData("host")]
    public async Task U7_FailedDownloadLeavesExeByteIdenticalAndRemovesStage(string fault)
    {
        var scratch = Scratch(); try { var exe = Path.Combine(scratch, "dirused.exe"); var original = new byte[] { 3, 4, 5 }; File.WriteAllBytes(exe, original); var source = NewSource(fault switch { "no-root" => Zip(("folder/dirused.exe", new byte[] { 1 })), "duplicate" => Zip(("dirused.exe", new byte[] { 1 }), ("dirused.exe", new byte[] { 2 })), "traversal" => Zip(("../escape", new byte[] { 1 }), ("dirused.exe", new byte[] { 1 })), "oversize" => new byte[100_000_001], _ => Zip(("dirused.exe", new byte[] { 1 })) }); if (fault == "missing") source.Package = null; if (fault == "host") source.Package = Package("https://evil.example/x.zip"); if (fault == "hash") source.HashText = new string('0', 64) + "  " + source.Package!.ZipName; if (fault == "filename") source.HashText = new string('0', 64) + "  wrong.zip";
            var error = new StringWriter(); var rc = await SelfUpdater.RunAsync(source, "0.0.10", "win-x64", exe, new StringWriter(), error); Assert.Equal(1, rc); Assert.Equal(original, File.ReadAllBytes(exe)); Assert.False(Directory.Exists(Path.Combine(scratch, ".dirused-update")));
            var upToDate = NewSource(Array.Empty<byte>()); upToDate.Package = Package(version: "0.0.10"); var upToDateRc = await SelfUpdater.RunAsync(upToDate, "0.0.10", "win-x64", exe, new StringWriter(), new StringWriter()); Assert.Equal(0, upToDateRc); Assert.Equal(0, upToDate.DownloadCalls);
        } finally { Directory.Delete(scratch, true); }
    }

    [Fact] public void U8_Sha256ParserHandlesCrLfAndRejectsBadTokens()
    { var hash = new string('a', 64); Assert.Equal(hash, Sha256File.Parse(hash + "  x.zip\r\n", "x.zip")); Assert.Throws<InvalidDataException>(() => Sha256File.Parse(new string('a', 63) + " x.zip", "x.zip")); Assert.Throws<InvalidDataException>(() => Sha256File.Parse(new string('z', 64) + " x.zip", "x.zip")); }

    [Fact] public void U9_UpdateAndVersionMustBeStandalone()
    { Assert.NotNull(Dirused.Common.ArgParser.Parse(new[] { "C:\\", "/update" }).Error); Assert.NotNull(Dirused.Common.ArgParser.Parse(new[] { "/update", "/help" }).Error); Assert.NotNull(Dirused.Common.ArgParser.Parse(new[] { "C:\\", "/version" }).Error); }

    [Fact] public void U10_InvalidTagsAreIgnored()
    { var good = Release("v0.0.11", "win-x64"); var picked = ReleaseSelector.Select(new[] { good, Release("v0.1.0-rc1", "win-x64"), Release("latest", "win-x64"), Release("0.0.12", "win-x64") }, "win-x64"); Assert.Equal("v0.0.11", picked?.Tag); }

    [Fact] public async Task U11_NeverReturningSourceDoesNotHoldScanBeyondGrace()
    { var scratch = Scratch(); try { var error = new StringWriter(); var stdout = new StringWriter(); var source = new FakeSource { Never = true }; var check = Checker(source, error, scratch); var sw = System.Diagnostics.Stopwatch.StartNew(); var inFlight = check.CheckAsync(); await Task.Run(() => check.WaitAfterReport(inFlight, TimeSpan.FromSeconds(1))).WaitAsync(TimeSpan.FromSeconds(1.5)); sw.Stop(); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5)); Assert.Empty(error.ToString()); Assert.Empty(stdout.ToString()); } finally { Directory.Delete(scratch, true); } }

    [Fact] public async Task U12_ScanCheckListsReleaseButNeverDownloadsAssets()
    { var scratch = Scratch(); try { var source = new FakeSource { Package = Package() }; await Checker(source, new StringWriter(), scratch).CheckAsync(); Assert.Equal(1, source.FindCalls); Assert.Equal(0, source.DownloadCalls); } finally { Directory.Delete(scratch, true); } }

    [Fact] public async Task U13_FileFaultsAndHeldLockPreserveOrRecoverExecutable()
    {
        var scratch = Scratch(); try { var exe = Path.Combine(scratch, "dirused.exe"); var original = new byte[] { 2, 4 }; File.WriteAllBytes(exe, original); var old = exe + ".old"; File.WriteAllText(old, "locked-old"); var ops = new FaultFileOperations { FailDeleteOld = true }; Assert.Equal(1, await SelfUpdater.RunAsync(NewSource(Zip(("dirused.exe", new byte[] { 9 }))), "0.0.10", "win-x64", exe, new StringWriter(), new StringWriter(), ops)); Assert.Equal(original, File.ReadAllBytes(exe));
            File.Delete(old); ops = new FaultFileOperations { FailInstallMove = true }; Assert.Equal(1, await SelfUpdater.RunAsync(NewSource(Zip(("dirused.exe", new byte[] { 9 }))), "0.0.10", "win-x64", exe, new StringWriter(), new StringWriter(), ops)); Assert.Equal(original, File.ReadAllBytes(exe)); Assert.False(File.Exists(old));
            ops = new FaultFileOperations { FailInstallMove = true, FailRestoreMove = true }; var error = new StringWriter(); Assert.Equal(1, await SelfUpdater.RunAsync(NewSource(Zip(("dirused.exe", new byte[] { 9 }))), "0.0.10", "win-x64", exe, new StringWriter(), error, ops)); Assert.Contains(old, error.ToString());
            File.WriteAllBytes(exe, original); using var held = new FileStream(Path.Combine(scratch, "dirused.update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); Assert.Equal(1, await SelfUpdater.RunAsync(NewSource(Zip(("dirused.exe", new byte[] { 9 }))), "0.0.10", "win-x64", exe, new StringWriter(), new StringWriter())); Assert.Equal(original, File.ReadAllBytes(exe));
        } finally { Directory.Delete(scratch, true); }
    }

    [Fact] public async Task U14_CorruptFutureAndConcurrentStateFilesAreTreatedAsUnchecked()
    { var scratch = Scratch(); try { var path = Path.Combine(scratch, "state.json"); File.WriteAllText(path, "{"); var src = new FakeSource { Package = null }; await Checker(src, new StringWriter(), scratch).CheckAsync(); Assert.Equal(1, src.FindCalls); new UpdateState(DateTimeOffset.UtcNow.AddDays(2), null, null).Write(path); await Checker(src, new StringWriter(), scratch).CheckAsync(); Assert.Equal(2, src.FindCalls);
        var successfulWrites = 0;
        var writer = Task.Run(() => { for (var i = 0; i < 20; i++) { try { new UpdateState(DateTimeOffset.UtcNow, null, null).Write(path); Interlocked.Increment(ref successfulWrites); } catch (IOException) { } catch (UnauthorizedAccessException) { } } });
        var readers = Enumerable.Range(0, 20).Select(_ => Task.Run(() => UpdateState.Read(path, DateTimeOffset.UtcNow))).ToArray(); await writer; await Task.WhenAll(readers);
        Assert.True(successfulWrites > 0); Assert.NotNull(UpdateState.Read(path, DateTimeOffset.UtcNow)); Assert.Empty(Directory.GetFiles(scratch, "*.tmp-*"));
      } finally { Directory.Delete(scratch, true); } }

    [Fact] public void U16_OnlyPrefixedWordsAreFlags()
    {
        foreach (var flag in new[] { "/update", "-update", "/UPDATE" }) Assert.True(Dirused.Common.ArgParser.IsFlag(flag, "update"));
        foreach (var target in new[] { "update", "//update", "C:\\update", "/updates" }) Assert.False(Dirused.Common.ArgParser.IsFlag(target, "update"));
    }

    [Fact] public async Task U15_HeldLockLeavesOtherUpdatersFilesAlone()
    {
        var scratch = Scratch(); try
        {
            var exe = Path.Combine(scratch, "dirused.exe"); var original = new byte[] { 1, 2, 3 }; File.WriteAllBytes(exe, original);
            var stage = Path.Combine(scratch, ".dirused-update"); Directory.CreateDirectory(stage); var staged = Path.Combine(stage, "dirused.exe"); File.WriteAllText(staged, "other updater");
            var lockPath = Path.Combine(scratch, "dirused.update.lock"); using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var error = new StringWriter(); Assert.Equal(1, await SelfUpdater.RunAsync(NewSource(Zip(("dirused.exe", new byte[] { 9 }))), "0.0.10", "win-x64", exe, new StringWriter(), error));
                Assert.Contains("Another update is running.", error.ToString()); Assert.True(File.Exists(staged)); Assert.True(File.Exists(lockPath)); Assert.Equal(original, File.ReadAllBytes(exe));
            }
        } finally { Directory.Delete(scratch, true); }
    }

    [Fact] public async Task U17_NoticeIsWrittenOnlyAfterWaitAndNeverAfterGrace()
    {
        var scratch = Scratch(); try
        {
            var error = new StringWriter(); var immediate = new FakeSource { Package = Package() }; var check = Checker(immediate, error, scratch);
            var completed = check.CheckAsync(); await completed; Assert.Empty(error.ToString()); check.WaitAfterReport(completed, TimeSpan.FromSeconds(1)); Assert.Contains("dirused 0.0.11 is available", error.ToString());
            error.GetStringBuilder().Clear(); var late = new DelayedSource(Package(), TimeSpan.FromSeconds(1.5)); var lateCheck = Checker(late, error, Path.Combine(scratch, "late"));
            var pending = lateCheck.CheckAsync(); lateCheck.WaitAfterReport(pending, TimeSpan.FromSeconds(1)); await Task.Delay(TimeSpan.FromSeconds(1)); Assert.Empty(error.ToString());
        } finally { Directory.Delete(scratch, true); }
    }

    private static ReleaseInfo Release(string tag, string rid, bool draft = false, bool hash = true) { var zip = $"dirused-{tag}-{rid}.zip"; var assets = new List<ReleaseAsset> { new(zip, new Uri($"https://github.com/wilddog64/dirused/releases/download/{tag}/{zip}")) }; if (hash) assets.Add(new(zip + ".sha256", new Uri($"https://github.com/wilddog64/dirused/releases/download/{tag}/{zip}.sha256"))); return new(tag, draft, assets); }
    private static string Scratch() { var path = Path.Combine(Path.GetTempPath(), "dirused-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); Assert.Equal(path, Path.GetFullPath(path)); return path; }
    private static UpdateCheck Checker(IReleaseSource source, TextWriter error, string dir, Func<DateTimeOffset>? clock = null, string version = "0.0.10") => new(source, error, Path.Combine(dir, "state.json"), version, "win-x64", clock);
    private static ReleasePackage Package(string? url = null, string version = "0.0.11") { var tag = "v" + version; var zip = $"dirused-{tag}-win-x64.zip"; return new(version, tag, "win-x64", new Uri(url ?? $"https://github.com/wilddog64/dirused/releases/download/{tag}/{zip}"), new Uri($"https://github.com/wilddog64/dirused/releases/download/{tag}/{zip}.sha256"), zip); }
    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries) { using var ms = new MemoryStream(); using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) foreach (var (name, bytes) in entries) { using var s = z.CreateEntry(name).Open(); s.Write(bytes); } return ms.ToArray(); }
    private static FakeSource NewSource(byte[] zip) { var s = new FakeSource { Package = Package(), Zip = zip }; s.HashText = Sha256File.Hash(zip) + "  " + s.Package.ZipName + "\r\n"; return s; }
    private sealed class FakeSource : IReleaseSource
    {
        public int FindCalls, DownloadCalls; public bool Never; public Exception? Failure; public ReleasePackage? Package = Package(); public byte[] Zip = Array.Empty<byte>(); public string HashText = "";
        public Task<ReleasePackage?> FindLatestAsync(string currentVersion, string rid, CancellationToken cancellationToken = default) { FindCalls++; if (Never) return new Task<ReleasePackage?>(() => null); if (Failure != null) throw Failure; return Task.FromResult(Package); }
        public Task<byte[]> DownloadAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default) { DownloadCalls++; if (uri.AbsolutePath.EndsWith(".sha256")) return Task.FromResult(Encoding.UTF8.GetBytes(HashText)); return Task.FromResult(Zip); }
    }
    private sealed class DelayedSource(ReleasePackage package, TimeSpan delay) : IReleaseSource
    {
        public async Task<ReleasePackage?> FindLatestAsync(string currentVersion, string rid, CancellationToken cancellationToken = default) { await Task.Delay(delay, cancellationToken); return package; }
        public Task<byte[]> DownloadAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FaultFileOperations : IUpdateFileOperations
    {
        public bool FailDeleteOld, FailInstallMove, FailRestoreMove; public bool FileExists(string p) => File.Exists(p); public bool DirectoryExists(string p) => Directory.Exists(p); public void CreateDirectory(string p) => Directory.CreateDirectory(p);
        public void DeleteFile(string p) { if (FailDeleteOld && p.EndsWith(".old")) throw new IOException("locked"); File.Delete(p); }
        public void DeleteDirectory(string p, bool r) => Directory.Delete(p, r); public Stream CreateFile(string p) => File.Create(p);
        public void MoveFile(string s, string d) { if (FailInstallMove && s.EndsWith(Path.Combine(".dirused-update", "dirused.exe"))) throw new IOException("install move failed"); if (FailRestoreMove && s.EndsWith(".old")) throw new IOException("restore move failed"); File.Move(s, d); }
    }
}
