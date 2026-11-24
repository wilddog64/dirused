using System.Security.Cryptography;
using System.Text;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class RunPlanTests
{
    private const string Id = "20261124-1200-a1b2";
    private const string Root = @"C:\Users\op\AppData\Local\dirused\plans";
    private static readonly string Dir = Root + (char)92 + Id;
    private static readonly string Exe = Dir + @"\dirused.exe";
    private const string User = "S-1-5-21-1-2-3-1104";
    private static readonly DateTimeOffset Created = new(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Run = Created.AddMinutes(10);
    private static readonly byte[] ExeBytes = { 1, 2, 3, 4 };
    private const string ListText = "# dirused delete list v3\n# purge days=1000 minsize=1B\n# tool version=test\n# created=2026-11-24 10:00:00.0000000 -08:00\n# args=test\n# target\tvolumeSerial\tfileId\tbytes\tsize\tlastWrite\tpath\nC:\\logs\t00000001\t0000000000000001\t10\t10B\t2020-01-01 00:00:00.0000000 -08:00\tC:\\logs\\old.log\n";

    [Fact]
    public void MatchingPlanClaimsDeletesAndWritesAtomicReport()
    {
        var f = Fixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        int exit = PlanRunner.RunPlan(Id, f.Seams, f.RunSeams, output, error);
        Assert.Equal(0, exit);
        Assert.Equal("", output.ToString()); Assert.Equal("", error.ToString());
        Assert.Equal($"run {Pacific(Run):yyyy-MM-dd'T'HH:mm:sszzz} pid 41\n", Encoding.UTF8.GetString(f.Store.Files[Dir + @"\claim"]));
        string report = Encoding.UTF8.GetString(f.Store.Files[Dir + @"\report.txt"]);
        Assert.Contains("# outcome: done\n# exit: 0\n", report);
        Assert.True(report.Contains("# result: deleted 1 file(s), 10.0 B freed; 0 skipped; 0 failed"), report);
        Assert.Contains("\tC:\\logs\\old.log\t10\tdeleted", report);
        Assert.DoesNotContain("\r", report);
        Assert.Equal(1, f.Store.Reads.Count(x => x == Dir + @"\list.txt"));
        Assert.Contains("WriteNew " + Dir + @"\report.tmp", f.Store.Calls);
        Assert.Contains("MoveNew " + Dir + @"\report.tmp " + Dir + @"\report.txt", f.Store.Calls);
    }

    [Fact]
    public void OutsideWindowRefusesBeforeClaimAndExpiryClaimsAndReports()
    {
        var early = Fixture(Run.AddMinutes(-2));
        using var o = new StringWriter(); using var e = new StringWriter();
        Assert.Equal(1, PlanRunner.RunPlan(Id, early.Seams, early.RunSeams, o, e));
        Assert.Contains("refused: not the scheduled time", e.ToString());
        Assert.DoesNotContain(Dir + @"\claim", early.Store.Files.Keys);

        var expired = Fixture(Created.AddHours(24));
        using var o2 = new StringWriter(); using var e2 = new StringWriter();
        Assert.Equal(0, PlanRunner.RunPlan(Id, expired.Seams, expired.RunSeams, o2, e2));
        Assert.Contains("# outcome: expired", Encoding.UTF8.GetString(expired.Store.Files[Dir + @"\report.txt"]));
        Assert.Empty(expired.Fs.Deleted);
    }

    private sealed record Setup(ScheduleSeams Seams, RunPlanSeams RunSeams, MemoryStore Store, MemoryFs Fs);
    private static Setup Fixture(DateTimeOffset? clock = null)
    {
        var store = new MemoryStore(); var security = new MemorySecurity(); var fs = new MemoryFs();
        DateTimeOffset now = clock ?? Run;
        byte[] list = Encoding.UTF8.GetBytes(ListText);
        string plan = $"# dirused plan v1\n# id: {Id}\n# created: {Pacific(Created):yyyy-MM-dd'T'HH:mm:sszzz}\n# run: {Pacific(Run):yyyy-MM-dd'T'HH:mm:sszzz}\n# expires: {Pacific(Created.AddHours(24)):yyyy-MM-dd'T'HH:mm:sszzz}\n# tool: test\n# exe-sha256: {Hex(ExeBytes)}\n# list-sha256: {Hex(list)}\n# files: 1\n# bytes: 10\n# host: localhost\n# source: C:\\source\\list.txt\n# trusted: none\n";
        store.Files[Dir + @"\plan.txt"] = Encoding.UTF8.GetBytes(plan);
        store.Files[Dir + @"\list.txt"] = list;
        store.Files[Exe] = ExeBytes;
        var securePaths = new[] { @"C:\Users\op\AppData\Local\dirused", Root, Dir, Dir + @"\plan.txt", Dir + @"\list.txt", Exe };
        foreach (string path in securePaths) security.Acls[path] = new(true, false, User, "operator", new[] { new AclEntry(User, "operator", WriteRights.GenericAll, false, true) }, null);
        // PlanTrust also checks the executable and every ancestor. Give those paths safe, readable ACLs.
        foreach (string path in new[] { @"C:\", @"C:\Program Files", @"C:\Program Files\dirused", @"C:\Program Files\dirused\dirused.exe", @"C:\Users", @"C:\Users\op", @"C:\Users\op\AppData", @"C:\Users\op\AppData\Local" })
            security.Acls[path] = new(true, false, User, "operator", Array.Empty<AclEntry>(), null);
        var scheduler = new NoScheduler(); var trust = new NoTrust(); var accounts = new NoAccounts();
        var seams = new ScheduleSeams(() => now, TimeZoneInfo.Utc, Root, User, Exe, "test", () => 0, TokenLevel.Medium, store, security, scheduler, trust, accounts, _ => null);
        var file = new PurgeFileInfo(@"C:\logs\old.log", 10, new DateTime(2020, 1, 1, 8, 0, 0, DateTimeKind.Utc), false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.Files[file.FinalPath] = file;
        return new(seams, new RunPlanSeams(fs, null, _ => true, () => new DateTime(2026, 11, 24, 18, 10, 0, DateTimeKind.Utc), 41, @"C:\temp\purge.log", _ => { }), store, fs);
    }
    private static DateTimeOffset Pacific(DateTimeOffset value)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        return TimeZoneInfo.ConvertTime(value, zone);
    }
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class MemoryStore : IPlanStore
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase) { Root, Dir };
        public List<string> Calls { get; } = new(); public List<string> Reads { get; } = new();
        public bool Exists(string p) => Files.ContainsKey(p) || Dirs.Contains(p);
        public bool IsReparsePoint(string p) => false;
        public SeamResult CreateDirectory(string p) { Dirs.Add(p); return new(true); }
        public SeamResult WriteNew(string p, byte[] b) { Calls.Add("WriteNew " + p); if (Exists(p)) return new(false, "already exists"); Files[p] = b; return new(true); }
        public byte[]? ReadAll(string p) { Calls.Add("ReadAll " + p); Reads.Add(p); return Files.TryGetValue(p, out var b) ? b : null; }
        public IReadOnlyList<string> ListDirectoryNames(string p) => Array.Empty<string>();
        public SeamResult DeleteTree(string p) { Calls.Add("DeleteTree " + p); Files.Remove(p); Dirs.Remove(p); return new(true); }
        public SeamResult AppendLog(string line) { Calls.Add("AppendLog " + line); return new(true); }
        public SeamResult MoveNew(string from, string to) { Calls.Add("MoveNew " + from + " " + to); if (!Files.Remove(from, out var b) || Exists(to)) return new(false, "move failed"); Files[to] = b; return new(true); }
    }
    private sealed class MemorySecurity : IPlanFolderSecurity
    {
        public Dictionary<string, AclRead> Acls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SeamResult Secure(string p, string sid) => new(true);
        public AclRead ReadAcl(string p) => Acls.TryGetValue(p, out var a) ? a : new(false, true, null, null, Array.Empty<AclEntry>(), "not found");
    }
    private sealed class MemoryFs : IPurgeFileSystem
    {
        public Dictionary<string, PurgeFileInfo> Files { get; } = new(StringComparer.OrdinalIgnoreCase); public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ResolveRoot(string p, out string? error) { error = null; return p; }
        public PurgeOpen Open(string p) { if (!Files.TryGetValue(p, out var f)) return new("missing", true); return new(f, new Disposable(), () => { Deleted.Add(p); Files.Remove(p); }); }
        public PurgeFileInfo? Stat(string p, out string? error, out bool notFound) { error = null; notFound = !Files.TryGetValue(p, out var f); return f; }
        public bool? ProbeHeld(string p, out string? error) { error = null; return false; }
        private sealed class Disposable : IDisposable { public void Dispose() { } }
    }
    private sealed class NoScheduler : ITaskScheduler { public SeamResult Register(string n, string p, PlanTask t) => new(true); public bool Exists(string n) => false; public SeamResult Delete(string n) => new(true); }
    private sealed class NoTrust : ITrustStore { public IReadOnlyList<TrustedAccount> Load() => Array.Empty<TrustedAccount>(); public SeamResult Add(TrustedAccount a) => new(true); public SeamResult Remove(string s) => new(true); }
    private sealed class NoAccounts : IAccountResolver { public TrustedAccount? Resolve(string n) => null; }
}
