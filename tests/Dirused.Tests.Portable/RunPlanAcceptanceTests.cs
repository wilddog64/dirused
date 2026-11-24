using System.Text;
using Dirused.Common;
using Dirused.Common.Renderers;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class RunPlanAcceptanceTests
{
    private const string DirusedDir = @"C:\Users\op\AppData\Local\dirused";
    private const string Plans = DirusedDir + @"\plans";
    private const string ProgExe = @"C:\Program Files\dirused\dirused.exe";
    private const string ListPath = @"C:\Users\op\m10b.txt";
    private const string Root = @"\\h\d$\Tomcat";
    private const string HeldPath = @"\\h\d$\Tomcat\Tomcat\logs\Tomcat85-stderr.2026-11-04.log";
    private const string ScheduledStatus = "skipped: held by Tomcat85 — scheduled runs do not stop services";
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Pst = TimeSpan.FromHours(-8);
    private static readonly DateTimeOffset RunClock = new(2026, 11, 23, 22, 30, 0, Pst);
    private static readonly byte[] ExeBytes = { 1, 2, 3, 4, 5 };

    private static TimeZoneInfo Pac() => TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private sealed class Mem : IPlanStore
    {
        public List<string> Calls = new(); public List<string> Log = new();
        public Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Links = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, SeamResult?>? WriteHook; public string? MoveFail; public Func<string, SeamResult?>? LogHook;
        public bool Exists(string p) { Calls.Add("Exists " + p); return Files.ContainsKey(p) || Dirs.Contains(p); }
        public bool IsReparsePoint(string p) { Calls.Add("IsReparsePoint " + p); return Links.Contains(p); }
        public SeamResult CreateDirectory(string p) { Calls.Add("CreateDirectory " + p); if (Dirs.Contains(p) || Files.ContainsKey(p)) return new(false, "exists"); Dirs.Add(p); return new(true); }
        public SeamResult WriteNew(string p, byte[] b) { Calls.Add("WriteNew " + p); var h = WriteHook?.Invoke(p); if (h != null) return h; if (Files.ContainsKey(p)) return new(false, "exists " + p); Files[p] = b; return new(true); }
        public SeamResult MoveNew(string from, string to) { Calls.Add("MoveNew " + from + " " + to); if (MoveFail != null) return new(false, MoveFail); if (Files.ContainsKey(to) || !Files.ContainsKey(from)) return new(false, "bad move"); Files[to] = Files[from]; Files.Remove(from); return new(true); }
        public byte[]? ReadAll(string p) { Calls.Add("ReadAll " + p); return Files.TryGetValue(p, out var b) ? b : null; }
        public IReadOnlyList<string> ListDirectoryNames(string p) { Calls.Add("ListDirectoryNames " + p); return Dirs.Where(d => d.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase) && d.IndexOf('\\', p.Length + 1) < 0).Select(d => d[(p.Length + 1)..]).ToArray(); }
        public SeamResult DeleteTree(string p) { Calls.Add("DeleteTree " + p); Files.Remove(p); Dirs.Remove(p); return new(true); }
        public SeamResult AppendLog(string line) { Calls.Add("AppendLog " + line); var h = LogHook?.Invoke(line); if (h != null) return h; Log.Add(line); return new(true); }
    }

    private sealed class Svc : IServiceQuery
    {
        public IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error)
        { error = null; return host.Equals("h", StringComparison.OrdinalIgnoreCase) ? new[] { new ServiceEntry("Tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) } : Array.Empty<ServiceEntry>(); }
    }

    private sealed class Fs : IPurgeFileSystem
    {
        public Dictionary<string, bool?> HeldProbes = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeFileInfo> Stats = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeFileInfo> OpenInfos = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeOpen> OpenErrors = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Deleted = new(StringComparer.OrdinalIgnoreCase);
        public bool ThrowOnOpen;
        public string? ResolveRoot(string path, out string? error) { error = null; return path; }
        public PurgeOpen Open(string path)
        {
            if (ThrowOnOpen) throw new InvalidOperationException("boom-open");
            if (Deleted.Contains(path)) return new PurgeOpen("not found", notFound: true);
            if (OpenErrors.Remove(path, out var e)) return e;
            if (!OpenInfos.TryGetValue(path, out var info)) return new PurgeOpen("missing", true);
            return new PurgeOpen(info, new Disp(), () => Deleted.Add(path), () => Deleted.Remove(path));
        }
        public PurgeFileInfo? Stat(string path, out string? error, out bool notFound) { error = null; notFound = !Stats.TryGetValue(path, out var i); return i; }
        public bool? ProbeHeld(string path, out string? error) { error = null; return HeldProbes.GetValueOrDefault(path, false); }
        private sealed class Disp : IDisposable { public void Dispose() { } }
    }

    private sealed record Row(string Path, long Bytes, ulong Id, bool Held = false);
    private static List<Row> DefaultRows() => new()
    {
        new(@"\\h\d$\Tomcat\Tomcat\logs\p1.log", 100, 0x1000000000000001),
        new(@"\\h\d$\Tomcat\Tomcat\logs\p2.log", 200, 0x1000000000000002),
        new(@"\\h\d$\Tomcat\Tomcat\logs\p3.log", 300, 0x1000000000000003),
        new(HeldPath, 400, 0x2000000000000001, true),
    };
    private static PurgeFileInfo Info(Row r, long? len = null, DateTime? t = null, ulong? id = null) =>
        new(r.Path, len ?? r.Bytes, t ?? Old, false, false, 1) { VolumeSerial = 0x12345678, FileIndex = id ?? r.Id };

    private static Fs MakeFs(List<Row> rows, Action<Fs>? mod = null)
    {
        var fs = new Fs();
        foreach (var r in rows)
        {
            fs.OpenInfos[r.Path] = Info(r); fs.Stats[r.Path] = Info(r); fs.HeldProbes[r.Path] = r.Held;
            if (r.Held) fs.OpenErrors[r.Path] = new PurgeOpen("sharing violation", errorCode: 32);
        }
        mod?.Invoke(fs);
        return fs;
    }

    private static byte[] MakeList(List<Row> rows)
    {
        var sb = new StringBuilder(PurgeList.Header + "\n# purge days=7 minsize=1B\n");
        foreach (var r in rows)
            sb.Append($"{Root}\t12345678\t{r.Id:X16}\t{r.Bytes}\t{r.Bytes}B\t2026-01-01 00:00:00.0000000 +00:00\t{r.Path}\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static AclRead UserOnly() => new(true, false, ScheduleFakes.UserSid, @"PROD\op", new[] { new AclEntry(ScheduleFakes.UserSid, @"PROD\op", WriteRights.GenericAll, false, true) }, null);

    private sealed class Rig
    {
        public Mem St = new(); public FakeFolderSecurity Sec; public List<string> SecCalls = new(); public FakeTrustStore Tr;
        public List<string> SchedCalls = new(); public string Id = ""; public string PlanDir = ""; public List<Row> Rows;
        public byte[] ListBytes; public int Sleeps; public Action<Fs>? FsMod; public int Pid = 4242;
        public DateTimeOffset Created = new(2026, 11, 23, 21, 0, 0, Pst);
        public string P(string name) => PlanDir + "\\" + name;

        public Rig(bool jump = false, bool trustBoth = false, string time = "22:30", List<Row>? rows = null)
        {
            Rows = rows ?? DefaultRows(); ListBytes = MakeList(Rows);
            var (_, sec) = jump ? ScheduleFakes.JumpBoxProfile() : ScheduleFakes.StockSystemDrive();
            Sec = sec; Tr = new FakeTrustStore(SchedCalls);
            if (trustBoth) { Tr.Accounts.Add(new(ScheduleFakes.AdminSid, @"PROD\admin2")); Tr.Accounts.Add(new(ScheduleFakes.UnknownSid, "")); }
            St.Files[ProgExe] = ExeBytes;
            var opt = new Options(Array.Empty<string>(), Purge: true, FromList: ListPath, ScheduleTime: time) with { Confirm = true, Yes = true };
            var seams = new ScheduleSeams(() => Created, Pac(), Plans, ScheduleFakes.UserSid, ProgExe, "0.0.28", () => 0xa1b2, TokenLevel.Medium, St, Sec, new FakeTaskScheduler(SchedCalls), Tr, new FakeAccountResolver(SchedCalls), _ => null);
            var o = new StringWriter { NewLine = "\n" }; var e = new StringWriter { NewLine = "\n" };
            int code = ScheduleCommands.Schedule(new ScheduleRequest(opt, ListBytes, ListPath, ListPath), seams, o, e, new StringReader("y\n"), true);
            Assert.True(code == 0, "schedule failed: " + e + o);
            Id = St.Dirs.Where(d => d.StartsWith(Plans + "\\", StringComparison.OrdinalIgnoreCase)).Single()[(Plans.Length + 1)..];
            PlanDir = Plans + "\\" + Id;
            foreach (var path in new[] { DirusedDir, Plans, PlanDir, P("plan.txt"), P("list.txt"), P("dirused.exe") }) Sec.Acls[path] = UserOnly();
            St.Calls.Clear(); St.Log.Clear(); SchedCalls.Clear();
        }

        public string PlanText { get => Encoding.UTF8.GetString(St.Files[P("plan.txt")]); set => St.Files[P("plan.txt")] = new UTF8Encoding(false).GetBytes(value); }
        public string PlanLine(string key) => PlanText.Split('\n').First(l => l.StartsWith("# " + key + ":"));
        public Dictionary<string, byte[]> Snapshot() => St.Files.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

        public ScheduleSeams Seams(DateTimeOffset clock, string? exe = null) =>
            new(() => clock, Pac(), Plans, ScheduleFakes.UserSid, exe ?? P("dirused.exe"), "0.0.28", () => 0, TokenLevel.Medium, St, Sec, new FakeTaskScheduler(SchedCalls), Tr, new FakeAccountResolver(SchedCalls), _ => null);

        public Fs LastFs = null!;
        public (int Exit, string Err, string Out) Run(DateTimeOffset clock, string? id = null, string? exe = null)
        {
            LastFs = MakeFs(Rows, FsMod);
            var r = new RunPlanSeams(LastFs, new Svc(), null, () => clock.UtcDateTime, Pid, @"C:\x\purge.log", _ => Sleeps++);
            var o = new StringWriter { NewLine = "\n" }; var e = new StringWriter { NewLine = "\n" };
            int code = PlanRunner.RunPlan(id ?? Id, Seams(clock, exe), r, o, e);
            Assert.Empty(o.ToString());
            Invariants();
            return (code, e.ToString(), o.ToString());
        }

        public void Invariants()
        {
            Assert.True(St.Calls.Count(c => c == "ReadAll " + P("list.txt")) <= 1, "list read twice");
            Assert.True(St.Calls.Count(c => c == "ReadAll " + P("plan.txt")) <= 1, "plan read twice");
            Assert.DoesNotContain(St.Calls, c => c.Contains("m10b.txt"));
            Assert.DoesNotContain(St.Calls, c => c == "DeleteTree " + PlanDir || c == "DeleteTree " + Plans);
            Assert.DoesNotContain(SchedCalls, c => c.StartsWith("Register") || c.StartsWith("Delete"));
        }

        public string? ReportText() => St.Files.TryGetValue(P("report.txt"), out var b) ? new UTF8Encoding(false).GetString(b) : null;
        public IEnumerable<string> Writes() => St.Calls.Where(c => c.StartsWith("WriteNew ") || c.StartsWith("MoveNew "));
        public void AssertNothingCreated(Dictionary<string, byte[]> before)
        {
            Assert.DoesNotContain(St.Calls, c => c == "WriteNew " + P("claim") || c == "WriteNew " + P("report.tmp") || c == "WriteNew " + P("report.txt") || c.StartsWith("MoveNew"));
            Assert.Equal(before.Keys.OrderBy(x => x), St.Files.Keys.OrderBy(x => x));
        }
        public void AssertRefusedStderr(int exit, string err, string line)
        {
            Assert.Equal(1, exit);
            Assert.Equal(line + "\n", err);
            Assert.Contains($"runplan {Id}: {line}", St.Log);
        }
    }

    private static string Stamp(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    private static (PurgeRun Run, List<string> Log, List<string> Err) Oracle(Rig rig, DateTimeOffset clock)
    {
        Assert.True(PurgeList.Parse(rig.ListBytes, out var list));
        var fs = MakeFs(rig.Rows, rig.FsMod); var log = new List<string>(); var err = new StringWriter { NewLine = "\n" };
        var run = Purge.Apply(PurgeList.ToResults(list!), list!.Days, list.MinBytes, true, fs, () => clock.UtcDateTime, log.Add, CancellationToken.None,
            purgeLogPath: @"C:\x\purge.log", services: new Svc(), isFixedDrive: null, control: null, localNow: null, sleep: null, input: TextReader.Null, inputRedirected: true, yes: true, error: err, scheduled: true);
        return (run, log, err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
    }

    private static string ExpectedReport(string id, DateTimeOffset started, DateTimeOffset finished, string outcome, int exit, PurgeRun? run, List<string> log, List<string> err)
    {
        var w = new StringWriter { NewLine = "\n" };
        w.WriteLine("# dirused run report v1"); w.WriteLine("# id: " + id); w.WriteLine("# started: " + Stamp(started)); w.WriteLine("# finished: " + Stamp(finished));
        w.WriteLine("# outcome: " + outcome); w.WriteLine("# exit: " + exit);
        if (run != null)
        {
            w.WriteLine($"# result: deleted {run.Deleted} file(s), {Format.Size(run.DeletedBytes)} freed; {run.Skipped} skipped; {run.Failed} failed");
            new TextRenderer().Render(w, run.Results);
            if (run.HeldSummary is { Total: > 0 } h)
                w.WriteLine(h.TomcatCount > 0 && h.Service != null
                    ? $"held files: {h.Total} (Tomcat: {h.Service} on {h.Host}, root {h.InstallRoot}: {h.TomcatCount} file(s), {Format.Size(h.TomcatBytes)}; other: {h.OtherCount})"
                    : $"held files: {h.Total} (other: {h.OtherCount})");
        }
        w.WriteLine("--- purge log"); foreach (var l in log) w.WriteLine(l);
        w.WriteLine("--- stderr"); foreach (var l in err) w.WriteLine(l);
        return w.ToString();
    }

    private static string Outcome(string? report) => report!.Split('\n').First(l => l.StartsWith("# outcome: "))[11..];
    private static string ExitLine(string? report) => report!.Split('\n').First(l => l.StartsWith("# exit: "))[8..];

    [Fact]
    public void M1_HappyPath()
    {
        var rig = new Rig();
        var (exit, err, _) = rig.Run(RunClock);
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Equal("run 2026-11-23T22:30:00-08:00 pid 4242\n", Encoding.UTF8.GetString(rig.St.Files[rig.P("claim")]));
        Assert.Equal(new[] { "WriteNew " + rig.P("claim"), "WriteNew " + rig.P("report.tmp"), "MoveNew " + rig.P("report.tmp") + " " + rig.P("report.txt") }, rig.Writes());
        Assert.False(rig.St.Files.ContainsKey(rig.P("report.tmp")));
        Assert.Equal(new[] { "p1.log", "p2.log", "p3.log" }.Select(n => @"\\h\d$\Tomcat\Tomcat\logs\" + n).OrderBy(x => x), rig.LastFs.Deleted.OrderBy(x => x));
        var (run, log, oerr) = Oracle(rig, RunClock);
        Assert.Equal(3, run.Deleted); Assert.Equal(1, run.Skipped); Assert.Equal(0, run.Failed);
        Assert.Equal(3, log.Count);
        byte[] bytes = rig.St.Files[rig.P("report.txt")];
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        string report = rig.ReportText()!;
        Assert.Contains(ScheduledStatus, report);
        Assert.Equal(ExpectedReport(rig.Id, RunClock, RunClock, "done", 0, run, log, oerr), report);
        Assert.Contains("# result: deleted 3 file(s), " + Format.Size(600) + " freed; 1 skipped; 0 failed", report);
        Assert.Contains("held files: 1 (Tomcat: Tomcat85", report);
        Assert.Equal(log, rig.St.Log);
        Assert.Equal(1, rig.St.Calls.Count(c => c == "ReadAll " + rig.P("list.txt")));
        var o = new StringWriter { NewLine = "\n" }; var e = new StringWriter();
        SchedulePlans.List(rig.Seams(RunClock), o, e);
        Assert.Contains("done (deleted 3 file(s), " + Format.Size(600) + " freed; 1 skipped; 0 failed, exit 0)", o.ToString());
    }

    public static IEnumerable<object[]> LinkCases()
    {
        yield return new object[] { DirusedDir };
        yield return new object[] { Plans };
        yield return new object[] { "<plan>" };
        yield return new object[] { "<plan>\\plan.txt" };
        yield return new object[] { "<plan>\\list.txt" };
        yield return new object[] { "<plan>\\dirused.exe" };
    }

    [Theory, MemberData(nameof(LinkCases))]
    public void M2_ReparsePointOnEachOfSix(string path)
    {
        var rig = new Rig(); string p = path.Replace("<plan>", rig.PlanDir); rig.St.Links.Add(p);
        var before = rig.Snapshot();
        var (exit, err, _) = rig.Run(RunClock);
        rig.AssertRefusedStderr(exit, err, "refused: plan folder contains a link");
        rig.AssertNothingCreated(before);
        Assert.Empty(rig.LastFs.Deleted);
    }

    [Fact]
    public void M2_BadIdMissingPlanWrongExeAcl()
    {
        var rig = new Rig(); var before = rig.Snapshot();
        var (e1, s1, _) = rig.Run(RunClock, id: "bad");
        Assert.Equal(1, e1); Assert.Equal("refused: not a plan id: bad\n", s1); Assert.Contains("runplan bad: refused: not a plan id: bad", rig.St.Log);
        rig.St.Log.Clear();
        var (e2, s2, _) = rig.Run(RunClock, id: "20261123-2230-ffff");
        Assert.Equal(1, e2); Assert.Equal("refused: not a plan id: 20261123-2230-ffff\n", s2); Assert.Contains("runplan 20261123-2230-ffff: refused: not a plan id: 20261123-2230-ffff", rig.St.Log);
        rig.St.Log.Clear();
        var (e3, s3, _) = rig.Run(RunClock, exe: ProgExe); rig.AssertRefusedStderr(e3, s3, "refused: not started from the plan's program copy");
        rig.St.Log.Clear();
        rig.Sec.Acls[rig.P("plan.txt")] = new AclRead(true, false, ScheduleFakes.UserSid, @"PROD\op", new[] { new AclEntry(ScheduleFakes.UserSid, @"PROD\op", WriteRights.GenericAll, false, true), new AclEntry(ScheduleFakes.AdminSid, @"PROD\admin2", WriteRights.Write, false, true) }, null);
        var (e4, s4, _) = rig.Run(RunClock); rig.AssertRefusedStderr(e4, s4, "refused: plan folder permissions changed");
        rig.AssertNothingCreated(before); Assert.Empty(rig.LastFs.Deleted);
    }

    [Theory]
    [InlineData(-120)]
    [InlineData(16 * 60)]
    [InlineData(-61)]
    [InlineData(15 * 60 + 1)]
    public void M2_N3_OutsideWindow(int offsetSeconds)
    {
        var rig = new Rig(); var before = rig.Snapshot();
        var (exit, err, _) = rig.Run(RunClock.AddSeconds(offsetSeconds));
        rig.AssertRefusedStderr(exit, err, "refused: not the scheduled time (scheduled Mon 2026-11-23 22:30 PST)");
        rig.AssertNothingCreated(before); Assert.Empty(rig.LastFs.Deleted);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(15 * 60)]
    public void N3_WindowEdgesRunAndWriteClaim(int offsetSeconds)
    {
        var rig = new Rig(); var (exit, _, _) = rig.Run(RunClock.AddSeconds(offsetSeconds));
        Assert.Equal(0, exit); Assert.True(rig.St.Files.ContainsKey(rig.P("claim"))); Assert.Equal(3, rig.LastFs.Deleted.Count);
    }

    [Fact]
    public void M2_ReportPresentNoClaim()
    {
        var rig = new Rig(); var planted = Encoding.UTF8.GetBytes("# planted\n"); rig.St.Files[rig.P("report.txt")] = planted;
        var before = rig.Snapshot();
        var (exit, err, _) = rig.Run(RunClock);
        rig.AssertRefusedStderr(exit, err, "refused: plan already has a report");
        rig.AssertNothingCreated(before);
        Assert.Equal(planted, rig.St.Files[rig.P("report.txt")]); Assert.Empty(rig.LastFs.Deleted);
    }

    private static void AssertReportOutcome(Rig rig, (int Exit, string Err, string Out) r, string outcome, int exit)
    {
        Assert.Equal(exit, r.Exit);
        Assert.Equal(new[] { "WriteNew " + rig.P("claim"), "WriteNew " + rig.P("report.tmp"), "MoveNew " + rig.P("report.tmp") + " " + rig.P("report.txt") }, rig.Writes());
        string rep = rig.ReportText()!;
        Assert.Equal(outcome, Outcome(rep)); Assert.Equal(exit.ToString(), ExitLine(rep));
        Assert.DoesNotContain("# result:", rep);
        Assert.DoesNotContain((byte)'\r', rig.St.Files[rig.P("report.txt")]);
        Assert.Empty(rig.LastFs.Deleted);
        Assert.DoesNotContain(rig.St.Calls, c => c.StartsWith("DeleteTree"));
        Assert.StartsWith("run ", Encoding.UTF8.GetString(rig.St.Files[rig.P("claim")]));
    }

    [Fact] public void M3_NoTrustedLine()
    {
        var rig = new Rig(); rig.PlanText = rig.PlanText.Replace(rig.PlanLine("trusted") + "\n", "");
        AssertReportOutcome(rig, rig.Run(RunClock), "refused: not a dirused plan", 1);
    }
    [Fact] public void M3_DuplicatedRun()
    {
        var rig = new Rig(); rig.PlanText = rig.PlanText.Replace(rig.PlanLine("run") + "\n", rig.PlanLine("run") + "\n" + rig.PlanLine("run") + "\n");
        AssertReportOutcome(rig, rig.Run(RunClock), "refused: not a dirused plan", 1);
    }
    [Fact] public void M3_ExpiresPlus25h()
    {
        var rig = new Rig(); rig.PlanText = rig.PlanText.Replace(rig.PlanLine("expires"), "# expires: " + Stamp(rig.Created.AddHours(25)));
        AssertReportOutcome(rig, rig.Run(RunClock), "refused: not a dirused plan", 1);
    }
    [Fact] public void M3_ExeChanged()
    {
        var rig = new Rig(); rig.St.Files[rig.P("dirused.exe")] = new byte[] { 9, 9 };
        AssertReportOutcome(rig, rig.Run(RunClock), "refused: program copy changed since it was scheduled", 1);
    }
    [Fact] public void M3_ListChanged()
    {
        var rig = new Rig(); rig.St.Files[rig.P("list.txt")] = rig.ListBytes.Concat(new byte[] { (byte)'\n' }).ToArray();
        AssertReportOutcome(rig, rig.Run(RunClock), "refused: list changed since it was scheduled", 1);
    }
    [Fact] public void M3_ClockEqualsExpires()
    {
        var rig = new Rig(); var r = rig.Run(rig.Created.AddHours(24));
        AssertReportOutcome(rig, r, "expired", 0);
    }
    [Fact] public void M3_TrustRecheckJumpBox()
    {
        var rig = new Rig(jump: true, trustBoth: true); rig.PlanText = rig.PlanText.Replace(rig.PlanLine("trusted"), "# trusted: none");
        AssertReportOutcome(rig, rig.Run(RunClock), @"refused: C:\Users\op can be changed by PROD\admin2 (full control)", 1);
    }
    [Fact] public void M3b_PositiveControlTrusted()
    {
        var rig = new Rig(jump: true, trustBoth: true);
        Assert.Equal("# trusted: S-1-5-21-1-2-3-2001,S-1-5-21-9-9-9-8249", rig.PlanLine("trusted"));
        var (exit, _, _) = rig.Run(RunClock);
        Assert.Equal(0, exit); Assert.Equal("done", Outcome(rig.ReportText())); Assert.Equal(3, rig.LastFs.Deleted.Count);
    }
    [Fact] public void M3_MoreInvalidPlans()
    {
        foreach (var edit in new Func<string, string>[]
        {
            t => t.Replace("# dirused plan v1", "# dirused plan v2"),
            t => t.Replace("# files: 4", "# files: -1"),
            t => t.Replace("# bytes: 1000", "# bytes: x"),
            t => t.Replace("# id: ", "# id: x"),
            t => t.Replace("# host: h", "# host: "),
        })
        {
            var rig = new Rig(); string orig = rig.PlanText; string changed = edit(orig); Assert.NotEqual(orig, changed); rig.PlanText = changed;
            AssertReportOutcome(rig, rig.Run(RunClock), "refused: not a dirused plan", 1);
        }
        var rig2 = new Rig(); rig2.St.Files[rig2.P("plan.txt")] = new byte[] { 0xFF, 0xFE, 0xFD };
        AssertReportOutcome(rig2, rig2.Run(RunClock), "refused: not a dirused plan", 1);
        var rig3 = new Rig(); rig3.PlanText = rig3.PlanText.Replace("\n", "\r\n");
        var r3 = rig3.Run(RunClock); Assert.Equal(0, r3.Exit);
        var rig4 = new Rig(); rig4.PlanText = rig4.PlanText.Replace(rig4.PlanLine("run"), "# run: " + Stamp(rig4.Created.AddHours(23).AddMinutes(46)));
        AssertReportOutcome(rig4, rig4.Run(RunClock), "refused: not a dirused plan", 1);
    }

    [Fact]
    public void M4_ClaimAlreadyCancelOrRunOrEmpty()
    {
        foreach (var clock in new[] { RunClock, new Rig().Created.AddHours(24) })
        {
            var rig = new Rig(); rig.St.Files[rig.P("claim")] = Encoding.UTF8.GetBytes("cancel 2026-11-23T22:00:00-08:00\n"); var before = rig.Snapshot();
            var (e, s, _) = rig.Run(clock);
            Assert.Equal(0, e); Assert.Equal("cancelled\n", s); NoDeleteNoReport(rig, before);
            var rig2 = new Rig(); rig2.St.Files[rig2.P("claim")] = Encoding.UTF8.GetBytes("run 2026-11-23T22:00:00-08:00 pid 7\n"); before = rig2.Snapshot();
            var (e2, s2, _) = rig2.Run(clock);
            Assert.Equal(1, e2); Assert.Equal("refused: plan already started\n", s2); NoDeleteNoReport(rig2, before);
        }
        var rig3 = new Rig(); rig3.St.Files[rig3.P("claim")] = Array.Empty<byte>(); var b3 = rig3.Snapshot();
        var (e3, s3, _) = rig3.Run(RunClock);
        Assert.Equal(3, e3); Assert.Equal("error: could not claim plan: exists " + rig3.P("claim") + "\n", s3); Assert.Equal(2, rig3.Sleeps); NoDeleteNoReport(rig3, b3);
        Assert.Equal(3, rig3.St.Calls.Count(c => c == "ReadAll " + rig3.P("claim")));
    }
    private static void NoDeleteNoReport(Rig rig, Dictionary<string, byte[]> before)
    {
        Assert.Empty(rig.LastFs.Deleted);
        Assert.DoesNotContain(rig.St.Calls, c => c == "WriteNew " + rig.P("report.tmp") || c == "WriteNew " + rig.P("report.txt") || c.StartsWith("MoveNew") || c.StartsWith("DeleteTree"));
        Assert.Equal(before.Keys.OrderBy(x => x), rig.St.Files.Keys.OrderBy(x => x));
    }

    [Fact]
    public void M4_ClaimSlowlyAppears()
    {
        var rig = new Rig(); rig.St.Files[rig.P("claim")] = Array.Empty<byte>(); int reads = 0;
        var fs = rig.St; 
        var seams = rig.Seams(RunClock);
        var r = new RunPlanSeams(MakeFs(rig.Rows), new Svc(), null, () => RunClock.UtcDateTime, 1, "x", _ => { if (++reads == 1) rig.St.Files[rig.P("claim")] = Encoding.UTF8.GetBytes("cancel 2026-11-23T22:00:00-08:00\n"); });
        var e = new StringWriter { NewLine = "\n" };
        Assert.Equal(0, PlanRunner.RunPlan(rig.Id, seams, r, new StringWriter(), e));
        Assert.Equal("cancelled\n", e.ToString()); Assert.Equal(1, reads);
    }

    [Fact]
    public void M5_MoveFailsAndWriteFails()
    {
        var rig = new Rig(); rig.St.MoveFail = "disk full";
        var (exit, err, _) = rig.Run(RunClock);
        Assert.Equal(0, exit); Assert.Contains("error: could not write report: disk full", err.Split('\n'));
        Assert.Contains($"runplan {rig.Id}: report failed: disk full", rig.St.Log);
        var rig2 = new Rig(); rig2.St.WriteHook = p => p.EndsWith("report.tmp") ? new SeamResult(false, "no space") : null;
        var (e2, s2, _) = rig2.Run(RunClock);
        Assert.Equal(0, e2); Assert.Contains("error: could not write report: no space", s2.Split('\n'));
        Assert.Contains($"runplan {rig2.Id}: report failed: no space", rig2.St.Log);
        var rig3 = new Rig(); rig3.St.MoveFail = "x"; rig3.Rows[0] = rig3.Rows[0] with { Bytes = 100 }; rig3.FsMod = fs => { fs.OpenInfos[rig3.Rows[0].Path] = Info(rig3.Rows[0], len: 101); };
        var (e3, _, _) = rig3.Run(RunClock);
        Assert.Equal(0, e3);
    }

    [Fact]
    public void M6_AppendLogFailsOnFirstDeleteLine()
    {
        var rig = new Rig(); int n = 0;
        rig.St.LogHook = line => ++n == 1 ? new SeamResult(false, "log disk") : null;
        var (exit, _, _) = rig.Run(RunClock);
        Assert.Equal(3, exit);
        string rep = rig.ReportText()!;
        Assert.Equal("done", Outcome(rep)); Assert.Equal("3", ExitLine(rep));
        Assert.Contains("failed: log write failed", rep);
        Assert.Empty(rig.LastFs.Deleted);
    }

    [Fact]
    public void M7_OpenThrows()
    {
        var rig = new Rig(); rig.FsMod = fs => fs.ThrowOnOpen = true;
        var (exit, _, _) = rig.Run(RunClock);
        string rep = rig.ReportText()!;
        Assert.Equal("done", Outcome(rep)); Assert.Equal(3, exit); Assert.Contains("failed: boom-open", rep);
    }

    private sealed class ThrowSvc : IServiceQuery { public IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error) => throw new InvalidOperationException("boom-svc"); }

    [Theory]
    [InlineData("utcnow")]
    public void M7_OutsideThrows(string where)
    {
        var rig = new Rig();
        var r = new RunPlanSeams(MakeFs(rig.Rows), where == "svc" ? new ThrowSvc() : new Svc(), where == "fixed" ? (_ => throw new InvalidOperationException("boom-fixed")) : null,
            where == "utcnow" ? (() => throw new InvalidOperationException("boom-utcnow")) : (() => RunClock.UtcDateTime), 4242, "x", _ => { });
        var e = new StringWriter { NewLine = "\n" };
        int exit = PlanRunner.RunPlan(rig.Id, rig.Seams(RunClock), r, new StringWriter(), e);
        string rep = rig.ReportText() ?? "<none>";
        if (where == "utcnow") { Assert.Equal("refused: error during run: boom-utcnow", Outcome(rep)); Assert.Equal(3, exit); Assert.DoesNotContain("# result:", rep); }
    }

    [Fact]
    public void N1_MismatchedRowsNotDeletedControlDeleted()
    {
        var rows = new List<Row> {
            new(@"\\h\d$\Tomcat\Tomcat\logs\size.log", 100, 0x1000000000000001), new(@"\\h\d$\Tomcat\Tomcat\logs\time.log", 100, 0x1000000000000002),
            new(@"\\h\d$\Tomcat\Tomcat\logs\id.log", 100, 0x1000000000000003), new(@"\\h\d$\Tomcat\Tomcat\logs\ok.log", 100, 0x1000000000000004) };
        var rig = new Rig(rows: rows);
        rig.FsMod = fs =>
        {
            void Set(Row r, PurgeFileInfo i) { fs.OpenInfos[r.Path] = i; fs.Stats[r.Path] = i; }
            Set(rows[0], Info(rows[0], len: 101)); Set(rows[1], Info(rows[1], t: Old.AddTicks(1))); Set(rows[2], Info(rows[2], id: 0x1000000000000099));
        };
        var (exit, _, _) = rig.Run(RunClock);
        Assert.Equal(new[] { rows[3].Path }, rig.LastFs.Deleted.ToArray());
        Assert.Equal(0, exit);
    }

    [Fact]
    public void N4_ExpiredDeletesNothing_ControlLateWindowDeletes()
    {
        var rig = new Rig(); var r = rig.Run(rig.Created.AddHours(24)); Assert.Empty(rig.LastFs.Deleted); Assert.Equal(0, r.Exit);
        var rig2 = new Rig(time: "20:45");
        Assert.Equal("# run: 2026-11-24T20:45:00-08:00", rig2.PlanLine("run"));
        var clock = new DateTimeOffset(2026, 11, 24, 20, 45, 0, Pst);
        var (exit, _, _) = rig2.Run(clock);
        Assert.Equal(0, exit); Assert.Equal("done", Outcome(rig2.ReportText())); Assert.Equal(3, rig2.LastFs.Deleted.Count);
    }

    [Fact]
    public void N5_ReadCountsAndNoSource()
    {
        var rig = new Rig(); rig.Run(RunClock);
        Assert.Equal(1, rig.St.Calls.Count(c => c == "ReadAll " + rig.P("list.txt")));
        Assert.Equal(1, rig.St.Calls.Count(c => c == "ReadAll " + rig.P("plan.txt")));
        Assert.DoesNotContain(rig.St.Calls, c => c.Contains(ListPath));
    }

    [Fact]
    public void N6_StaleReportTmpIsDeleted()
    {
        var rig = new Rig(); rig.St.Files[rig.P("report.tmp")] = new byte[] { 1 };
        var (exit, _, _) = rig.Run(RunClock);
        Assert.Equal(0, exit); Assert.Contains("DeleteTree " + rig.P("report.tmp"), rig.St.Calls);
        Assert.Equal("done", Outcome(rig.ReportText())); Assert.False(rig.St.Files.ContainsKey(rig.P("report.tmp")));
        var rig2 = new Rig(); rig2.St.Files[rig2.P("report.tmp")] = new byte[] { 1 }; rig2.Run(RunClock.AddHours(5));
        Assert.DoesNotContain("DeleteTree " + rig2.P("report.tmp"), rig2.St.Calls);
    }

    [Fact]
    public void NoServiceControlOrSchedulerAndSourceNeverChecked()
    {
        Assert.DoesNotContain(typeof(RunPlanSeams).GetProperties(), p => p.PropertyType == typeof(IServiceControl));
    }
}
