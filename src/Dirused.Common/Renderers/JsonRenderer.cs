using System.Text.Json;
using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class JsonRenderer : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results) => output.WriteLine(JsonSerializer.Serialize(new { targets = results }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
}
