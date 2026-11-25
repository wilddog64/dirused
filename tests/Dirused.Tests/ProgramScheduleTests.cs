using System.Text;
using Dirused.Cli;
using Dirused.Common;
using Dirused.Common.Update;
using Dirused.Tests.Portable;
using Xunit;

namespace Dirused.Tests;

public sealed class ProgramScheduleTests
{
    private const string PlanRoot = @"C:\Users\op\AppData\Local\dirused\plans";
    private const string Exe = @"C:\Program Files\dirused\dirused.exe";
    private const string FilePath = @"\\h\d$\Tomcat\Tomcat\logs\Tomcat85-stderr.2026-11-04.log";
    private const string Target = @"\\h\d$\Tomcat";
    private static readonly DateTimeOffset InitialNow = new(2026, 11, 23, 21, 0, 0, TimeSpan.FromHours(-8));
    private static readonly DateTimeOffset RunNow = new(2026, 11, 23, 22, 30, 0, TimeSpan.FromHours(-8));
    private static readonly DateTime OldWrite = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void B3dM1_SchedulesRunsListsCancelsAndDispatchesTrustForms()
    {
        using var temp = new TempDir();
        string listPath = temp.PathFor("list.txt");
        File.WriteAllBytes(listPath, MakeList());
        var rig = new Rig(temp.PathFor("purge.log"));
        var seams = rig.ProgramSeams();

        var result = Invoke(new[] { "/fromlist", listPath, "/purge", "/schedule", "22:30", "/confirm", "/yes" }, seams);
        Assert.True(result.Code == 0, result.Err);
        string id = Assert.Single(rig.Scheduler.Registered).Expected.Id;
        Assert.Contains("Scheduled " + id + " for ", result.Out);
        Assert.DoesNotContain("is not available in this version", result.Out + result.Err);

        rig.Now = RunNow;
        rig.Fs.SetHeld(false);
        var planSchedule = rig.Schedule with { ExePath = PlanRoot + "\\" + id + "\\dirused.exe" };
        var runSeams = rig.ProgramSeams() with { Schedule = planSchedule };
        result = Invoke(new[] { "/runplan", id }, runSeams);
        Assert.True(result.Code == 0, result.Err);
        Assert.StartsWith("run 2026-11-23T22:30:00-08:00 pid ", Encoding.UTF8.GetString(rig.Store.ReadAll(PlanRoot + "\\" + id + "\\claim")!));

        rig.Now = InitialNow;
        result = Invoke(new[] { "/fromlist", listPath, "/purge", "/schedule", "22:30", "/confirm", "/yes" }, seams);
        Assert.True(result.Code == 0, result.Err);
        string secondId = rig.Scheduler.Registered.Last().Expected.Id;
        result = Invoke(new[] { "/schedule", "/list" }, seams);
        Assert.True(result.Code == 0, result.Err);
        Assert.Contains(secondId, result.Out);
        result = Invoke(new[] { "/schedule", "/cancel", secondId }, seams);
        Assert.True(result.Code == 0, result.Err);
        Assert.Equal($"Cancelled {secondId}.\n", result.Out);
        result = Invoke(new[] { "/schedule", "/trust" }, seams);
        Assert.True(result.Code == 0, result.Err);
        Assert.Equal("No trusted accounts.\n", result.Out);
        result = Invoke(new[] { "/schedule", "/untrust", "x" }, seams);
        Assert.True(result.Code == 1, result.Err);
        Assert.Equal("x is not trusted.\n", result.Err);
        Assert.DoesNotContain("is not available in this version", result.Out + result.Err);
        Assert.Equal(0, rig.Release.Calls);
    }

    [Theory]
    [InlineData("/runplan x")]
    [InlineData("/schedule /list")]
    [InlineData("/schedule /cancel x")]
    [InlineData("/schedule /trust")]
    [InlineData("/schedule /untrust x")]
    public void B3dM2_FormsNeedWindowsWithoutSeams(string form)
    {
        var seams = NoWindows();
        var result = Invoke(form.Split(' '), seams);
        Assert.True(result.Code == 1, result.Err);
        Assert.Empty(result.Out);
        Assert.Equal("/schedule needs Windows\n", result.Err);
    }

    [Fact]
    public void B3dM2_ConfirmedFromListNeedsWindowsAndPreviewRemainsAvailable()
    {
        using var temp = new TempDir();
        string listPath = temp.PathFor("list.txt");
        File.WriteAllBytes(listPath, MakeList());
        var seams = NoWindows();
        var result = Invoke(new[] { "/fromlist", listPath, "/purge", "/schedule", "22:30", "/confirm" }, seams);
        Assert.True(result.Code == 1, result.Err);
        Assert.Empty(result.Out);
        Assert.Equal("/schedule needs Windows\n", result.Err);

        result = Invoke(new[] { "/fromlist", listPath, "/purge", "/schedule", "22:30", "PT" }, seams);
        Assert.True(result.Code == 0, result.Err);
        Assert.Contains("Scheduled purge preview", result.Out);
        Assert.Contains("Preview only. Add /confirm to schedule it.", result.Out);
    }

    [Fact]
    public void B3dM3_FromListCannotBePurgeLogAndDoesNotRegister()
    {
        using var temp = new TempDir();
        string listPath = temp.PathFor("list.txt");
        File.WriteAllBytes(listPath, MakeList());
        var rig = new Rig(temp.PathFor("purge.log"));
        var seams = rig.ProgramSeams() with { PurgeLogPath = listPath };
        var result = Invoke(new[] { "/fromlist", listPath, "/purge", "/schedule", "22:30", "/confirm", "/yes" }, seams);
        Assert.True(result.Code == 1, result.Err);
        Assert.Equal("/fromlist cannot be the purge log\n", result.Err);
        Assert.Empty(rig.Scheduler.Registered);
    }

    [Fact]
    public void FromListCannotBeOutputKeepsExistingRefusal()
    {
        using var temp = new TempDir();
        string listPath = temp.PathFor("list.txt");
        File.WriteAllBytes(listPath, MakeList());
        var rig = new Rig(temp.PathFor("purge.log"));
        var result = Invoke(new[] { "/fromlist", listPath, "/purge", "/output", listPath }, rig.ProgramSeams());
        Assert.True(result.Code == 1, result.Err);
        Assert.Equal("/fromlist cannot be the output or purge log\n", result.Err);
        Assert.Empty(rig.Scheduler.Registered);
    }

    [Fact]
    public void B3dM4_ScheduleFormsSkipCleanupAndVersionCallsIt()
    {
        using var temp = new TempDir();
        string listPath = temp.PathFor("list.txt");
        File.WriteAllBytes(listPath, MakeList());
        string[][] forms =
        [
            ["/runplan", "x"], ["/schedule", "/list"],
            ["/fromlist", listPath, "/purge", "/schedule", "22:30"]
        ];
        foreach (string[] form in forms)
        {
            int cleanupCalls = 0;
            var rig = new Rig(temp.PathFor("purge-" + Guid.NewGuid().ToString("N") + ".log"));
            Invoke(form, rig.ProgramSeams(cleanup: () => cleanupCalls++));
            Assert.Equal(0, cleanupCalls);
        }
        int versionCleanupCalls = 0;
        var positive = new Rig(temp.PathFor("version-purge.log"));
        var result = Invoke(new[] { "/version" }, positive.ProgramSeams(cleanup: () => versionCleanupCalls++));
        Assert.True(result.Code == 0, result.Err);
        Assert.Equal(1, versionCleanupCalls);
    }

    [Fact]
    public void B3dN1_RunPlanNeverCallsControlButAttendedPurgeDoes()
    {
        using var temp = new TempDir();
        string listPath = temp.PathFor("list.txt");
        File.WriteAllBytes(listPath, MakeList());
        var rig = new Rig(temp.PathFor("purge.log"), held: true);
        var seams = rig.ProgramSeams();
        var scheduled = Invoke(new[] { "/fromlist", listPath, "/purge", "/schedule", "22:30", "/confirm", "/yes" }, seams);
        Assert.True(scheduled.Code == 0, scheduled.Err);
        string id = Assert.Single(rig.Scheduler.Registered).Expected.Id;
        rig.Now = RunNow;
        var planSeams = rig.ProgramSeams() with { Schedule = rig.Schedule with { ExePath = PlanRoot + "\\" + id + "\\dirused.exe" } };
        Invoke(new[] { "/runplan", id }, planSeams);
        Assert.Empty(rig.Control.Actions);

        rig.Fs.SetHeld(true);
        rig.Fs.OnStop = () => rig.Fs.SetHeld(false);
        rig.Fs.OnStart = () => rig.Fs.SetHeld(true);
        var attendedSeams = rig.ProgramSeams() with
        {
            LocalNow = () => new DateTime(2026, 11, 23, 22, 30, 0),
            Sleep = _ => { },
            Control = rig.Control
        };
        var attended = Invoke(new[] { "/fromlist", listPath, "/purge", "/confirm", "/yes" }, attendedSeams);
        Assert.Contains("Stop", rig.Control.Actions);
        Assert.Contains("Start", rig.Control.Actions);
        Assert.True(attended.Code is 0 or 3, attended.Err);
    }

    [Fact]
    public void B3dN2_ScheduleFormsSkipReleaseSourceAndUsageForcesIt()
    {
        using var temp = new TempDir();
        string listPath = temp.PathFor("list.txt");
        File.WriteAllBytes(listPath, MakeList());
        string[][] forms =
        [
            ["/fromlist", listPath, "/purge", "/schedule", "22:30", "/confirm", "/yes"],
            ["/runplan", "x"], ["/schedule", "/list"], ["/schedule", "/cancel", "x"],
            ["/schedule", "/trust"], ["/schedule", "/untrust", "x"]
        ];
        foreach (string[] form in forms)
        {
            var rig = new Rig(temp.PathFor("purge-" + Guid.NewGuid().ToString("N") + ".log"));
            Invoke(form, rig.ProgramSeams());
            Assert.Equal(0, rig.Release.Calls);
        }
        var positive = new Rig(temp.PathFor("usage-purge.log"));
            Invoke(new[] { "/?" }, positive.ProgramSeams(updateStatePath: temp.PathFor("update-state.json")));
        Assert.True(positive.Release.Calls > 0);
    }

    private static ProgramTestSeams NoWindows() => new(null, null, false, null, "1.2.3", "purge.log");
    private static (int Code, string Out, string Err) Invoke(string[] args, ProgramTestSeams seams)
    {
        using var output = new StringWriter { NewLine = "\n" };
        using var error = new StringWriter { NewLine = "\n" };
        int code = Program.RunWithSchedule(args, output, error, seams);
        return (code, output.ToString(), error.ToString());
    }

    private static byte[] MakeList()
    {
        string text = PurgeList.Header + "\n# purge days=7 minsize=1B\n" +
            $"{Target}\t12345678\t2000000000000001\t400\t400B\t2026-01-01 00:00:00.0000000 +00:00\t{FilePath}\n";
        return Encoding.UTF8.GetBytes(text);
    }

    private sealed class Rig
    {
        public DateTimeOffset Now = InitialNow;
        public readonly MemoryStore Store = new();
        public readonly MemorySecurity Security = new();
        public readonly RecordingScheduler Scheduler = new();
        public readonly FakeTrustStore Trust = new(new List<string>());
        public readonly FakeAccountResolver Accounts = new(new List<string>());
        public readonly RecordingRelease Release = new();
        public readonly HeldFs Fs;
        public readonly RecordingControl Control;
        public readonly ScheduleSeams Schedule;
        public readonly string PurgeLogPath;

        public Rig(string purgeLogPath, bool held = false)
        {
            PurgeLogPath = purgeLogPath;
            Store.Files[Exe] = [1, 2, 3, 4, 5];
            Fs = new HeldFs(held);
            Control = new RecordingControl(Fs);
            Schedule = new(() => Now, Pacific(), PlanRoot, ScheduleFakes.UserSid, Exe, "1.2.3", Suffixes.Next,
                TokenLevel.Medium, Store, Security, Scheduler, Trust, Accounts, _ => null);
        }

        public ProgramTestSeams ProgramSeams(Action? cleanup = null, bool windows = true, string? updateStatePath = null) => new(Schedule,
            new RunPlanSeams(Fs, new TomcatServices(), null, () => Now.UtcDateTime, 123, PurgeLogPath, _ => { }),
            windows, Release, "1.2.3", PurgeLogPath, Fs, new TomcatServices(), Control, TextReader.Null, true,
            () => new DateTime(2026, 11, 23, 22, 30, 0), _ => { }, () => Now.UtcDateTime, UpdateStatePath: updateStatePath, Cleanup: cleanup);
    }

    private static TimeZoneInfo Pacific() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneInfo.TryConvertIanaIdToWindowsId("America/Los_Angeles", out string? win) ? win : "America/Los_Angeles");

    private static class Suffixes
    {
        private static int value;
        public static int Next() => Interlocked.Increment(ref value);
    }

    private sealed class MemoryStore : IPlanStore
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => Files.ContainsKey(path) || Dirs.Contains(path);
        public bool IsReparsePoint(string path) => false;
        public SeamResult CreateDirectory(string path) { if (!Dirs.Add(path)) return new(false, "exists"); return new(true); }
        public SeamResult WriteNew(string path, byte[] bytes) { if (Files.ContainsKey(path)) return new(false, "exists"); Files[path] = bytes; return new(true); }
        public SeamResult MoveNew(string from, string to) { if (!Files.Remove(from, out var bytes) || Files.ContainsKey(to)) return new(false, "bad move"); Files[to] = bytes; return new(true); }
        public byte[]? ReadAll(string path) => Files.TryGetValue(path, out var bytes) ? bytes : null;
        public IReadOnlyList<string> ListDirectoryNames(string path) => Dirs.Where(d => d.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && d.IndexOf('\\', path.Length + 1) < 0).Select(d => d[(path.Length + 1)..]).ToArray();
        public SeamResult DeleteTree(string path) { Dirs.Remove(path); foreach (string child in Files.Keys.Where(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) Files.Remove(child); return new(true); }
        public SeamResult AppendLog(string line) => new(true);
    }

    private sealed class MemorySecurity : IPlanFolderSecurity
    {
        private static AclRead Good => new(true, false, ScheduleFakes.UserSid, "operator", [new AclEntry(ScheduleFakes.UserSid, "operator", WriteRights.GenericAll, false, true)], null);
        public SeamResult Secure(string path, string userSid) => new(true);
        public AclRead ReadAcl(string path) => Good;
    }

    private sealed class RecordingScheduler : ITaskScheduler
    {
        public readonly List<(string Name, string Xml, PlanTask Expected)> Registered = new();
        public SeamResult Register(string taskName, string xmlPath, PlanTask expected) { Registered.Add((taskName, xmlPath, expected)); return new(true); }
        public bool Exists(string taskName) => Registered.Any(r => r.Name == taskName);
        public SeamResult Delete(string taskName) { Registered.RemoveAll(r => r.Name == taskName); return new(true); }
    }

    private sealed class HeldFs : IPurgeFileSystem
    {
        private bool held;
        private bool deleted;
        public Action? OnStop, OnStart;
        public HeldFs(bool initial) => held = initial;
        public void SetHeld(bool value) => held = value;
        private static PurgeFileInfo Info => new(FilePath, 400, OldWrite, false, false, 1) { VolumeSerial = 0x12345678, FileIndex = 0x2000000000000001 };
        public string? ResolveRoot(string path, out string? error) { error = null; return path; }
        public PurgeOpen Open(string path) => held ? new PurgeOpen("sharing violation", errorCode: 32) : deleted ? new PurgeOpen("not found", notFound: true) : new PurgeOpen(Info, new Disposable(), () => deleted = true, () => deleted = false);
        public PurgeFileInfo? Stat(string path, out string? error, out bool notFound) { error = null; notFound = false; return Info; }
        public bool? ProbeHeld(string path, out string? error) { error = null; return held; }
        private sealed class Disposable : IDisposable { public void Dispose() { } }
    }

    private sealed class TomcatServices : IServiceQuery
    {
        public IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error)
        { error = null; return [new ServiceEntry("Tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true)]; }
    }

    private sealed class RecordingControl(HeldFs fs) : IServiceControl
    {
        public readonly List<string> Actions = new();
        public IServiceHandle? Open(string host, string key, out string? error) { error = null; return new Handle(this, fs); }
        private sealed class Handle(RecordingControl parent, HeldFs fs) : IServiceHandle
        {
            private bool stopped;
            public IReadOnlyList<string>? RunningDependents(out string? error) { error = null; return []; }
            public (bool Ok, int Win32Error) Stop() { parent.Actions.Add("Stop"); stopped = true; fs.OnStop?.Invoke(); return (true, 0); }
            public (bool Ok, int Win32Error) Start() { parent.Actions.Add("Start"); stopped = false; fs.OnStart?.Invoke(); return (true, 0); }
            public (ServiceState State, string? Error) QueryState() => (stopped ? ServiceState.Stopped : ServiceState.Running, null);
            public void Dispose() { }
        }
    }

    private sealed class RecordingRelease : IReleaseSource
    {
        public int Calls;
        public Task<ReleasePackage?> FindLatestAsync(string currentVersion, string rid, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.FromResult<ReleasePackage?>(null); }
        public Task<byte[]> DownloadAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.FromResult(Array.Empty<byte>()); }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "dirused-program-schedule-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(root);
        public string PathFor(string name) => Path.Combine(root, name);
        public void Dispose() => Directory.Delete(root, true);
    }
}
