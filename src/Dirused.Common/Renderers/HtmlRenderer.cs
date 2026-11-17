using System.Globalization;
using System.Net;
using Dirused.Core;

namespace Dirused.Common.Renderers;

public sealed class HtmlRenderer(bool summary, int warn, int crit, int top) : IRenderer
{
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public void Render(TextWriter output, IReadOnlyList<ScanResult> results)
    {
        var (volumes, failed) = SummaryRenderer.Prepare(results);
        output.WriteLine("<!doctype html>");
        output.WriteLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>dirused report</title>");
        output.WriteLine("<style>:root{font:16px system-ui,sans-serif;color:#202124;background:#fff}body{max-width:1200px;margin:2rem auto;padding:0 1rem}h1{margin-bottom:.2rem}small{color:#666}.scroll{overflow-x:auto;margin:1rem 0}table{border-collapse:collapse;width:100%;min-width:680px}th,td{padding:.55rem .7rem;border-bottom:1px solid #ddd;text-align:left;vertical-align:top}th{background:#f2f4f7}td.num{text-align:right;white-space:nowrap}.crit{background:#fee2e2}.warn{background:#fef3c7}.bar{display:inline-block;width:90px;height:.65rem;background:#ddd;border-radius:1rem;margin-left:.5rem;overflow:hidden}.bar>span{display:block;height:100%;background:#4b83d1}section{margin-top:2rem}code{overflow-wrap:anywhere}@media(prefers-color-scheme:dark){:root{color:#eee;background:#171717}small{color:#bbb}th{background:#292929}td,th{border-color:#444}.crit{background:#542b2b}.warn{background:#51451f}.bar{background:#444}.bar>span{background:#8ab4f8}}</style></head><body>");
        output.WriteLine("<h1>dirused report</h1>");
        output.WriteLine($"<p>Generated {E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))} on {E(Environment.MachineName)} · warn {warn}% / crit {crit}%</p>");

        var hostVolumes = volumes.GroupBy(Host, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var hosts = results.Select(Host).Distinct(StringComparer.OrdinalIgnoreCase).Select(host => new
        {
            Host = host,
            Volumes = hostVolumes.TryGetValue(host, out var list) ? list : new List<ScanResult>()
        }).OrderByDescending(x => x.Volumes.Count > 0 ? x.Volumes.Max(r => r.Volume.PercentUsed) : double.NegativeInfinity).ToList();

        output.WriteLine("<h2>Nodes</h2><div class=\"scroll\"><table class=\"nodes\"><thead><tr><th>Host</th><th>Volumes</th><th>Crit</th><th>Warn</th><th>Highest % used</th></tr></thead><tbody>");
        foreach (var host in hosts)
        {
            string highest = host.Volumes.Count == 0 ? "failed" : host.Volumes.Max(r => r.Volume.PercentUsed).ToString("F1", CultureInfo.InvariantCulture) + "%";
            output.WriteLine($"<tr><td>{E(host.Host)}</td><td class=\"num\">{host.Volumes.Count}</td><td class=\"num\">{host.Volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Crit)}</td><td class=\"num\">{host.Volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Warn)}</td><td class=\"num\">{E(highest)}</td></tr>");
        }
        output.WriteLine("</tbody></table></div>");

        output.WriteLine("<h2>Volumes</h2><div class=\"scroll\"><table class=\"volumes\"><thead><tr><th>Host</th><th>Volume</th><th>Used</th><th>Free</th><th>Total</th><th>% used</th><th>Level</th><th>Top folders</th></tr></thead><tbody>");
        foreach (var r in volumes)
        {
            var v = r.Volume;
            string level = Fullness.For(r, warn, crit) switch { FullnessLevel.Crit => "CRIT", FullnessLevel.Warn => "WARN", _ => "" };
            string cls = level == "" ? "" : $" class=\"{level.ToLowerInvariant()}\"";
            string pct = v.PercentUsed.ToString("F1", CultureInfo.InvariantCulture);
            string bar = Math.Clamp(v.PercentUsed, 0, 100).ToString("0.##", CultureInfo.InvariantCulture);
            var folders = summary
                ? r.TopFoldersNote != null ? $"incomplete: {E(r.TopFoldersNote)}" : string.Join("<br>", r.Directories.OrderByDescending(e => e.LogicalBytes).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(TopFolders.Count).Select(e => $"{E(Format.Size(e.LogicalBytes))} {E(e.Path)}"))
                : "";
            output.WriteLine($"<tr{cls}><td>{E(Host(r))}</td><td>{E(string.IsNullOrEmpty(v.Drive) ? r.Target : v.Drive)}</td><td class=\"num\">{E(Format.Size(v.UsedBytes))}</td><td class=\"num\">{E(Format.Size(v.FreeBytes))}</td><td class=\"num\">{E(Format.Size(v.TotalBytes))}</td><td class=\"num\">{E(pct)}% <span class=\"bar\"><span style=\"width:{bar}%\"></span></span></td><td>{level}</td><td>{folders}</td></tr>");
        }
        output.WriteLine("</tbody></table></div>");

        if (failed.Count > 0)
        {
            output.WriteLine("<h2>Failures</h2><div class=\"scroll\"><table class=\"failed\"><thead><tr><th>Host</th><th>Target</th><th>Reason</th></tr></thead><tbody>");
            foreach (var r in failed) output.WriteLine($"<tr><td>{E(Host(r))}</td><td>{E(r.Target)}</td><td>{E(r.Errors.FirstOrDefault()?.Reason ?? "Unknown error")}</td></tr>");
            output.WriteLine("</tbody></table></div>");
        }
        if (volumes.Count > 0)
        {
            int c = volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Crit);
            int w = volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Warn);
            output.WriteLine($"<p>{volumes.Count} volume(s): {c} at or above {crit}%, {w} at or above {warn}%</p>");
        }
        if (!summary)
            foreach (var r in results.Where(r => r.Complete))
            {
                output.WriteLine($"<section><h2>{E(Host(r))} — {E(r.Target)}</h2><div class=\"scroll\"><table><thead><tr><th>Size</th><th>Path</th></tr></thead><tbody>");
                foreach (var entry in r.Directories.Concat(r.Files).OrderByDescending(e => e.LogicalBytes).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(top))
                    output.WriteLine($"<tr><td class=\"num\">{E(Format.Size(entry.LogicalBytes))}</td><td>{E(entry.Path)}</td></tr>");
                output.WriteLine("</tbody></table></div></section>");
            }
        output.WriteLine("</body></html>");
    }

    private static string Host(ScanResult result)
    {
        string host = SummaryRenderer.ExtractHost(result.Target);
        return host.Length == 0 ? Environment.MachineName : host;
    }
}
