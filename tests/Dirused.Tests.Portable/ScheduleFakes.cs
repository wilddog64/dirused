using Dirused.Common;

namespace Dirused.Tests.Portable;

internal sealed class FakePlanStore : IPlanStore
{
    public List<string> Calls { get; }
    public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);
    public FakePlanStore(List<string> calls) => Calls = calls;
    public bool Exists(string p) { Calls.Add("Exists " + p); return false; }
    public bool IsReparsePoint(string p) { Calls.Add("IsReparsePoint " + p); return Links.Contains(p); }
    public SeamResult CreateDirectory(string p) { Calls.Add("CreateDirectory " + p); return new(true); }
    public SeamResult WriteNew(string p, byte[] b) { Calls.Add("WriteNew " + p); return new(true); }
    public byte[]? ReadAll(string p) { Calls.Add("ReadAll " + p); return null; }
    public IReadOnlyList<string> ListDirectoryNames(string p) { Calls.Add("ListDirectoryNames " + p); return Array.Empty<string>(); }
    public SeamResult DeleteTree(string p) { Calls.Add("DeleteTree " + p); return new(true); }
    public SeamResult AppendLog(string line) { Calls.Add("AppendLog " + line); return new(true); }
}
internal sealed class FakeFolderSecurity : IPlanFolderSecurity
{
    public List<string> Calls { get; }
    public Dictionary<string, AclRead> Acls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public FakeFolderSecurity(List<string> calls) => Calls = calls;
    public SeamResult Secure(string p, string sid) { Calls.Add("Secure " + p + " " + sid); return new(true); }
    public AclRead ReadAcl(string p) { Calls.Add("ReadAcl " + p); return Acls.TryGetValue(p, out var a) ? a : new(false, true, null, null, Array.Empty<AclEntry>(), null); }
}
internal sealed class FakeTaskScheduler : ITaskScheduler
{
    public List<string> Calls { get; }
    public FakeTaskScheduler(List<string> calls) => Calls = calls;
    public SeamResult Register(string n, string p, PlanTask expected) { Calls.Add("Register " + n); return new(true); }
    public bool Exists(string n) { Calls.Add("TaskExists " + n); return false; }
    public SeamResult Delete(string n) { Calls.Add("TaskDelete " + n); return new(true); }
}
internal sealed class FakeTrustStore : ITrustStore
{
    public List<string> Calls { get; }
    public List<TrustedAccount> Accounts { get; } = new();
    public string? Failure { get; set; }
    public FakeTrustStore(List<string> calls) => Calls = calls;
    public IReadOnlyList<TrustedAccount> Load() { Calls.Add("TrustLoad"); return Accounts.ToArray(); }
    public SeamResult Add(TrustedAccount a) { Calls.Add("TrustAdd " + a.Sid); if (Failure != null) return new(false, Failure); Accounts.Add(a); return new(true); }
    public SeamResult Remove(string sid) { Calls.Add("TrustRemove " + sid); if (Failure != null) return new(false, Failure); Accounts.RemoveAll(a => a.Sid == sid); return new(true); }
}
internal sealed class FakeAccountResolver : IAccountResolver
{
    public List<string> Calls { get; }
    public Dictionary<string, TrustedAccount> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public FakeAccountResolver(List<string> calls) => Calls = calls;
    public TrustedAccount? Resolve(string name) { Calls.Add("Resolve " + name); return Accounts.TryGetValue(name, out var a) ? a : null; }
}
internal static class ScheduleFakes
{
    public const string UserSid = "S-1-5-21-1-2-3-1104";
    public const string AdminSid = "S-1-5-21-1-2-3-2001";
    public const string UnknownSid = "S-1-5-21-9-9-9-8249";
    public const string TiSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public static (FakePlanStore Store, FakeFolderSecurity Security) StockSystemDrive() => Build(false);
    public static (FakePlanStore Store, FakeFolderSecurity Security) JumpBoxProfile() => Build(true);
    private static (FakePlanStore, FakeFolderSecurity) Build(bool jump)
    {
        var calls = new List<string>(); var store = new FakePlanStore(calls); var sec = new FakeFolderSecurity(calls);
        const string C = @"C:\", pf = @"C:\Program Files", bin = @"C:\Program Files\dirused", users = @"C:\Users", profile = @"C:\Users\op", app = @"C:\Users\op\AppData", local = @"C:\Users\op\AppData\Local", dirused = @"C:\Users\op\AppData\Local\dirused", plans = @"C:\Users\op\AppData\Local\dirused\plans";
        AclEntry E(string sid, string display, WriteRights rights, bool io = false) => new(sid, display, rights, io, true);
        AclRead R(string owner, string od, params AclEntry[] entries) => new(true, false, owner, od, entries, null);
        var commonPf = new[] { E(TiSid, "TrustedInstaller", WriteRights.GenericAll, true), E("S-1-5-18", "SYSTEM", WriteRights.GenericAll, true), E("S-1-5-32-544", "Administrators", WriteRights.GenericAll, true), E("S-1-5-32-545", "Users", WriteRights.None), E("S-1-5-32-545", "Users", WriteRights.None, true), E("S-1-3-0", "CREATOR OWNER", WriteRights.GenericAll, true), E("S-1-15-2-1", "ALL APPLICATION PACKAGES", WriteRights.None), E("S-1-15-2-1", "ALL APPLICATION PACKAGES", WriteRights.None, true), E("S-1-15-2-2", "ALL RESTRICTED APPLICATION PACKAGES", WriteRights.None), E("S-1-15-2-2", "ALL RESTRICTED APPLICATION PACKAGES", WriteRights.None, true) };
        sec.Acls[C] = R(TiSid, "TrustedInstaller", E("S-1-5-18", "SYSTEM", WriteRights.GenericAll), E("S-1-5-32-544", "Administrators", WriteRights.GenericAll), E("S-1-5-32-545", "Users", WriteRights.None), E("S-1-5-32-545", "Users", WriteRights.Append), E("S-1-5-32-545", "Users", WriteRights.Write, true), E("S-1-3-0", "CREATOR OWNER", WriteRights.GenericAll, true));
        sec.Acls[pf] = R(TiSid, "TrustedInstaller", E(TiSid, "TrustedInstaller", WriteRights.GenericAll), E(TiSid, "TrustedInstaller", WriteRights.GenericAll, true), E("S-1-5-18", "SYSTEM", WriteRights.Write | WriteRights.Append | WriteRights.Delete), E("S-1-5-18", "SYSTEM", WriteRights.GenericAll, true), E("S-1-5-32-544", "Administrators", WriteRights.Write | WriteRights.Append | WriteRights.Delete), E("S-1-5-32-544", "Administrators", WriteRights.GenericAll, true), E("S-1-5-32-545", "Users", WriteRights.None), E("S-1-5-32-545", "Users", WriteRights.None, true), E("S-1-3-0", "CREATOR OWNER", WriteRights.GenericAll, true), E("S-1-15-2-1", "ALL APPLICATION PACKAGES", WriteRights.None), E("S-1-15-2-1", "ALL APPLICATION PACKAGES", WriteRights.None, true), E("S-1-15-2-2", "ALL RESTRICTED APPLICATION PACKAGES", WriteRights.None), E("S-1-15-2-2", "ALL RESTRICTED APPLICATION PACKAGES", WriteRights.None, true));
        sec.Acls[bin] = R(TiSid, "TrustedInstaller", commonPf.Where(x => x.InheritOnly && x.Sid != "S-1-3-0").Select(x => x with { InheritOnly = false }).ToArray());
        sec.Acls[bin + @"\dirused.exe"] = sec.Acls[bin];
        sec.Acls[users] = R("S-1-5-18", "SYSTEM", E("S-1-5-18", "SYSTEM", WriteRights.GenericAll), E("S-1-5-32-544", "Administrators", WriteRights.GenericAll), E("S-1-5-32-545", "Users", WriteRights.None), E("S-1-5-32-545", "Users", WriteRights.None, true), E("S-1-1-0", "Everyone", WriteRights.None), E("S-1-1-0", "Everyone", WriteRights.None, true));
        var profileEntries = new List<AclEntry> { E("S-1-5-18", "SYSTEM", WriteRights.GenericAll), E("S-1-5-32-544", "Administrators", WriteRights.GenericAll), E(UserSid, @"PROD\op", WriteRights.GenericAll) };
        if (jump) { profileEntries.Add(E(UnknownSid, "", WriteRights.GenericAll)); profileEntries.Add(E(AdminSid, @"PROD\admin2", WriteRights.GenericAll)); }
        sec.Acls[profile] = R("S-1-5-32-544", "Administrators", profileEntries.ToArray());
        sec.Acls[app] = R(UserSid, @"PROD\op", profileEntries.ToArray());
        sec.Acls[local] = R(UserSid, @"PROD\op", profileEntries.ToArray());
        sec.Acls[dirused] = new(false, true, null, null, Array.Empty<AclEntry>(), null);
        sec.Acls[plans] = new(false, true, null, null, Array.Empty<AclEntry>(), null);
        return (store, sec);
    }
}
