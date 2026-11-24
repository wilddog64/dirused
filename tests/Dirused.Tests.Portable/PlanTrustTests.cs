using Dirused.Common;
using System.Text.RegularExpressions;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class PlanTrustTests
{
    private const string Exe = @"C:\Program Files\dirused\dirused.exe";
    private const string Plans = @"C:\Users\op\AppData\Local\dirused\plans";
    private static AclRead Acl(string? owner = null, string? display = null, params AclEntry[] entries) => new(true, false, owner, display, entries, null);
    private static AclEntry Entry(string sid, string? display, WriteRights rights, bool io = false, bool allow = true) => new(sid, display!, rights, io, allow);
    private static TrustCheck CheckAt(string path, PathRole role, AclRead acl, IReadOnlyCollection<TrustedAccount>? trusted = null)
    {
        var f = ScheduleFakes.StockSystemDrive();
        f.Security.Acls[path] = acl;
        return PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, trusted ?? Array.Empty<TrustedAccount>(), f.Store, f.Security);
    }

    [Fact] public void PathsToCheckHasExactLeavesAncestorsAndErrors()
    {
        var p = PlanTrust.PathsToCheck(Exe, Plans, out var error)!;
        Assert.Null(error);
        Assert.Equal(new[] { Exe, @"C:\Program Files\dirused", @"C:\Program Files", @"C:\", @"C:\Users\op\AppData\Local\dirused", Plans, @"C:\Users\op\AppData\Local", @"C:\Users\op\AppData", @"C:\Users\op", @"C:\Users" }, p.Select(x => x.FullPath));
        Assert.Equal(new[] { PathRole.Leaf, PathRole.Leaf, PathRole.Ancestor, PathRole.Ancestor, PathRole.Leaf, PathRole.Leaf, PathRole.Ancestor, PathRole.Ancestor, PathRole.Ancestor, PathRole.Ancestor }, p.Select(x => x.Role));
        Assert.True(p[4].MayBeMissing); Assert.True(p[5].MayBeMissing);
        Assert.Null(PlanTrust.PathsToCheck(@"\\srv\share\dirused.exe", Plans, out error)); Assert.Equal("/schedule: dirused.exe must be on a local drive to schedule (\\\\srv\\share\\dirused.exe)", error);
        Assert.Null(PlanTrust.PathsToCheck("dirused.exe", Plans, out error)); Assert.Contains("dirused.exe", error);
        Assert.Null(PlanTrust.PathsToCheck(Exe, "relative", out error)); Assert.Equal("/schedule: the plan folder must be on a local drive (relative)", error);
        var norm = PlanTrust.PathsToCheck(@"C:\\Program Files\\dirused\\dirused.exe\", @"C:\Users\op\AppData\Local\dirused\plans", out _)!;
        Assert.Equal(Exe, norm[0].FullPath);
        Assert.Null(PlanTrust.PathsToCheck(@"C:/dirused.exe", Plans, out error)); Assert.Contains(@"C:/dirused.exe", error);
    }

    [Fact] public void StockAclIsSafeAndJumpBoxFindingsHaveStableOrderAndTrustSteps()
    {
        var stock = ScheduleFakes.StockSystemDrive();
        var ok = PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, Array.Empty<TrustedAccount>(), stock.Store, stock.Security);
        Assert.True(ok.Ok); Assert.Empty(ok.Lines);
        var jump = ScheduleFakes.JumpBoxProfile();
        var first = PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, Array.Empty<TrustedAccount>(), jump.Store, jump.Security);
        Assert.False(first.Ok);
        Assert.Equal(new[] {
            @"/schedule: C:\Users\op can be changed by PROD\admin2 (full control).",
            "  If you trust this account, run once: dirused /schedule /trust \"PROD\\admin2\"",
            @"  also: C:\Users\op by unresolved account S-1-5-21-9-9-9-8249",
            @"  also: C:\Users\op\AppData by PROD\admin2",
            @"  also: C:\Users\op\AppData by unresolved account S-1-5-21-9-9-9-8249",
            @"  also: C:\Users\op\AppData\Local by PROD\admin2",
            @"  also: C:\Users\op\AppData\Local by unresolved account S-1-5-21-9-9-9-8249"
        }, first.Lines);
        var one = PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, new[] { new TrustedAccount(ScheduleFakes.AdminSid, @"PROD\admin2") }, jump.Store, jump.Security);
        Assert.Equal(@"/schedule: C:\Users\op can be changed by unresolved account S-1-5-21-9-9-9-8249 (full control).", one.Lines[0]);
        Assert.Equal("  If you trust this account, run once: dirused /schedule /trust \"S-1-5-21-9-9-9-8249\"", one.Lines[1]);
        Assert.True(PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, new[] { new TrustedAccount(ScheduleFakes.AdminSid, @"PROD\admin2"), new TrustedAccount(ScheduleFakes.UnknownSid, "") }, jump.Store, jump.Security).Ok);
    }

    [Fact] public void LeafAndAncestorRightsDistinguishInheritanceAndDeny()
    {
        const string leaf = @"C:\Program Files\dirused"; const string ancestor = @"C:\Program Files";
        var append = CheckAt(leaf, PathRole.Leaf, Acl(entries: new[] { Entry("S-1-5-32-545", "Users", WriteRights.Append) }));
        Assert.Equal("  This group is too wide to trust; ask IT to remove this permission or move dirused.exe.", append.Lines[1]);
        var write = CheckAt(leaf, PathRole.Leaf, Acl(entries: new[] { Entry(ScheduleFakes.AdminSid, @"PROD\admin2", WriteRights.Write) }));
        Assert.Equal("  If you trust this account, run once: dirused /schedule /trust \"PROD\\admin2\"", write.Lines[1]);
        Assert.True(CheckAt(leaf, PathRole.Leaf, Acl(entries: new[] { Entry(ScheduleFakes.AdminSid, "admin", WriteRights.Write, true) })).Ok);
        Assert.True(CheckAt(leaf, PathRole.Leaf, Acl(entries: new[] { Entry(ScheduleFakes.AdminSid, "admin", WriteRights.Write, allow: false) })).Ok);
        Assert.True(CheckAt(leaf, PathRole.Leaf, Acl(entries: new[] { Entry(ScheduleFakes.AdminSid, "admin", WriteRights.None) })).Ok);
        foreach (var (rights, expected) in new[] { (WriteRights.Delete, "delete"), (WriteRights.DeleteChild, "delete child"), (WriteRights.WriteDac, "change permissions"), (WriteRights.WriteOwner, "take ownership"), (WriteRights.GenericAll, "full control") })
        { var result = CheckAt(ancestor, PathRole.Ancestor, Acl(entries: new[] { Entry(ScheduleFakes.AdminSid, "admin", rights) })); Assert.Contains("(" + expected + ").", result.Lines[0]); }
        foreach (var rights in new[] { WriteRights.Append, WriteRights.Write, WriteRights.GenericWrite }) Assert.True(CheckAt(ancestor, PathRole.Ancestor, Acl(entries: new[] { Entry(ScheduleFakes.AdminSid, "admin", rights) })).Ok);
    }

    [Fact] public void OwnerDisplaysAndCreatorOwnerAreHandled()
    {
        var owner = CheckAt(@"C:\Program Files\dirused", PathRole.Leaf, Acl(ScheduleFakes.AdminSid, @"PROD\admin2"));
        Assert.Contains("(owner)", owner.Lines[0]); Assert.Contains("PROD\\admin2", owner.Lines[0]);
        Assert.True(CheckAt(@"C:\Program Files\dirused", PathRole.Leaf, Acl("S-1-5-32-544", "Administrators")).Ok);
        Assert.True(CheckAt(@"C:\Program Files\dirused", PathRole.Leaf, Acl(ScheduleFakes.TiSid, "TrustedInstaller")).Ok);
        Assert.False(CheckAt(@"C:\Program Files\dirused", PathRole.Leaf, Acl("S-1-3-0", "CREATOR OWNER")).Ok);
        var unresolved = CheckAt(@"C:\Program Files\dirused", PathRole.Leaf, Acl(entries: new[] { Entry(ScheduleFakes.UnknownSid, "", WriteRights.Write) }));
        Assert.Contains("unresolved account " + ScheduleFakes.UnknownSid, unresolved.Lines[0]); Assert.Contains("\"" + ScheduleFakes.UnknownSid + "\"", unresolved.Lines[1]);
    }

    [Fact] public void ReparseReadFailuresAndMissingPathsStopOrPassPrecisely()
    {
        var f = ScheduleFakes.StockSystemDrive(); f.Store.Links.Add(@"C:\Program Files\dirused");
        Assert.Equal(new[] { "/schedule: C:\\Program Files\\dirused is a link; dirused does not schedule through links" }, PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, Array.Empty<TrustedAccount>(), f.Store, f.Security).Lines);
        Assert.DoesNotContain(f.Security.Calls, x => x == "ReadAcl C:\\Program Files\\dirused");
        f = ScheduleFakes.StockSystemDrive(); f.Security.Acls[@"C:\Users"] = new(false, false, null, null, Array.Empty<AclEntry>(), "access denied");
        Assert.Equal("/schedule: cannot read permissions of C:\\Users: access denied", PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, Array.Empty<TrustedAccount>(), f.Store, f.Security).Lines.Single());
        f = ScheduleFakes.StockSystemDrive(); Assert.True(PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, Array.Empty<TrustedAccount>(), f.Store, f.Security).Ok);
        f.Security.Acls[@"C:\Program Files\dirused"] = new(false, true, null, null, Array.Empty<AclEntry>(), null);
        Assert.Equal("/schedule: cannot read permissions of C:\\Program Files\\dirused: not found", PlanTrust.Check(Exe, Plans, ScheduleFakes.UserSid, Array.Empty<TrustedAccount>(), f.Store, f.Security).Lines.Single());
    }

    [Fact] public void EveryBroadSidAndDomainAliasIsRecognized()
    {
        foreach (var sid in new[] { "S-1-1-0", "S-1-2-0", "S-1-5-2", "S-1-5-4", "S-1-5-7", "S-1-5-11", "S-1-5-13", "S-1-5-14", "S-1-5-15", "S-1-15-2-1", "S-1-5-32-545", "S-1-5-32-546", "S-1-5-32-555", "S-1-5-21-1-2-3-513", "S-1-5-21-1-2-3-514", "S-1-5-21-1-2-3-515" }) Assert.True(PlanTrust.IsBroad(sid), sid);
        foreach (var sid in new[] { "S-1-5-21-1-2-3-2001", "S-1-5-32-544", "S-1-5-18", "S-1-5-21-1-2-3-1514", "S-1-5-21-1-2-3-1104" }) Assert.False(PlanTrust.IsBroad(sid), sid);
    }

    [Fact] public void BroadSidsRemainRejectedWhenPresentInTrustStoreAndRightsMergeInContractOrder()
    {
        var broad = CheckAt(@"C:\Program Files\dirused", PathRole.Leaf, Acl(entries: new[] { Entry("S-1-1-0", "Everyone", WriteRights.Write) }), new[] { new TrustedAccount("S-1-1-0", "Everyone") });
        Assert.False(broad.Ok); Assert.Equal("  This group is too wide to trust; ask IT to remove this permission or move dirused.exe.", broad.Lines[1]);
        var rights = CheckAt(@"C:\Program Files\dirused", PathRole.Leaf, Acl(entries: new[] { Entry(ScheduleFakes.AdminSid, "admin", WriteRights.GenericWrite | WriteRights.Write | WriteRights.Append | WriteRights.DeleteChild) }));
        Assert.Equal("/schedule: C:\\Program Files\\dirused can be changed by admin (write, append, delete child, generic write).", rights.Lines[0]);
    }

    [Fact] public void TrustImplementationContainsNoOperatingSystemPathOrRegistryApis()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Dirused.Common", "PlanTrust.cs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var forbidden = new Regex(@"(?<![\w.])(File|Directory|Path|Registry|NTAccount|SecurityIdentifier|Process)\s*[.(]");
        var fullyQualified = new Regex(@"\bSystem\.IO\.(File|Directory|Path)\s*[.(]");
        foreach (var name in new[] { "PlanTrust.cs", "TrustCommands.cs" })
        {
            var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Dirused.Common", name));
            source = Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            Assert.Null(forbidden.Match(source).Success ? forbidden.Match(source) : fullyQualified.Match(source).Success ? fullyQualified.Match(source) : null);
        }
        Assert.Matches(fullyQualified, "System.IO.Path.GetDirectoryName(\"x\")");
    }
}
