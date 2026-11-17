using System.ComponentModel;
using Dirused.Common.Targets;
namespace Dirused.Core.Targets;
public record ResolvedTarget(Target Target, string? Error = null);
public static class TargetResolver
{
    public static IReadOnlyList<Target> Resolve(IEnumerable<string> typed, out IReadOnlyList<string> errors)
    {
        var resolved = ResolveDetailed(typed);
        errors = resolved.Where(x => x.Error != null).Select(x => x.Error!).ToList();
        return resolved.Where(x => x.Error == null).Select(x => x.Target).ToList();
    }
    public static IReadOnlyList<ResolvedTarget> ResolveDetailed(IEnumerable<string> typed, INodeShares? nodeShares = null)
    {
        nodeShares ??= new NodeShares();
        var typedList = typed.ToList();
        bool hasAll = typedList.Any(x => x.Equals("/all", StringComparison.OrdinalIgnoreCase));
        var otherTargets = typedList.Where(x => !x.Equals("/all", StringComparison.OrdinalIgnoreCase)).ToList();

        if (hasAll && otherTargets.Count == 0)
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => new ResolvedTarget(TargetParser.Parse(d.Name)!))
                .ToList();
        }

        var result = new List<ResolvedTarget>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in typedList)
        {
            if (value.Equals("/all", StringComparison.OrdinalIgnoreCase)) continue;

            var target = TargetParser.Parse(value);
            if (target == null)
            {
                result.Add(new(new Target(value, TargetKind.LocalPath, "", value), $"Invalid target: {value}"));
                continue;
            }

            if (hasAll)
            {
                if (target.Kind == TargetKind.Node || target.Kind == TargetKind.Unc)
                {
                    int slash = target.Path.IndexOf('\\', 2);
                    string host = slash >= 0 ? target.Path[2..slash] : target.Path[2..];
                    if (seenHosts.Add(host))
                    {
                        var nodeTarget = TargetParser.Parse(@"\\" + host)!;
                        EnumerateHost(nodeTarget, nodeShares, result);
                    }
                    continue;
                }
                result.Add(new(target));
            }
            else
            {
                if (target.Kind != TargetKind.Node)
                {
                    result.Add(new(target));
                    continue;
                }
                EnumerateHost(target, nodeShares, result);
            }
        }
        return result;
    }

    private static void EnumerateHost(Target nodeTarget, INodeShares nodeShares, List<ResolvedTarget> result)
    {
        try
        {
            var shares = nodeShares.Enumerate(nodeTarget.Path)
                .Where(s => (s.Type & 0x80000000u) != 0 && (s.Type & 0xffu) == 0 && s.Name.Length == 2 && char.IsAsciiLetter(s.Name[0]) && s.Name[1] == '$')
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (shares.Count == 0) result.Add(new(nodeTarget, $"NO DRIVES: {nodeTarget.Display}"));
            foreach (var share in shares) result.Add(new(TargetParser.Parse(nodeTarget.Path + @"\" + share.Name + @"\")!));
        }
        catch (Win32Exception ex) { result.Add(new(nodeTarget, ErrorMessage(nodeTarget.Display, ex.NativeErrorCode))); }
    }
    public static string ErrorMessage(string target, int code) => code switch
    {
        5 => $"ACCESS DENIED: {target} — admin shares need an administrator on the target",
        51 or 53 or 64 or 67 or 1231 or 1707 => $"UNREACHABLE: {target} ({code})",
        _ => $"ERROR: {target} ({code})"
    };
}
