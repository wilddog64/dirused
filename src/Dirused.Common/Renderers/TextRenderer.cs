using Dirused.Core;
namespace Dirused.Common.Renderers;
public sealed class TextRenderer(int top = 50, bool? exact = null, int? consoleWidth = null) : IRenderer
{
    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        int? effectiveWidth = consoleWidth;
        if (effectiveWidth == null && output == Console.Out)
        {
            try
            {
                if (!Console.IsOutputRedirected)
                    effectiveWidth = Console.WindowWidth;
            }
            catch
            {
                effectiveWidth = null;
            }
        }

        foreach (var r in results)
        {
            var v = r.Volume;
            output.WriteLine($"{v.Drive}  used {Format.Short(v.UsedBytes)}, free {Format.Short(v.FreeBytes)}, total {Format.Short(v.TotalBytes)}, {v.PercentUsed:F1}% used");

            var rows = r.Directories.Concat(r.Files)
                .OrderByDescending(x => x.LogicalBytes)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Take(top)
                .ToList();

            if (rows.Count > 0)
            {
                int maxPathLen = rows.Max(x => x.Path.Length);
                int pathColWidth = maxPathLen + 2;
                var sizeStrings = rows.Select(x => Format.Short(x.LogicalBytes)).ToList();
                int maxSizeLen = sizeStrings.Max(s => s.Length);

                bool isExact = exact ?? r.Exact;
                long cluster = r.Volume.ClusterSize > 0 ? r.Volume.ClusterSize : 4096;

                for (int i = 0; i < rows.Count; i++)
                {
                    var e = rows[i];
                    var sizeStr = sizeStrings[i];
                    var paddedSize = sizeStr.PadLeft(maxSizeLen);
                    string extra = "";
                    if (isExact && Math.Abs(e.OnDiskBytes - e.LogicalBytes) > cluster)
                    {
                        extra = $" ({Format.Short(e.OnDiskBytes)} on disk)";
                    }

                    string paddedRow = e.Path.PadRight(pathColWidth) + paddedSize + extra;
                    if (effectiveWidth != null && paddedRow.Length > effectiveWidth.Value)
                    {
                        output.WriteLine($"{e.Path}  {sizeStr}{extra}");
                    }
                    else
                    {
                        output.WriteLine(paddedRow);
                    }
                }
            }

            if (!r.Complete)
            {
                output.WriteLine($"INCOMPLETE: {r.Errors.Count} directories could not be read");
                foreach (var e in r.Errors.Take(10)) output.WriteLine($"{e.Path}: {e.Reason}");
            }
        }
    }
}
