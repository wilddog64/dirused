using Dirused.Common.Targets;
namespace Dirused.Core.Targets;
public static class TargetResolver
{
    public static IReadOnlyList<Target> Resolve(IEnumerable<string> typed, out IReadOnlyList<string> errors)
    {
        var result = new List<Target>(); var failures = new List<string>();
        foreach (var value in typed)
        {
            if (value == "/all") { result.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => TargetParser.Parse(d.Name)!)); continue; }
            var target = TargetParser.Parse(value);
            if (target == null) failures.Add($"Invalid target: {value}");
            else if (target.Kind is TargetKind.Unc or TargetKind.Node) failures.Add("UNC targets arrive in M1b");
            else result.Add(target);
        }
        errors = failures; return result;
    }
}
