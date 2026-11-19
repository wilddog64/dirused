using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class SummaryRenderer(int warn = 85, int crit = 90, bool color = false) : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        var (volumes, failed) = Prepare(results);
        foreach (var r in volumes)
        {
            var v = r.Volume;
            string label = string.IsNullOrEmpty(v.Drive) ? r.Target : v.Drive;
            var level = Fullness.For(r, warn, crit);
            Fullness.WriteLine(output, $"{label} used {Format.Size(v.UsedBytes)}, free {Format.Size(v.FreeBytes)}, total {Format.Size(v.TotalBytes)}, {v.PercentUsed:F1}% used{Fullness.Tag(level)}", level, color);
            var directories = r.Directories.OrderByDescending(e => e.LogicalBytes).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(TopFolders.Count).ToList();
            int width = directories.Select(e => Format.Size(e.LogicalBytes).Length).DefaultIfEmpty(0).Max();
            foreach (var directory in directories) output.WriteLine($"    {Format.Size(directory.LogicalBytes).PadLeft(width)}  {directory.Path}");
            if (r.TopFoldersNote != null) output.WriteLine($"    top folders incomplete: {r.TopFoldersNote}");
            TextRenderer.WriteLargest(output, r.Largest, Format.Size);
        }
        foreach (var f in failed)
        {
            string reason = f.Errors.FirstOrDefault()?.Reason ?? "Unknown error";
            output.WriteLine($"{f.Target}: {reason}");
        }
        if (volumes.Count > 0)
        {
            int c = volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Crit);
            int w = volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Warn);
            output.WriteLine($"{volumes.Count} volume(s): {c} at or above {crit}%, {w} at or above {warn}%");
        }
    }

    internal static (IReadOnlyList<ScanResult> Volumes, IReadOnlyList<ScanResult> Failed) Prepare(IReadOnlyList<ScanResult> results)
    {
        var volumes = new List<ScanResult>();
        var failed = new List<ScanResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (r.Largest?.Files.Any(f => f.FromList) == true) continue;
            if (!r.Complete)
            {
                failed.Add(r);
                continue;
            }
            if (seen.Add(VolumeKey(r))) volumes.Add(r);
        }
        return (volumes.OrderByDescending(r => r.Volume.PercentUsed).ToList(), failed);
    }

    internal static string VolumeKey(ScanResult r)
    {
        string host = ExtractHost(r.Target);
        return r.Volume.SerialNumber != 0
            ? $"{host}:serial:{r.Volume.SerialNumber}"
            : $"{host}:drive:{r.Volume.Drive}";
    }

    internal static string ExtractHost(string target)
    {
        if (string.IsNullOrEmpty(target)) return "";
        string t = target;
        if (t.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            t = @"\\" + t[8..];
        else if (t.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase))
            t = @"//" + t[8..];
        if (t.StartsWith(@"\\", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal))
        {
            int slash = t.IndexOfAny(new[] { '\\', '/' }, 2);
            return slash >= 0 ? t[2..slash] : t[2..];
        }
        return "";
    }
}
