using System.Text.RegularExpressions;

namespace Dirused.Common;

public enum PathRole { Leaf, Ancestor }
public sealed record TrustPath(string FullPath, PathRole Role, bool MayBeMissing);
public sealed record TrustCheck(bool Ok, IReadOnlyList<string> Lines);

public static class PlanTrust
{
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private static readonly HashSet<string> Broad = new(StringComparer.OrdinalIgnoreCase) {
        "S-1-1-0", "S-1-2-0", "S-1-5-2", "S-1-5-4", "S-1-5-7", "S-1-5-11", "S-1-5-13", "S-1-5-14", "S-1-5-15",
        "S-1-15-2-1", "S-1-5-32-545", "S-1-5-32-546", "S-1-5-32-555"
    };
    private static readonly Regex DomainBroad = new(@"\AS-1-5-21-[0-9-]+-(513|514|515)\z", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly WriteRights LeafRights = WriteRights.Write | WriteRights.Append | WriteRights.Delete | WriteRights.DeleteChild | WriteRights.WriteDac | WriteRights.WriteOwner | WriteRights.GenericWrite | WriteRights.GenericAll;
    private static readonly WriteRights AncestorRights = WriteRights.Delete | WriteRights.DeleteChild | WriteRights.WriteDac | WriteRights.WriteOwner | WriteRights.GenericAll;

    public static bool IsBroad(string sid) => Broad.Contains(sid) || DomainBroad.IsMatch(sid);

    public static IReadOnlyList<TrustPath>? PathsToCheck(string exePath, string planRoot, out string? error)
    {
        error = null;
        if (!DriveRooted(exePath)) { error = $"/schedule: dirused.exe must be on a local drive to schedule ({exePath})"; return null; }
        if (!DriveRooted(planRoot)) { error = $"/schedule: the plan folder must be on a local drive ({planRoot})"; return null; }
        string exe = Normalize(exePath), root = exe[..3], plan = Normalize(planRoot), exeDir = Parent(exe), planParent = Parent(plan);
        var paths = new List<TrustPath> { new(exe, PathRole.Leaf, false), new(exeDir, PathRole.Leaf, false) };
        AddAncestors(paths, Parent(exeDir), root);
        paths.Add(new(planParent, PathRole.Leaf, true));
        paths.Add(new(plan, PathRole.Leaf, true));
        AddAncestors(paths, Parent(planParent), root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TrustPath>();
        foreach (var p in paths)
        {
            int i = result.FindIndex(x => Same(x.FullPath, p.FullPath));
            if (i >= 0) { if (p.Role == PathRole.Leaf && result[i].Role != PathRole.Leaf) result[i] = p; continue; }
            if (seen.Add(p.FullPath)) result.Add(p);
        }
        return result;
    }

    public static TrustCheck Check(string exePath, string planRoot, string userSid, IReadOnlyCollection<TrustedAccount> trusted, IPlanStore store, IPlanFolderSecurity security)
    {
        var paths = PathsToCheck(exePath, planRoot, out var error);
        if (paths == null) return new(false, new[] { error! });
        var trustedSids = new HashSet<string>(trusted.Select(a => a.Sid), StringComparer.OrdinalIgnoreCase);
        var findings = new List<Finding>();
        for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            var path = paths[pathIndex];
            if (store.IsReparsePoint(path.FullPath)) return new(false, new[] { $"/schedule: {path.FullPath} is a link; dirused does not schedule through links" });
            var acl = security.ReadAcl(path.FullPath);
            if (!acl.Ok)
            {
                if (acl.Missing && path.MayBeMissing) continue;
                return new(false, new[] { $"/schedule: cannot read permissions of {path.FullPath}: {acl.Error ?? "not found"}" });
            }
            var mask = path.Role == PathRole.Leaf ? LeafRights : AncestorRights;
            foreach (var group in acl.Entries.Where(e => e.Allow && !e.InheritOnly && !SidEquals(e.Sid, "S-1-3-0") && !Allowed(e.Sid, userSid, trustedSids) && (e.Rights & mask) != 0).GroupBy(e => e.Sid, StringComparer.OrdinalIgnoreCase))
            {
                var rights = group.Aggregate(WriteRights.None, (r, e) => r | (e.Rights & mask));
                var entry = group.First();
                Merge(findings, new(path.FullPath, entry.Sid, Display(entry.Display, entry.Sid), rights, false, pathIndex), pathIndex);
            }
            if (acl.OwnerSid is { } owner && !AllowedOwner(owner, userSid, trustedSids))
                Merge(findings, new(path.FullPath, owner, Display(acl.OwnerDisplay, owner), WriteRights.None, true, pathIndex), pathIndex);
        }
        if (findings.Count == 0) return new(true, Array.Empty<string>());
        var ordered = findings.OrderBy(x => x.Path.Split('\\').Length).ThenBy(x => x.PathIndex).ThenBy(x => x.Sid, StringComparer.Ordinal).ToArray();
        var first = ordered[0];
        var lines = new List<string> { $"/schedule: {first.Path} can be changed by {first.Display} ({RightsText(first)})." };
        lines.Add(IsBroad(first.Sid) ? "  This group is too wide to trust; ask IT to remove this permission or move dirused.exe." : $"  If you trust this account, run once: dirused /schedule /trust \"{(first.Display == "unresolved account " + first.Sid ? first.Sid : first.Display)}\"");
        lines.AddRange(ordered.Skip(1).Select(x => $"  also: {x.Path} by {x.Display}"));
        return new(false, lines);
    }

    private sealed record Finding(string Path, string Sid, string Display, WriteRights Rights, bool Owner, int PathIndex);
    private static void Merge(List<Finding> findings, Finding f, int pathIndex)
    {
        var at = findings.FindIndex(x => Same(x.Path, f.Path) && string.Equals(x.Sid, f.Sid, StringComparison.OrdinalIgnoreCase));
        if (at < 0) { findings.Add(f); return; }
        var old = findings[at];
        findings[at] = old with { Rights = old.Rights | f.Rights, Owner = old.Owner || f.Owner, PathIndex = Math.Min(old.PathIndex, pathIndex) };
    }
    private static bool Allowed(string sid, string user, HashSet<string> trusted) => SidEquals(sid, user) || SidEquals(sid, "S-1-5-18") || SidEquals(sid, "S-1-5-32-544") || SidEquals(sid, TrustedInstaller) || SidEquals(sid, "S-1-3-0") || (trusted.Contains(sid) && !IsBroad(sid));
    private static bool AllowedOwner(string sid, string user, HashSet<string> trusted) => !SidEquals(sid, "S-1-3-0") && Allowed(sid, user, trusted);
    private static bool SidEquals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Display(string? display, string sid) => string.IsNullOrEmpty(display) ? "unresolved account " + sid : display;
    private static string RightsText(Finding f)
    {
        var rights = new List<string>();
        if ((f.Rights & WriteRights.GenericAll) != 0) rights.Add("full control");
        else foreach (var (bit, label) in new[] { (WriteRights.Write, "write"), (WriteRights.Append, "append"), (WriteRights.Delete, "delete"), (WriteRights.DeleteChild, "delete child"), (WriteRights.WriteDac, "change permissions"), (WriteRights.WriteOwner, "take ownership"), (WriteRights.GenericWrite, "generic write") }) if ((f.Rights & bit) != 0) rights.Add(label);
        if (f.Owner) rights.Add("owner");
        return string.Join(", ", rights);
    }
    private static bool DriveRooted(string p) => Regex.IsMatch(p, @"\A[A-Za-z]:\\");
    private static string Normalize(string p)
    {
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string normalized = p[..2] + "\\" + string.Join("\\", parts.Skip(1));
        return normalized.Length == 3 ? normalized : normalized.TrimEnd('\\');
    }
    private static void AddAncestors(List<TrustPath> paths, string path, string root)
    {
        for (var p = path; ; p = Parent(p)) { paths.Add(new(p, PathRole.Ancestor, false)); if (Same(p, root)) break; }
    }
    private static string Parent(string p) { int i = p.LastIndexOf('\\'); return i <= 2 ? p[..3] : p[..i]; }
    private static string Join(string p, string c) => p.TrimEnd('\\') + "\\" + c;
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
