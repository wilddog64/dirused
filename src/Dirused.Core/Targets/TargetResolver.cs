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
        var result = new List<ResolvedTarget>();
        foreach (var value in typed)
        {
            if (value == "/all") { result.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => new ResolvedTarget(TargetParser.Parse(d.Name)!))); continue; }
            var target = TargetParser.Parse(value);
            if (target == null) { result.Add(new(new Target(value, TargetKind.LocalPath, "", value), $"Invalid target: {value}")); continue; }
            if (target.Kind != TargetKind.Node) { result.Add(new(target)); continue; }
            try
            {
                var shares = nodeShares.Enumerate(target.Path).Where(s => (s.Type & 0x80000000u) != 0 && (s.Type & 0xffu) == 0 && s.Name.Length == 2 && char.IsAsciiLetter(s.Name[0]) && s.Name[1] == '$').OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (shares.Count == 0) result.Add(new(target, $"NO DRIVES: {target.Display}"));
                foreach (var share in shares) result.Add(new(TargetParser.Parse(target.Path + @"\" + share.Name + @"\")!));
            }
            catch (Win32Exception ex) { result.Add(new(target, ErrorMessage(target.Display, ex.NativeErrorCode))); }
        }
        return result;
    }
    public static string ErrorMessage(string target, int code) => code switch
    {
        5 => $"ACCESS DENIED: {target} — admin shares need an administrator on the target",
        53 or 67 or 1231 => $"UNREACHABLE: {target} ({code})",
        _ => $"ERROR: {target} ({code})"
    };
}
