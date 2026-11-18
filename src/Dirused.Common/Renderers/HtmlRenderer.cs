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
        output.WriteLine("<style>:root{font:16px system-ui,sans-serif;color:#202124;background:#fff}body{max-width:1200px;margin:2rem auto;padding:0 1rem}h1{margin-bottom:.2rem}small{color:#666}.scroll{overflow-x:auto;margin:1rem 0}table{border-collapse:collapse;width:100%;min-width:680px}th,td{padding:.55rem .7rem;border-bottom:1px solid #ddd;text-align:left;vertical-align:top}th{background:#f2f4f7}td.num{text-align:right;white-space:nowrap}td.date{white-space:nowrap}.crit{background:#fee2e2}.warn{background:#fef3c7}.bar{display:inline-block;width:90px;height:.65rem;background:#ddd;border-radius:1rem;margin-left:.5rem;overflow:hidden}.bar>span{display:block;height:100%;min-width:3px;background:#4b83d1}.crit .bar>span{background:#d93025}.warn .bar>span{background:#f9ab00}.ok .bar>span{background:#1e8e3e}.lvl{font-weight:600;border-radius:.3rem;padding:.1rem .45rem}.crit .lvl{color:#fff;background:#d93025}.warn .lvl{color:#202124;background:#fbbc04}.ok .lvl{color:#fff;background:#1e8e3e}section{margin-top:2rem}code{overflow-wrap:anywhere}@media(prefers-color-scheme:dark){:root{color:#eee;background:#171717}small{color:#bbb}th{background:#292929}td,th{border-color:#444}.crit{background:#542b2b}.warn{background:#51451f}.bar{background:#444}.bar>span{background:#8ab4f8}.ok .bar>span{background:#81c995}.ok .lvl{color:#202124;background:#81c995}}</style></head><body>");
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
            int nodeCrit = host.Volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Crit);
            int nodeWarn = host.Volumes.Count(r => Fullness.For(r, warn, crit) == FullnessLevel.Warn);
            string nodeCls = nodeCrit > 0 ? " class=\"crit\"" : nodeWarn > 0 ? " class=\"warn\"" : "";
            output.WriteLine($"<tr{nodeCls}><td>{E(host.Host)}</td><td class=\"num\">{host.Volumes.Count}</td><td class=\"num\">{nodeCrit}</td><td class=\"num\">{nodeWarn}</td><td class=\"num\">{E(highest)}</td></tr>");
        }
        output.WriteLine("</tbody></table></div>");

        output.WriteLine("<h2>Volumes</h2><div class=\"scroll\"><table class=\"volumes\"><thead><tr><th>Host</th><th>Volume</th><th>Used</th><th>Free</th><th>Total</th><th>% used</th><th>Level</th><th>Top folders</th></tr></thead><tbody>");
        foreach (var r in volumes)
        {
            var v = r.Volume;
            string level = Fullness.For(r, warn, crit) switch { FullnessLevel.Crit => "CRIT", FullnessLevel.Warn => "WARN", FullnessLevel.Ok => "OK", _ => "" };
            string cls = level == "" ? "" : $" class=\"{level.ToLowerInvariant()}\"";
            string levelCell = level == "" ? "" : "<span class=\"lvl\">" + level + "</span>";
            string pct = v.PercentUsed.ToString("F1", CultureInfo.InvariantCulture);
            double clampedPercent = Math.Clamp(v.PercentUsed, 0, 100);
            string bar = clampedPercent.ToString("0.##", CultureInfo.InvariantCulture);
            string barFill = clampedPercent == 0 ? "" : $"<span style=\"width:{bar}%\"></span>";
            var folders = summary
                ? r.TopFoldersNote != null ? $"incomplete: {E(r.TopFoldersNote)}" : string.Join("<br>", r.Directories.OrderByDescending(e => e.LogicalBytes).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(TopFolders.Count).Select(e => $"{E(Format.Size(e.LogicalBytes))} {E(e.Path)}"))
                : "";
            output.WriteLine($"<tr{cls}><td>{E(Host(r))}</td><td>{E(string.IsNullOrEmpty(v.Drive) ? r.Target : v.Drive)}</td><td class=\"num\">{E(Format.Size(v.UsedBytes))}</td><td class=\"num\">{E(Format.Size(v.FreeBytes))}</td><td class=\"num\">{E(Format.Size(v.TotalBytes))}</td><td class=\"num\">{E(pct)}% <span class=\"bar\">{barFill}</span></td><td>{levelCell}</td><td>{folders}</td></tr>");
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
        var largestGroups = (summary ? volumes : results).Where(r => r.Largest != null).ToList();
        if (largestGroups.Count > 0)
        {
            bool purge = largestGroups.Any(r => r.Largest!.Files.Any(f => f.PurgeStatus != null));
            output.WriteLine("<h2>Largest candidate files</h2><div class=\"scroll\"><table class=\"largest\"><thead><tr><th>Host</th><th>Size</th><th>Modified</th><th>Path</th>" + (purge ? "<th>Purge</th>" : "") + "</tr></thead><tbody>");
            foreach (var r in largestGroups)
            {
                var l = r.Largest!;
                output.WriteLine($"<tr class=\"group\"><th colspan=\"{(purge ? 5 : 4)}\">{E(Host(r))} {E(r.Target)}: {l.Files.Count} of {l.Matches.ToString("N0", CultureInfo.InvariantCulture)} matches, {E(Format.Size(l.MatchedBytes))}{(l.PurgeBytes is long b ? (l.PurgeDryRun == true ? ", would free " : ", freed ") + E(Format.Size(b)) : "")}{(l.Complete ? "" : " (scan incomplete)")}</th></tr>");
                var maxBytes = l.Files.Count == 0 ? 0 : l.Files.Max(f => f.LogicalBytes);
                foreach (var f in l.Files)
                {
                    string cls = f.LogicalBytes >= 10L * 1024 * 1024 * 1024 ? "crit" : f.LogicalBytes >= 1L * 1024 * 1024 * 1024 ? "warn" : "";
                    string rowClass = cls == "" ? "" : $" class=\"{cls}\"";
                    double pct = maxBytes <= 0 || f.LogicalBytes <= 0 ? 0 : f.LogicalBytes * 100.0 / maxBytes;
                    string bar = pct.ToString("0.##", CultureInfo.InvariantCulture);
                    string barFill = pct == 0 ? "" : $"<span style=\"width:{bar}%\"></span>";
                    output.WriteLine($"<tr{rowClass}><td>{E(Host(r))}</td><td class=\"num\">{E(Format.Size(f.LogicalBytes))}<span class=\"bar\">{barFill}</span></td><td class=\"date\">{E(f.LastWriteUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-")}</td><td>{E(f.Path)}</td>" + (purge ? $"<td class=\"purge\">{E(f.PurgeStatus)}</td>" : "") + "</tr>");
                }
            }
            output.WriteLine("</tbody></table></div>");
        }
        output.WriteLine("</body></html>");
    }

    private static string Host(ScanResult result)
    {
        string host = SummaryRenderer.ExtractHost(result.Target);
        return host.Length == 0 ? Environment.MachineName : host;
    }
}
