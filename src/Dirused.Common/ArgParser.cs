using Dirused.Common.Targets;
namespace Dirused.Common;
public static class ArgParser
{
    public const string UsageText = "Usage: dirused <target>... [/top N] [/json|/csv]\nTargets: . | <drive>:\\path | <drive>:\\ | /all";
    public static ParseResult Parse(string[] args)
    {
        var targets = new List<string>(); int top = 50; bool json = false, csv = false;
        string? file = null; bool summary = false; TimeSpan? timeout = null; int? parallel = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i], name = arg.TrimStart('/', '-').ToLowerInvariant();
            if (arg.Equals("help", StringComparison.OrdinalIgnoreCase) || arg == "-h" || ((arg.StartsWith('/') || arg.StartsWith("--")) && (name == "help" || name == "?"))) return new(Usage: true);
            if (arg.StartsWith('/') || arg.StartsWith('-'))
            {
                switch (name)
                {
                    case "all": targets.Add("/all"); break;
                    case "top": if (++i >= args.Length || !int.TryParse(args[i], out top) || top < 1) return new(Error: "/top requires a positive integer"); break;
                    case "json": json = true; break;
                    case "csv": csv = true; break;
                    case "purge": case "plan": return new(Error: "not available yet (M2)");
                    case "monitor": case "threshold": return new(Error: "not available yet (M3)");
                    case "gui": return new(Error: "not available yet (GUI)");
                    case "node": return new(Error: "not available yet (M1b)");
                    case "file": file = i + 1 < args.Length ? args[++i] : ""; break;
                    case "summary": summary = true; break;
                    case "timeout":
                        if (i + 1 < args.Length) { var s = args[++i]; var suffix = s.Length > 0 ? s[^1] : '\0'; var number = char.IsLetter(suffix) ? s[..^1] : s; if (double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 0 && n <= TimeSpan.MaxValue.TotalSeconds / 3600) timeout = TimeSpan.FromSeconds(n * (suffix == 'h' ? 3600 : suffix == 'm' ? 60 : 1)); }
                        timeout ??= TimeSpan.Zero; break;
                    case "parallel": if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) { parallel = p; i++; } else parallel = 0; break;
                    default: return new(Error: $"Unknown switch: {arg}");
                }
                var unavailable = RejectUnavailable(new Options(targets, top, OutputFormat.Text, file, summary, timeout, parallel));
                if (unavailable != null) return new(Error: unavailable);
            }
            else if (TargetParser.Parse(arg) != null) targets.Add(arg);
            else return new(Error: $"Invalid target: {arg}");
        }
        if (targets.Count == 0 || (json && csv)) return new(Error: UsageText);
        return new(Options: new Options(targets, top, json ? OutputFormat.Json : csv ? OutputFormat.Csv : OutputFormat.Text, file, summary, timeout, parallel));
    }
    public static string? RejectUnavailable(Options options) => options.File != null || options.Summary || options.Timeout != null || options.Parallel != null ? "not available yet (M1b)" : null;
}
