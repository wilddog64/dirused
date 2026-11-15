using Dirused.Core;
namespace Dirused.Common.Renderers;
public interface IRenderer { void Render(TextWriter output, IReadOnlyList<ScanResult> results); }
