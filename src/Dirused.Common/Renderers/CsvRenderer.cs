using System.Globalization;
using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class CsvRenderer(bool summary = false) : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        if (!summary)
        {
            output.WriteLine("kind,target,path,logical_bytes,on_disk_bytes");
            foreach (var r in results) { foreach (var e in r.Directories) Format.Row(output, "directory", r.Target, e); foreach (var e in r.Files) Format.Row(output, "file", r.Target, e); }
            return;
        }
        output.WriteLine("kind,target,used_bytes,free_bytes,total_bytes,percent_used");
        var (volumes, failed) = SummaryRenderer.Prepare(results);
        foreach (var r in volumes)
        {
            var v = r.Volume;
            string label = string.IsNullOrEmpty(v.Drive) ? r.Target : v.Drive;
            output.WriteLine($"volume,{Format.Quote(label)},{v.UsedBytes.ToString(CultureInfo.InvariantCulture)},{v.FreeBytes.ToString(CultureInfo.InvariantCulture)},{v.TotalBytes.ToString(CultureInfo.InvariantCulture)},{v.PercentUsed.ToString("F1", CultureInfo.InvariantCulture)}");
        }
        foreach (var f in failed)
        {
            output.WriteLine($"error,{Format.Quote(f.Target)},0,0,0,0.0");
        }
    }
}
