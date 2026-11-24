using System.Text;
using System.Text.RegularExpressions;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class SchedulePlansTests
{
    private const string Root = @"C:\Users\op\AppData\Local\dirused\plans";
    private const string U = ScheduleFakes.UserSid;
    private const string Id = "20261123-2230-a1b2";
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 23, 21, 10, 0, TimeSpan.FromHours(-8));
    private const string Tail = "  12 file(s) 176.2 GB  m10b.txt";

    private sealed class Store : IPlanStore
    {
        public readonly List<string> Log = new();
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase), Links = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Names = new();
        public string? WriteFail, LogFail;
        public bool Exists(string p) => Dirs.Contains(p) || Files.ContainsKey(p);
        public bool IsReparsePoint(string p) => Links.Contains(p);
        public SeamResult CreateDirectory(string p) => new(true);
        public SeamResult WriteNew(string p, byte[] b) { Log.Add("WriteNew " + p + " " + Encoding.UTF8.GetString(b).Replace("\n", "\\n")); if (WriteFail != null) return new(false, WriteFail); if (Files.ContainsKey(p)) return new(false, "exists"); Files[p] = b; return new(true); }
        public byte[]? ReadAll(string p) => Files.TryGetValue(p, out var b) ? b : null;
        public IReadOnlyList<string> ListDirectoryNames(string p) => Names;
        public SeamResult DeleteTree(string p) { Log.Add("DeleteTree " + p); return new(true); }
        public SeamResult AppendLog(string l) { Log.Add("AppendLog " + l); return LogFail == null ? new(true) : new(false, LogFail); }
    }
    private sealed class Security : IPlanFolderSecurity
    {
        public readonly Dictionary<string, AclRead> Acls = new(StringComparer.OrdinalIgnoreCase);
        public SeamResult Secure(string p, string sid) => new(true);
        public AclRead ReadAcl(string p) => Acls.TryGetValue(p, out var a) ? a : GoodAcl();
    }
    private sealed class Scheduler : ITaskScheduler
    {
        private readonly List<string> _log;
        public readonly HashSet<string> Existing = new(StringComparer.Ordinal);
        public string? DeleteFailure;
        public Scheduler(List<string> log) => _log = log;
        public SeamResult Register(string n, string p, PlanTask e) => new(true);
        public bool Exists(string n) => Existing.Contains(n);
        public SeamResult Delete(string n) { _log.Add("Delete " + n); return DeleteFailure == null ? new(true) : new(false, DeleteFailure); }
    }
    private sealed class Env
    {
        public readonly Store S = new(); public readonly Security C = new(); public readonly Scheduler T;
        public Func<int, string?> Proc = _ => null; public DateTimeOffset Now = FixedNow;
        public Env() { T = new Scheduler(S.Log); }
        public ScheduleSeams Seams()
        {
            var f = ScheduleFakes.StockSystemDrive(); var calls = new List<string>();
            return new(() => Now, TimeZoneInfo.Utc, Root, U, @"C:\x\dirused.exe", "t", () => 1, TokenLevel.Medium, S, C, T,
                new FakeTrustStore(calls), new FakeAccountResolver(calls), p => Proc(p));
        }
        public void Plan(string id, string run = "2026-11-23T22:30:00-08:00", string files = "12", string bytes = "189200000000", string? host = "us03p02ffm01", string source = @"C:\lists\m10b.txt", string eol = "\n", bool final = true, string? idLine = null)
        {
            var b = new StringBuilder("# dirused plan v1" + eol + "# id: " + (idLine ?? id) + eol + "# created: x" + eol + "# run: " + run + eol + "# files: " + files + eol + "# bytes: " + bytes + eol);
            if (host != null) b.Append("# host: " + host + eol); b.Append("# source: " + source + (final ? eol : "")); Add(id, b.ToString());
        }
        public void Add(string id, string text) { S.Names.Add(id); S.Dirs.Add(Root + "\\" + id); S.Files[Root + "\\" + id + "\\plan.txt"] = Encoding.UTF8.GetBytes(text); T.Existing.Add(@"\dirused\" + U + @"\purge-" + id); }
        public void File(string id, string name, string data) => S.Files[Root + "\\" + id + "\\" + name] = Encoding.UTF8.GetBytes(data);
    }
    private static AclRead GoodAcl() => new(true, false, U, "me", new[] { new AclEntry(U, "me", WriteRights.GenericAll, false, true) }, null);
    private static string[] List(Env e) { var o = new StringWriter { NewLine = "\n" }; var er = new StringWriter { NewLine = "\n" }; Assert.Equal(0, SchedulePlans.List(e.Seams(), o, er)); Assert.Equal("", er.ToString()); return o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
    private static (int, string, string) Cancel(Env e, string id) { var o = new StringWriter { NewLine = "\n" }; var er = new StringWriter { NewLine = "\n" }; int r = SchedulePlans.Cancel(id, e.Seams(), o, er); return (r, o.ToString(), er.ToString()); }
    private static Env CE(string id = Id) { var e = new Env(); e.Plan(id); return e; }

    [Fact] public void RowFormatScheduled() { var e = new Env(); e.Plan(Id); Assert.Equal(new[] { Id + "  Mon 2026-11-23 22:30 PST  us03p02ffm01  scheduled".Replace("  Mon", "  Mon") + Tail }, List(e)); }
    [Fact] public void RowFormatUtcRunConvertedAndPdt() { var e = new Env(); e.Plan("20260701-1000-0001", run: "2026-07-01T17:00:00+00:00"); Assert.Equal("20260701-1000-0001  Wed 2026-07-01 10:00 PDT  us03p02ffm01  missed (did not run)" + Tail, List(e)[0]); }
    [Fact] public void AllNineStatesAndPrecedence()
    {
        var e = new Env(); string I(int n) => "20261123-2230-000" + n;
        e.Plan(I(1)); e.C.Acls[Root + "\\" + I(1)] = new(true, false, "S-1-5-18", "x", Array.Empty<AclEntry>(), null);
        e.Plan(I(2), host: null);
        e.Plan(I(3)); e.File(I(3), "claim", "cancel 2026-11-23T21:00:00-08:00\n");
        e.Plan(I(4)); e.File(I(4), "claim", "run 2026-11-23T22:30:00-08:00 pid 42\n"); e.File(I(4), "report.txt", "# outcome: refused: plan folder contains a link\n");
        e.Plan(I(5)); e.File(I(5), "claim", "run 2026-11-23T22:30:00-08:00 pid 42\n");
        e.Plan(I(6)); e.File(I(6), "claim", "run 2026-11-23T22:30:00-08:00 pid 43\n");
        e.Plan(I(7), run: "2026-11-23T20:00:00-08:00");
        e.Plan(I(8)); e.T.Existing.Remove(@"\dirused\" + U + @"\purge-" + I(8));
        e.Plan(I(9)); e.Proc = p => p == 42 ? Root + "\\" + I(5) + "\\DIRUSED.EXE" : p == 43 ? @"C:\Windows\notepad.exe" : null;
        var rows = List(e).ToDictionary(x => x[..18]);
        Assert.Equal(I(1) + "  -  -  permissions changed — not run", rows[I(1)]); Assert.Equal(I(2) + "  -  -  unreadable plan", rows[I(2)]);
        Assert.EndsWith("  cancelled" + Tail, rows[I(3)]); Assert.EndsWith("  refused: plan folder contains a link" + Tail, rows[I(4)]); Assert.EndsWith("  running" + Tail, rows[I(5)]);
        Assert.EndsWith("  interrupted (started Mon 22:30 PST, no report) — check the list and the report folder" + Tail, rows[I(6)]); Assert.EndsWith("  missed (did not run)" + Tail, rows[I(7)]); Assert.EndsWith("  task missing" + Tail, rows[I(8)]); Assert.EndsWith("  scheduled" + Tail, rows[I(9)]); Assert.Equal(9, rows.Count);
        e.File(I(1), "claim", "cancel 2026-11-23T21:00:00-08:00\n");
    }
    [Fact] public void ReportOutcomesDoneExpiredAndVerbatim()
    {
        var e = new Env(); e.Plan("20261123-2230-0001"); e.File("20261123-2230-0001", "claim", "run 2026-11-23T22:30:00-08:00 pid 42"); e.Proc = _ => Root + "\\20261123-2230-0001\\dirused.exe";
        e.File("20261123-2230-0001", "report.txt", "# outcome: done\r\n# result: deleted 3 file(s), 52.1 GB freed; 1 skipped; 0 failed\r\n# exit: 0");
        Assert.Contains("done (deleted 3 file(s), 52.1 GB freed; 1 skipped; 0 failed, exit 0)", List(e)[0]);
        e.File("20261123-2230-0001", "report.txt", "# outcome: expired\n"); Assert.Contains("  expired  ", List(e)[0]);
        e.File("20261123-2230-0001", "report.txt", "# outcome: weird thing: x\n"); Assert.Contains("  weird thing: x  ", List(e)[0]);
    }
    [Fact] public void CrlfNoFinalNewlineAndBadFields()
    {
        var e = new Env(); e.Plan(Id, eol: "\r\n", final: false); Assert.Contains("Mon 2026-11-23 22:30 PST", List(e)[0]);
        e = new Env(); e.Plan("20261123-2230-0001", idLine: "20261123-2230-0009"); e.Plan("20261123-2230-0002", bytes: "-5"); e.Plan("20261123-2230-0003", run: "garbage"); e.Plan("20261123-2230-0004", source: ""); e.Plan("20261123-2230-0005", files: "x");
        foreach (var row in List(e)) Assert.EndsWith("  -  -  unreadable plan", row);
    }
    [Fact] public void MissingPlanNotesAndOrdering()
    {
        var e = new Env(); Assert.Equal(new[] { "No scheduled purges." }, List(e)); e.S.Names.Add("notes"); Assert.Equal(new[] { "No scheduled purges." }, List(e));
        e.Plan("20261123-2230-0001"); e.Plan("20261123-2230-00ff"); e.Plan("20261124-0900-0001", run: "2026-11-24T09:00:00-08:00"); e.Plan("20261122-0900-0001", run: "2026-11-22T09:00:00-08:00"); e.Plan("20261125-0900-0001", host: null); e.Plan("20261125-0900-0002", host: null); e.S.Names.Add("20261126-0900-0001");
        e.C.Acls[Root + "\\20261126-0900-0001"] = new(false, true, null, null, Array.Empty<AclEntry>(), "x");
        Assert.Equal(new[] { "20261124-0900-0001", "20261123-2230-00ff", "20261123-2230-0001", "20261122-0900-0001", "20261126-0900-0001", "20261125-0900-0002", "20261125-0900-0001" }, List(e).Select(x => x[..18]));
    }
    [Fact] public void RunningOnlyExactImageAndInterruptedPdt()
    {
        var e = new Env(); e.Plan(Id); e.File(Id, "claim", "run 2026-11-23T22:30:00-08:00 pid 42\n"); e.Proc = _ => Root + "\\" + Id + "\\dirused.exe.bak"; Assert.Contains("interrupted", List(e)[0]); e.Proc = _ => Root + "\\" + Id + "\\dirused.exe"; Assert.Contains("  running  ", List(e)[0]);
        e = new Env(); e.Plan("20260701-1000-0001", run: "2026-07-01T10:00:00-07:00"); e.File("20260701-1000-0001", "claim", "run 2026-07-01T10:00:05-07:00 pid 42\n"); e.Now = new(2026, 7, 1, 10, 30, 0, TimeSpan.FromHours(-7)); Assert.Contains("interrupted (started Wed 10:00 PDT, no report)", List(e)[0]);
    }
    [Fact] public void CancelledPastRunRemainsCancelled()
    {
        var e = new Env(); e.Plan(Id, run: "2026-11-23T20:00:00-08:00"); e.File(Id, "claim", "cancel 2026-11-23T19:00:00-08:00\n"); Assert.Contains("  cancelled  ", List(e)[0]);
    }
    [Fact] public void CancelHappyAndPacificTime()
    {
        var e = CE(); var r = Cancel(e, Id); Assert.Equal((0, "Cancelled " + Id + ".\n", ""), r);
        Assert.Equal(new[] { @"WriteNew C:\Users\op\AppData\Local\dirused\plans\20261123-2230-a1b2\claim cancel 2026-11-23T21:10:00-08:00\n", @"Delete \dirused\" + U + @"\purge-20261123-2230-a1b2", "AppendLog schedule-cancel " + Id }, e.S.Log);
        e = CE(); e.Now = new DateTimeOffset(2026, 11, 24, 5, 10, 0, TimeSpan.Zero); Cancel(e, Id); Assert.Contains("cancel 2026-11-23T21:10:00-08:00\\n", e.S.Log[0]);
    }
    [Fact] public void CancelRefusalsFailuresAndLogging()
    {
        var e = CE(); e.File(Id, "claim", "run 2026-11-23T22:30:00-08:00 pid 4\n"); Assert.Equal((1, "", Id + " already started; it cannot be cancelled\n"), Cancel(e, Id)); Assert.DoesNotContain(e.S.Log, x => x.StartsWith("Delete") || x.StartsWith("AppendLog"));
        e = CE(); e.File(Id, "claim", "cancel 2026-11-23T20:00:00-08:00\n"); Assert.Equal((0, Id + " is already cancelled\n", ""), Cancel(e, Id)); Assert.DoesNotContain(e.S.Log, x => x.StartsWith("Delete") || x.StartsWith("AppendLog"));
        e = CE(); e.S.WriteFail = "disk full"; Assert.Equal((3, "", "error: could not cancel " + Id + ": disk full\n"), Cancel(e, Id)); Assert.DoesNotContain(e.S.Log, x => x.StartsWith("Delete"));
        e = CE(); e.File(Id, "claim", "garbage"); Assert.Equal(3, Cancel(e, Id).Item1); Assert.Contains("exists", Cancel(e, Id).Item3);
        e = new Env(); Assert.Equal((1, "", "unknown plan id: bad\n"), Cancel(e, "bad")); Assert.Equal((1, "", "unknown plan id: " + Id + "\n"), Cancel(e, Id)); Assert.Equal((1, "", "unknown plan id: 20261123-2230-A1B2\n"), Cancel(e, "20261123-2230-A1B2")); Assert.Empty(e.S.Log);
        e = CE(); e.C.Acls[Root + "\\" + Id] = new(true, false, "S-1-5-18", "x", Array.Empty<AclEntry>(), null); Assert.Equal((1, "", "refused: plan folder permissions changed\n"), Cancel(e, Id)); Assert.Empty(e.S.Log);
        e = CE(); e.T.DeleteFailure = "access denied"; Assert.Equal((3, "", "error: could not delete task for " + Id + ": access denied; the plan is marked cancelled and will not run\n"), Cancel(e, Id)); Assert.True(e.S.Files.ContainsKey(Root + "\\" + Id + "\\claim")); Assert.DoesNotContain(e.S.Log, x => x.StartsWith("AppendLog"));
        e = CE(); e.S.LogFail = "locked"; Assert.Equal((0, "Cancelled " + Id + ".\n", "warning: could not write the purge log: locked\n"), Cancel(e, Id));
    }
    [Fact] public void IsIntactChecksLinksOwnersAclsAndInheritOnly()
    {
        var pdir = Root + "\\" + Id; bool Intact(Env e) => SchedulePlans.IsIntact(pdir, e.Seams());
        var e = CE(); Assert.True(Intact(e));
        foreach (var p in new[] { @"C:\Users\op\AppData\Local\dirused", Root, pdir, pdir + "\\plan.txt", pdir + "\\list.txt", pdir + "\\dirused.exe" }) { e = CE(); e.S.Links.Add(p); Assert.False(Intact(e)); }
        var paths = new[] { Root, pdir, pdir + "\\plan.txt", pdir + "\\list.txt", pdir + "\\dirused.exe" };
        foreach (var p in paths)
        {
            AclEntry A(string sid, bool io = false, bool allow = true) => new(sid, "d", WriteRights.GenericAll, io, allow);
            var bad = new[] { new AclRead(true, false, ScheduleFakes.AdminSid, "a", new[] { A(U) }, null), new AclRead(true, false, null, null, new[] { A(U) }, null), new AclRead(true, false, U, "a", new[] { A(U), A(ScheduleFakes.AdminSid) }, null), new AclRead(true, false, U, "a", new[] { A(U), A(ScheduleFakes.AdminSid, true) }, null), new AclRead(false, false, U, "a", new[] { A(U) }, "boom") };
            foreach (var acl in bad) { e = CE(); e.C.Acls[p] = acl; Assert.False(Intact(e)); }
            e = CE(); e.C.Acls[p] = new(true, false, U.ToLowerInvariant(), "a", new[] { A(U.ToLowerInvariant()), A(ScheduleFakes.AdminSid, allow: false) }, null); Assert.True(Intact(e));
        }
    }
    [Fact] public void NoDeleteTreeAndForbiddenApiScan()
    {
        var e = CE(); Cancel(e, Id); List(e); Assert.DoesNotContain(e.S.Log, x => x.StartsWith("DeleteTree"));
        string dir = AppContext.BaseDirectory; while (dir != null && !File.Exists(Path.Combine(dir, "dirused.sln"))) dir = Path.GetDirectoryName(dir)!;
        string src = File.ReadAllText(Path.Combine(dir!, "src", "Dirused.Common", "SchedulePlans.cs")); src = Regex.Replace(src, @"//[^\n]*", ""); src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.DoesNotMatch(@"\b(File|Directory|Path|Process|Registry|Console)\s*[.(]", src); Assert.DoesNotContain("DeleteTree", src);
    }
}
