using System.Security.Cryptography;
using System.Text;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class VerifyB2d
{
    private const string ListPath = @"C:\Users\op\m10b.txt";
    private const string Plans = @"C:\Users\op\AppData\Local\dirused\plans";
    private const string Dirused = @"C:\Users\op\AppData\Local\dirused";
    private const string Exe = @"C:\Program Files\dirused\dirused.exe";
    private const string Plan = Plans + @"\20261123-2230-a1b2";
    private static readonly byte[] ExeBytes = { 1, 2, 3, 4, 5 };
    private static TimeZoneInfo Pac() => TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private static byte[] MakeList(bool bad = false)
    {
        var sb = new StringBuilder(PurgeList.Header + "\n# purge days=1 minsize=1GB\n");
        for (int i = 0; i < 12; i++)
        {
            long b = i < 11 ? 15L * 1073741824 : 12025908429L;
            sb.Append($"\\\\us03p02ffm01\\share\t00000001\t0000000000000001\t{b}\t1.0G\t2026-11-23 20:00:00.0000000 -08:00\t\\\\us03p02ffm01\\share\\f{i}.bin\n");
        }
        if (bad) sb.Append("garbage line\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
    private static long Total => 11 * 15L * 1073741824 + 12025908429L;

    private sealed class Store : IPlanStore
    {
        public List<string> Calls = null!; public HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase); public HashSet<string> Links = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, SeamResult>? CreateHook; public Func<string, SeamResult>? WriteHook; public SeamResult Log = new(true); public byte[]? Exe = ExeBytes;
        public Dictionary<string, byte[]> Written = new();
        public bool Exists(string p) { Calls.Add("Exists " + p); return Existing.Contains(p); }
        public bool IsReparsePoint(string p) { Calls.Add("IsReparsePoint " + p); return Links.Contains(p); }
        public SeamResult CreateDirectory(string p) { Calls.Add("CreateDirectory " + p); var r = CreateHook?.Invoke(p) ?? new(true); if (r.Ok) Existing.Add(p); return r; }
        public SeamResult WriteNew(string p, byte[] b) { Calls.Add("WriteNew " + p); var r = WriteHook?.Invoke(p) ?? new(true); if (r.Ok) Written[p] = b; return r; }
        public byte[]? ReadAll(string p) { Calls.Add("ReadAll " + p); return Exe; }
        public IReadOnlyList<string> ListDirectoryNames(string p) => Array.Empty<string>();
        public SeamResult DeleteTree(string p) { Calls.Add("DeleteTree " + p); return new(true); }
        public SeamResult AppendLog(string l) { Calls.Add("AppendLog " + l); return Log; }
    }
    private sealed class Sec : IPlanFolderSecurity
    {
        public List<string> Calls = null!; public FakeFolderSecurity Inner = null!; public Func<string, SeamResult>? Hook;
        public SeamResult Secure(string p, string sid) { Calls.Add("Secure " + p + " " + sid); return Hook?.Invoke(p) ?? new(true); }
        public AclRead ReadAcl(string p) => Inner.ReadAcl(p);
    }
    private sealed class Sched : ITaskScheduler
    {
        public List<string> Calls = null!; public SeamResult Result = new(true); public string? Name, Xml; public PlanTask? Task;
        public SeamResult Register(string n, string x, PlanTask t) { Calls.Add("Register " + n); Name = n; Xml = x; Task = t; return Result; }
        public bool Exists(string n) => false; public SeamResult Delete(string n) => new(true);
    }
    private sealed class Rig
    {
        public Store St; public Sec Se; public Sched Sc; public FakeTrustStore Tr; public List<string> Calls;
        public DateTimeOffset NowV = new DateTimeOffset(2026, 11, 23, 21, 0, 0, TimeSpan.FromHours(-8)).AddTicks(1234567);
        public Queue<int> Suffixes = new(new[] { 0xa1b2 }); public int Draws; public int Default = 0xdddd;
        public TokenLevel Token = TokenLevel.Medium;
        public StringWriter Out = new() { NewLine = "\n" }, Err = new() { NewLine = "\n" };
        public Options Opt = new(Array.Empty<string>(), Purge: true, FromList: ListPath, ScheduleTime: "22:30");
        public byte[] List = MakeList();
        public Rig(bool jump = false)
        {
            var (store, sec) = jump ? ScheduleFakes.JumpBoxProfile() : ScheduleFakes.StockSystemDrive();
            sec.Calls.Clear(); Calls = store.Calls; St = new Store { Calls = Calls }; Se = new Sec { Calls = Calls, Inner = sec };
            Sc = new Sched { Calls = Calls }; Tr = new FakeTrustStore(Calls);
        }
        public int Go(string input = "y", bool redirected = false, Func<DateTimeOffset>? now = null, TextReader? reader = null)
        {
            var seams = new ScheduleSeams(now ?? (() => NowV), Pac(), Plans, ScheduleFakes.UserSid, Exe, "0.0.28", () => { Draws++; return Suffixes.Count > 0 ? Suffixes.Dequeue() : Default++; }, Token, St, Se, Sc, Tr, new FakeAccountResolver(Calls), _ => null);
            return ScheduleCommands.Schedule(new ScheduleRequest(Opt, List, ListPath, ListPath), seams, Out, Err, reader ?? new StringReader(input + "\n"), redirected);
        }
        public Rig Confirm(bool yes = false) { Opt = Opt with { Confirm = true, Yes = yes }; return this; }
    }
    private sealed class AdvReader : TextReader { public Func<string?> F = () => "y"; public int Reads; public override string? ReadLine() { Reads++; return F(); } }
    private static string Sha12(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()[..12];
    private static string Expected(byte[] list) => string.Join("\n", new[] {
        "Scheduled purge preview (nothing is scheduled yet)",
        "  Run:      Mon 2026-11-23 22:30 PST (06:30 UTC Tue), in 1 h 30 min",
        "  Expires:  Tue 2026-11-24 21:00 PST (05:00 UTC Wed)",
        "  Host:     us03p02ffm01", @"  List:     C:\Users\op\m10b.txt",
        "            12 file(s), 176.2 GB in the list", "            sha256 " + Sha12(list),
        "  Held:     not checked now; files held open at run time are skipped",
        @"  Task:     \dirused\S-1-5-21-1-2-3-1104\purge-20261123-2230-a1b2",
        @"  Plan dir: C:\Users\op\AppData\Local\dirused\plans\20261123-2230-a1b2",
        @"  Program:  C:\Users\op\AppData\Local\dirused\plans\20261123-2230-a1b2\dirused.e", "            xe",
        @"            (copy of C:\Program Files\dirused\dirused.exe)",
        "It runs only while you are logged on to this machine (disconnected is fine).",
        "Preview only. Add /confirm to schedule it.", "" });

    [Fact] public void M1_LiteralPreviewAndPacificRunId()
    {
        var r = new Rig(); Assert.Equal(0, r.Go()); Assert.Equal(Expected(r.List), r.Out.ToString()); Assert.Empty(r.Err.ToString()); Assert.Empty(r.Calls);
        Assert.All(r.Out.ToString().Split('\n'), l => Assert.True(l.Length <= 80));
        r.NowV = r.NowV.ToUniversalTime(); r.Suffixes = new(new[] { 0xa1b2 }); r.Out.GetStringBuilder().Clear(); Assert.Equal(0, r.Go()); Assert.Equal(Expected(r.List), r.Out.ToString());
    }
    [Theory][InlineData("y")][InlineData("YES")][InlineData(" yes ")]
    public void M2_SchedulesAndWritesExpectedPlan(string answer)
    {
        var r = new Rig().Confirm(); Assert.Equal(0, r.Go(answer)); Assert.Equal("Schedule this? [y/N] ", r.Err.ToString());
        var ordered = r.Calls.Where(c => !c.StartsWith("ReadAcl") && !c.StartsWith("IsReparsePoint") && !c.StartsWith("Exists")).ToArray();
        Assert.Equal(new[] { "TrustLoad", "CreateDirectory " + Dirused, "Secure " + Dirused + " " + ScheduleFakes.UserSid,
            "CreateDirectory " + Plans, "Secure " + Plans + " " + ScheduleFakes.UserSid, "CreateDirectory " + Plan, "Secure " + Plan + " " + ScheduleFakes.UserSid,
            "ReadAll " + Exe, "WriteNew " + Plan + @"\dirused.exe", "WriteNew " + Plan + @"\list.txt", "WriteNew " + Plan + @"\task.xml", "WriteNew " + Plan + @"\plan.txt",
            @"Register \dirused\S-1-5-21-1-2-3-1104\purge-20261123-2230-a1b2", "AppendLog schedule 20261123-2230-a1b2 run 2026-11-23T22:30:00-08:00 files 12 bytes " + Total }, ordered);
        Assert.Equal(ExeBytes, r.St.Written[Plan + @"\dirused.exe"]); Assert.Equal(r.List, r.St.Written[Plan + @"\list.txt"]);
        var p = Encoding.UTF8.GetString(r.St.Written[Plan + @"\plan.txt"]);
        Assert.Contains("# created: 2026-11-23T21:00:00-08:00\n# run: 2026-11-23T22:30:00-08:00\n# expires: 2026-11-24T21:00:00-08:00\n", p);
        Assert.EndsWith("# trusted: none\n", p); Assert.DoesNotContain("\r", p);
        var expected = new PlanTask("20261123-2230-a1b2", new(2026, 11, 23, 22, 30, 0, TimeSpan.FromHours(-8)), new(2026, 11, 24, 21, 0, 0, TimeSpan.FromHours(-8)), ScheduleFakes.UserSid, Plan, "dirused scheduled purge 20261123-2230-a1b2 (12 file(s), 176.2 GB) from m10b.txt");
        Assert.Equal(TaskXml.Build(expected), r.St.Written[Plan + @"\task.xml"]); Assert.Equal(expected, r.Sc.Task); Assert.Equal(Plan + @"\task.xml", r.Sc.Xml);
        Assert.EndsWith("Scheduled 20261123-2230-a1b2 for Mon 2026-11-23 22:30 PST (06:30 UTC Tue). Check it with: dirused /schedule /list\n", r.Out.ToString());
    }
    [Fact] public void M3_YesAndNoDecisions()
    {
        var yes = new Rig().Confirm(true); var rd = new AdvReader(); Assert.Equal(0, yes.Go(reader: rd)); Assert.Empty(yes.Err.ToString()); Assert.Equal(0, rd.Reads); Assert.Single(yes.Calls, c => c.StartsWith("Register"));
        foreach (var (a, redirected) in new[] { ("n", false), ("", false), ("y", true) }) { var no = new Rig().Confirm(); var reader = new AdvReader { F = () => a }; Assert.Equal(0, no.Go(a, redirected, reader: reader)); Assert.EndsWith("Not scheduled.\n", no.Out.ToString()); Assert.Empty(no.Calls); Assert.Equal(redirected ? 0 : 1, reader.Reads); }
    }
    [Fact] public void M4_RechecksTimeAfterPrompt()
    {
        var r = new Rig().Confirm(); var now = r.NowV; var rd = new AdvReader { F = () => { now = new(2026, 11, 23, 22, 28, 30, TimeSpan.FromHours(-8)); return "y"; } };
        Assert.Equal(1, r.Go(reader: rd, now: () => now)); Assert.Equal("Schedule this? [y/N] /schedule: 22:30 is less than 2 minutes away; pick a later time\n", r.Err.ToString()); Assert.Empty(r.Calls);
    }
    [Fact] public void M5_ElevationIsRejectedBeforePrompt()
    {
        var r = new Rig().Confirm(); r.Token = TokenLevel.High; var rd = new AdvReader(); Assert.Equal(1, r.Go(reader: rd));
        Assert.Equal("/schedule: run from a normal (not elevated) window\n", r.Err.ToString()); Assert.Equal(0, rd.Reads); Assert.Empty(r.Calls);
    }
    [Fact] public void M6_BadRowsAndBadHeader()
    {
        var r = new Rig().Confirm(); r.List = MakeList(true); Assert.Equal(3, r.Go()); Assert.Empty(r.Calls); Assert.DoesNotContain("Preview only", r.Out.ToString());
        Assert.Matches(@"Bad list lines:\n  line \d+: .+\n/schedule: fix the list first; nothing was scheduled\n$", r.Err.ToString());
        var bad = new Rig().Confirm(); bad.List = Encoding.UTF8.GetBytes("nope"); Assert.Equal(1, bad.Go()); Assert.Equal("not a dirused delete list: " + ListPath + "\n", bad.Err.ToString()); Assert.Empty(bad.Calls);
    }
    [Fact] public void M7_TrustRefusesJumpProfileAndPersistsSortedTrustedSids()
    {
        var refuse = new Rig(true).Confirm(true); Assert.Equal(1, refuse.Go()); Assert.DoesNotContain(refuse.Calls, c => c.StartsWith("CreateDirectory") || c.StartsWith("WriteNew") || c.StartsWith("Register"));
        var r = new Rig(true).Confirm(true); r.Tr.Accounts.Add(new(ScheduleFakes.UnknownSid, "")); r.Tr.Accounts.Add(new(ScheduleFakes.AdminSid, @"PROD\admin2")); Assert.Equal(0, r.Go());
        var p = Encoding.UTF8.GetString(r.St.Written[Plan + @"\plan.txt"]); Assert.EndsWith("# trusted: S-1-5-21-1-2-3-2001,S-1-5-21-9-9-9-8249\n", p);
    }
    [Fact] public void M8_CollisionRegeneratesEveryPlanIdentityFromRun()
    {
        var r = new Rig().Confirm(true); r.Suffixes = new(new[] { 0xa1b2, 0xb2c3, 0xc3d4 }); int n = 0;
        r.St.CreateHook = p => { if (p.StartsWith(Plans + "\\") && n++ < 2) { r.St.Existing.Add(p); return new(false, "exists"); } return new(true); };
        Assert.Equal(0, r.Go()); var pd = Plans + @"\20261123-2230-c3d4"; Assert.Equal(3, r.Draws);
        Assert.Contains(@"Register \dirused\S-1-5-21-1-2-3-1104\purge-20261123-2230-c3d4", r.Calls); Assert.Equal(pd + @"\task.xml", r.Sc.Xml);
        Assert.Equal("20261123-2230-c3d4", r.Sc.Task!.Id); Assert.Equal(pd, r.Sc.Task.PlanDir); Assert.Contains("Scheduled 20261123-2230-c3d4 for", r.Out.ToString());
    }
    [Fact] public void M8_ExistingFoldersAreSecuredAndLogFailureIsWarning()
    {
        var r = new Rig().Confirm(true); r.St.Existing.Add(Plans); r.St.Existing.Add(Dirused); Assert.Equal(0, r.Go());
        Assert.DoesNotContain(r.Calls, c => c == "CreateDirectory " + Plans || c == "CreateDirectory " + Dirused);
        int sd = r.Calls.IndexOf("Secure " + Dirused + " " + ScheduleFakes.UserSid), sp = r.Calls.IndexOf("Secure " + Plans + " " + ScheduleFakes.UserSid), cp = r.Calls.IndexOf("CreateDirectory " + Plan);
        Assert.True(sd >= 0 && sp > sd && sp < cp);
        var log = new Rig().Confirm(true); log.St.Log = new(false, "locked"); Assert.Equal(0, log.Go()); Assert.Equal("warning: could not write the purge log: locked\n", log.Err.ToString());
        Assert.True(log.Calls.FindIndex(c => c.StartsWith("Register")) < log.Calls.FindIndex(c => c.StartsWith("AppendLog")));
    }
    [Fact] public void M8_SecureAndWriteFailuresDeletePlanBeforeRegistration()
    {
        var r = new Rig().Confirm(true); r.Se.Hook = p => p == Plan ? new(false, "acl boom") : new(true); Assert.Equal(3, r.Go()); Assert.Contains("DeleteTree " + Plan, r.Calls); Assert.DoesNotContain(r.Calls, c => c.StartsWith("WriteNew"));
        var w = new Rig().Confirm(true); w.St.WriteHook = p => p.EndsWith("task.xml") ? new(false, "disk full") : new(true); Assert.Equal(3, w.Go()); Assert.Contains("DeleteTree " + Plan, w.Calls); Assert.DoesNotContain(w.Calls, c => c.StartsWith("Register"));
    }
    [Fact] public void M8_TrustedListIsIncludedAndRegistrationFailureCleansUp()
    {
        var r = new Rig(true).Confirm(true); r.Tr.Accounts.Add(new(ScheduleFakes.AdminSid, "admin")); r.Tr.Accounts.Add(new(ScheduleFakes.UnknownSid, "unknown")); Assert.Equal(0, r.Go());
        Assert.EndsWith("# trusted: S-1-5-21-1-2-3-2001,S-1-5-21-9-9-9-8249\n", Encoding.UTF8.GetString(r.St.Written[Plan + @"\plan.txt"]));
        var f = new Rig().Confirm(true); f.Sc.Result = new(false, "could not schedule (schtasks exit 1)"); Assert.Equal(3, f.Go()); Assert.Contains("DeleteTree " + Plan, f.Calls); Assert.DoesNotContain(f.Calls, c => c.StartsWith("AppendLog"));
    }
}
