using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class CsvRenderer : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        output.WriteLine("kind,target,path,logical_bytes,on_disk_bytes");
        foreach (var r in results) { foreach (var e in r.Directories) Format.Row(output, "directory", r.Target, e); foreach (var e in r.Files) Format.Row(output, "file", r.Target, e); }
    }
}
