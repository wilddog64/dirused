using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class SummaryRenderer : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        var (volumes, failed) = Prepare(results);
        foreach (var r in volumes)
        {
            var v = r.Volume;
            string label = string.IsNullOrEmpty(v.Drive) ? r.Target : v.Drive;
            output.WriteLine($"{label} used {Format.Size(v.UsedBytes)}, free {Format.Size(v.FreeBytes)}, total {Format.Size(v.TotalBytes)}, {v.PercentUsed:F1}% used");
        }
        foreach (var f in failed)
        {
            string reason = f.Errors.FirstOrDefault()?.Reason ?? "Unknown error";
            output.WriteLine($"{f.Target}: {reason}");
        }
    }

    internal static (IReadOnlyList<ScanResult> Volumes, IReadOnlyList<ScanResult> Failed) Prepare(IReadOnlyList<ScanResult> results)
    {
        var volumes = new List<ScanResult>();
        var failed = new List<ScanResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (!r.Complete)
            {
                failed.Add(r);
                continue;
            }
            string key = r.Volume.SerialNumber != 0
                ? $"serial:{r.Volume.SerialNumber}"
                : $"drive:{r.Volume.Drive}";
            if (seen.Add(key)) volumes.Add(r);
        }
        return (volumes.OrderByDescending(r => r.Volume.PercentUsed).ToList(), failed);
    }
}
