using System.Text;
using Dirused.Common;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class ScheduledEngineTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private const string ScheduledStatus = "skipped: held by Tomcat85 — scheduled runs do not stop services";
    private const string Root = @"\\h\d$\Tomcat";
    private const string Install = @"D:\Tomcat\Tomcat";
    private const string HeldPath = @"\\h\d$\Tomcat\Tomcat\logs\Tomcat85-stderr.2026-11-04.log";

    [Fact]
    public void ParseAndReadAgreeForEveryVersionEncodingAndLineEnding()
    {
        foreach (int version in new[] { 1, 2, 3 })
        foreach (var variant in new[] { "utf8-crlf", "utf8bom-lf", "utf16-crlf", "utf16be-crlf", "utf8-cr" })
        {
            string text = ListText(version, "C:\\logs\\one.log");
            byte[] bytes = variant switch
            {
                "utf8-crlf" => new UTF8Encoding(false).GetBytes(text.Replace("\n", "\r\n")),
                "utf8bom-lf" => new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray(),
                "utf16-crlf" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text.Replace("\n", "\r\n"))).ToArray(),
                "utf16be-crlf" => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text.Replace("\n", "\r\n"))).ToArray(),
                _ => new UTF8Encoding(false).GetBytes(text.Replace("\n", "\r"))
            };
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(path, bytes);
                bool parseOk = PurgeList.Parse(bytes, out var parsed);
                bool readOk = PurgeList.Read(path, out var read);
                Assert.Equal(parseOk, readOk);
                Assert.True(parseOk);
                Assert.Single(parsed!.Files);
                Assert.Empty(parsed.Errors);
                AssertListsEqual(parsed, read!);
                Assert.Equal(HeaderFor(version), Header(bytes));
                Assert.Equal(@"C:\logs", parsed.Files.Single().ListTarget);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        string badText = PurgeList.HeaderV1 + "\n# purge days=7 minsize=1024\nmalformed\nC:\\logs\t00000000\t0000000000000001\t1\t2020-01-01T00:00:00.0000000Z\tbad.log\n";
        byte[] badBytes = Encoding.UTF8.GetBytes(badText);
        string badPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(badPath, badBytes);
            Assert.True(PurgeList.Parse(badBytes, out var parsed));
            Assert.True(PurgeList.Read(badPath, out var read));
            Assert.Equal(new[] { "expected 6 fields", "bad volume serial" }, parsed!.Errors.Select(x => x.Reason));
            AssertListsEqual(parsed, read!);
        }
        finally { File.Delete(badPath); }

        string oneLine = Path.GetTempFileName();
        string badHeader = Path.GetTempFileName();
        try
        {
            File.WriteAllText(oneLine, "only one line");
            File.WriteAllText(badHeader, "bad header\nsecond line\n");
            Assert.False(PurgeList.Parse(File.ReadAllBytes(oneLine), out _));
            Assert.False(PurgeList.Read(oneLine, out _));
            Assert.False(PurgeList.Parse(File.ReadAllBytes(badHeader), out _));
            Assert.False(PurgeList.Read(badHeader, out _));
            Assert.False(PurgeList.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), out _));
        }
        finally { File.Delete(oneLine); File.Delete(badHeader); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScheduledRunSkipsHeldTomcatWithoutControlCallsAndDeletesMatchingPeer(bool withControl)
    {
        var (result, held, peer) = ResultWithHeldAndPeer();
        var fs = new FakeFs();
        fs.Exists.Add(held.Path);
        fs.HeldProbes[held.Path] = true;
        fs.OpenErrors[held.Path] = new PurgeOpen("sharing violation", errorCode: 32);
        fs.Stats[held.Path] = Info(held);
        fs.HeldProbes[peer.Path] = false;
        fs.OpenInfos[peer.Path] = Info(peer);
        var control = new RecordingControl();
        var run = Apply(new[] { result }, fs, Services(), out var logs, control: withControl ? control : null, scheduled: true);
        Assert.Equal(ScheduledStatus, run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("deleted", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal(1, run.Skipped);
        Assert.DoesNotContain(logs, line => line.Contains(held.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(held.Path, fs.Exists);
        Assert.Contains(peer.Path, fs.Deleted);
        Assert.Equal(0, control.OpenCalls);
        Assert.Empty(control.Handle.Calls);
    }

    [Fact]
    public void ScheduledSkipPrecedesYesWindowAndEarlierFailureStillWins()
    {
        var (result, held, _) = ResultWithHeldAndPeer(onlyHeld: true);
        var fs = HeldFs(held);
        var scheduled = Apply(new[] { result }, fs, Services(), out _, scheduled: true, yes: true, localNow: new DateTime(2026, 11, 19, 14, 0, 0));
        Assert.Equal(ScheduledStatus, scheduled.Results[0].Largest!.Files[0].PurgeStatus);

        var (withPeer, queuedHeld, peer) = ResultWithHeldAndPeer();
        var failingLogFs = HeldFs(queuedHeld);
        failingLogFs.OpenInfos[peer.Path] = Info(peer);
        failingLogFs.HeldProbes[peer.Path] = false;
        var stopped = Apply(new[] { withPeer }, failingLogFs, Services(), out _, scheduled: true, log: _ => throw new IOException("log failed"));
        Assert.Equal("not attempted: log write failed", stopped.Results[0].Largest!.Files[0].PurgeStatus);
    }

    [Fact]
    public void ScheduledPreservesOtherHeldFailureAndSkipsRecentTomcatHeldRow()
    {
        string otherPath = @"\\h\d$\Tomcat\Tomcat\dump.hprof";
        var other = FileRow(otherPath) with { LastWriteUtc = Old };
        var otherResult = Result(other);
        var otherFs = new FakeFs();
        otherFs.Exists.Add(other.Path);
        otherFs.OpenErrors[other.Path] = new PurgeOpen("sharing violation", errorCode: 32);
        otherFs.HeldProbes[other.Path] = true;
        var otherRun = Apply(new[] { otherResult }, otherFs, Services(), out _, scheduled: true);
        Assert.Equal("failed: in use (heap dump, holder unknown)", otherRun.Results[0].Largest!.Files[0].PurgeStatus);

        var recent = FileRow(HeldPath) with { LastWriteUtc = Now };
        var recentResult = Result(recent);
        var recentFs = HeldFs(recent);
        var recentRun = Apply(new[] { recentResult }, recentFs, Services(), out _, scheduled: true);
        Assert.Equal(ScheduledStatus, recentRun.Results[0].Largest!.Files[0].PurgeStatus);
    }

    [Fact]
    public void ScheduledFalsePositiveControlStopsAndRestartsTomcat()
    {
        var held = FileRow(HeldPath) with { LastWriteUtc = Old };
        var result = Result(held);
        var fs = HeldFs(held);
        var control = new RecordingControl();
        var run = Apply(new[] { result }, fs, Services(), out _, control: control, scheduled: false, yes: true,
            localNow: new DateTime(2026, 11, 19, 22, 30, 0));
        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal(1, control.OpenCalls);
        Assert.Contains("Stop", control.Handle.Calls);
        Assert.Contains("Start", control.Handle.Calls);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("time")]
    [InlineData("id")]
    public void ScheduledListedFileIdentityAndMetadataMustStillMatch(string mismatch)
    {
        var original = FileRow(@"C:\logs\listed.log") with { FromList = true, ListTarget = @"C:\logs" };
        var result = ResultAt(@"C:\logs", original);
        var fs = new FakeFs();
        long length = mismatch == "size" ? original.LogicalBytes + 1 : original.LogicalBytes;
        DateTime time = mismatch == "time" ? Old.AddTicks(1) : Old;
        ulong id = mismatch == "id" ? original.FileId!.Value + 1 : original.FileId!.Value;
        fs.OpenInfos[original.Path] = new PurgeFileInfo(original.Path, length, time, false, false, 1) { VolumeSerial = original.VolumeSerial, FileIndex = id };
        fs.HeldProbes[original.Path] = false;
        var run = Apply(new[] { result }, fs, null, out _, scheduled: true);
        string part = mismatch == "id" ? "(id)" : "(size or time)";
        Assert.Equal($"skipped: file changed since list {part}", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.DoesNotContain(original.Path, fs.Deleted);

        var matchingFs = new FakeFs();
        matchingFs.Stats[original.Path] = Info(original);
        matchingFs.HeldProbes[original.Path] = false;
        matchingFs.OpenInfos[original.Path] = Info(original);
        var matching = Apply(new[] { result }, matchingFs, null, out _, scheduled: true);
        Assert.Equal("deleted", matching.Results[0].Largest!.Files[0].PurgeStatus);
    }

    private static string HeaderFor(int version) => version switch { 1 => PurgeList.HeaderV1, 2 => PurgeList.HeaderV2, _ => PurgeList.Header };

    private static string ListText(int version, string path)
    {
        string header = HeaderFor(version);
        string config = "# purge days=7 minsize=" + (version == 3 ? "1B" : "1") + "\n";
        string row = version switch
        {
            1 => $"C:\\logs\t12345678\t123456789ABCDEF0\t500\t{Old:O}\t{path}\n",
            2 => $"C:\\logs\t12345678\t123456789ABCDEF0\t500\t{Old:yyyy-MM-dd HH:mm:ss.fffffff zzz}\t{path}\n",
            _ => $"C:\\logs\t12345678\t123456789ABCDEF0\t500\t500B\t{Old:yyyy-MM-dd HH:mm:ss.fffffff zzz}\t{path}\n"
        };
        return header + "\n" + config + row;
    }

    private static string Header(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        return reader.ReadLine()!;
    }

    private static void AssertListsEqual(ParsedPurgeList expected, ParsedPurgeList actual)
    {
        Assert.Equal(expected.Days, actual.Days);
        Assert.Equal(expected.MinBytes, actual.MinBytes);
        Assert.Equal(expected.Errors, actual.Errors);
        Assert.Equal(expected.Files.Count, actual.Files.Count);
        for (int i = 0; i < expected.Files.Count; i++)
        {
            var a = expected.Files[i]; var b = actual.Files[i];
            Assert.Equal(a.Path, b.Path); Assert.Equal(a.LogicalBytes, b.LogicalBytes);
            Assert.Equal(a.LastWriteUtc, b.LastWriteUtc); Assert.Equal(a.VolumeSerial, b.VolumeSerial);
            Assert.Equal(a.FileId, b.FileId); Assert.Equal(a.FromList, b.FromList); Assert.Equal(a.ListTarget, b.ListTarget);
        }
    }

    private static LargestFile FileRow(string path) => new(path, 100, Old) { VolumeSerial = 0x12345678, FileId = 0x123456789ABCDEF0 };
    private static PurgeFileInfo Info(LargestFile file) => new(file.Path, file.LogicalBytes, file.LastWriteUtc!.Value, false, false, 1) { VolumeSerial = file.VolumeSerial, FileIndex = file.FileId };
    private static ScanResult Result(params LargestFile[] rows) => ResultAt(Root, rows);
    private static ScanResult ResultAt(string root, params LargestFile[] rows) => new(root, new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true) { ScanRoot = root, Largest = new LargestFiles(Array.Empty<string>(), rows.Length, rows.Sum(f => f.LogicalBytes), rows.ToList(), true) };

    private static (ScanResult Result, LargestFile Held, LargestFile Peer) ResultWithHeldAndPeer(bool onlyHeld = false)
    {
        var held = FileRow(HeldPath) with { LastWriteUtc = Old };
        if (onlyHeld) return (Result(held), held, held);
        var peer = FileRow(@"\\h\d$\Tomcat\Tomcat\logs\peer.log") with { LastWriteUtc = Old, FileId = 0x223456789ABCDEF0 };
        return (Result(held, peer), held, peer);
    }

    private static IReadOnlyList<ScanResult> One(ScanResult result) => new[] { result };
    private static IServiceQuery Services() => new FakeServices();
    private static FakeFs HeldFs(LargestFile file)
    {
        var fs = new FakeFs(); fs.Exists.Add(file.Path); fs.HeldProbes[file.Path] = true;
        fs.OpenErrors[file.Path] = new PurgeOpen("sharing violation", errorCode: 32);
        fs.Stats[file.Path] = Info(file); fs.OpenInfos[file.Path] = Info(file); return fs;
    }

    private static PurgeRun Apply(IReadOnlyList<ScanResult> results, FakeFs fs, IServiceQuery? services, out List<string> logLines,
        RecordingControl? control = null, bool scheduled = false, bool yes = false, DateTime? localNow = null,
        Action<string>? log = null)
    {
        logLines = new List<string>();
        var capturedLogs = logLines;
        return Purge.Apply(results, 1, 1, true, fs, () => Now, line => { capturedLogs.Add(line); log?.Invoke(line); }, CancellationToken.None,
            services: services, control: control, localNow: () => localNow ?? new DateTime(2026, 11, 19, 22, 30, 0),
            sleep: _ => { }, input: new StringReader(""), inputRedirected: true, yes: yes, error: TextWriter.Null, scheduled: scheduled);
    }

    private sealed class FakeServices : IServiceQuery
    {
        public IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error)
        { error = null; return host.Equals("h", StringComparison.OrdinalIgnoreCase) ? new[] { new ServiceEntry("Tomcat85", "Tomcat85", Install, true) } : Array.Empty<ServiceEntry>(); }
    }

    private sealed class FakeFs : IPurgeFileSystem
    {
        public Dictionary<string, bool?> HeldProbes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeFileInfo> Stats { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeFileInfo> OpenInfos { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeOpen> OpenErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Exists { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ResolveRoot(string path, out string? error) { error = null; return path; }
        public PurgeOpen Open(string path)
        {
            if (Deleted.Contains(path)) return new PurgeOpen("not found", notFound: true);
            if (OpenErrors.Remove(path, out var error)) return error;
            if (!OpenInfos.TryGetValue(path, out var info)) return new PurgeOpen("missing", true);
            Exists.Add(path);
            return new PurgeOpen(info, new Disposable(), () => { Deleted.Add(path); Exists.Remove(path); });
        }
        public PurgeFileInfo? Stat(string path, out string? error, out bool notFound)
        { error = null; notFound = !Stats.TryGetValue(path, out var info); return info; }
        public bool? ProbeHeld(string path, out string? error) { error = null; return HeldProbes.GetValueOrDefault(path, false); }
        private sealed class Disposable : IDisposable { public void Dispose() { } }
    }

    private sealed class RecordingControl : IServiceControl
    {
        public int OpenCalls { get; private set; }
        public RecordingHandle Handle { get; } = new();
        public IServiceHandle? Open(string host, string key, out string? error) { OpenCalls++; error = null; return Handle; }
    }

    private sealed class RecordingHandle : IServiceHandle
    {
        public List<string> Calls { get; } = new();
        private ServiceState state = ServiceState.Running;
        public IReadOnlyList<string>? RunningDependents(out string? error) { error = null; return Array.Empty<string>(); }
        public (bool Ok, int Win32Error) Stop() { Calls.Add("Stop"); state = ServiceState.Stopped; return (true, 0); }
        public (bool Ok, int Win32Error) Start() { Calls.Add("Start"); state = ServiceState.Running; return (true, 0); }
        public (ServiceState State, string? Error) QueryState() { Calls.Add("QueryState"); return (state, null); }
        public void Dispose() { }
    }
}
