using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class TextRenderer : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        foreach (var r in results)
        {
            var v = r.Volume; output.WriteLine($"{v.Drive} used {Format.Size(v.UsedBytes)}, free {Format.Size(v.FreeBytes)}, total {Format.Size(v.TotalBytes)}, {v.PercentUsed:F1}% used");
            output.WriteLine("Largest directories"); foreach (var e in r.Directories) Print(output, e);
            output.WriteLine("Largest files"); foreach (var e in r.Files) Print(output, e);
            if (!r.Complete) { output.WriteLine($"INCOMPLETE: {r.Errors.Count} directories could not be read"); foreach (var e in r.Errors.Take(10)) output.WriteLine($"{e.Path}: {e.Reason}"); }
        }
    }
    private static void Print(TextWriter w, Entry e) => w.WriteLine($"{e.Path}  logical {Format.Size(e.LogicalBytes)}  on disk {Format.Size(e.OnDiskBytes)}");
}
