using System.Text.Json;
using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class JsonRenderer(bool summary = false) : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        if (!summary)
        {
            output.WriteLine(JsonSerializer.Serialize(new { targets = results }, jsonOptions));
            return;
        }
        var (volumes, failed) = SummaryRenderer.Prepare(results);
        var combined = volumes.Concat(failed)
            .Select(r => r with { Directories = new List<Entry>(), Files = new List<Entry>() })
            .ToList();
        output.WriteLine(JsonSerializer.Serialize(new { summary = true, targets = combined }, jsonOptions));
    }
}
