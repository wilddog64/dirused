using System.Globalization;
using System.Security;
using System.Text;
using Dirused.Common.Targets;
namespace Dirused.Common;
public static class ArgParser
{
    public static bool IsFlag(string arg, string name)
    {
        if (string.IsNullOrEmpty(arg) || !(arg.StartsWith('/') && !arg.StartsWith("//", StringComparison.Ordinal) || arg.StartsWith('-'))) return false;
        var flag = arg.TrimStart('/', '-');
        return flag.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    public const string ShortUsageText =
        "Usage: dirused <target>... [/file <path>] [/summary] [/deep [N]] [/exact]\n" +
        "               [/top N] [/json|/csv] [/html] [/output <path>]\n" +
        "               [/warn [1-100]] [/crit [1-100]]\n" +
        "Run dirused /help for more.";

    public const string DefaultHtmlPath = @"C:\temp\output.html";

    public const string UsageText =
        "Usage: dirused [target...] [switches]\n" +
        "\n" +
        "Targets:\n" +
        "  . | <rel>           Current or relative path\n" +
        "  C:\\x | c:/x | C:    Local path or drive root\n" +
        "  \\\\server\\share      UNC share (or //server/share)\n" +
        "  \\\\node | //node     All admin shares on a host\n" +
        "  /all                All local drives, or every drive on each target host\n" +
        "\n" +
        "Switches (use /, - or -- in any case):\n" +
        "  /top <n>            Show top N entries (default: 50)\n" +
        "  /json               Output JSON (default: text)\n" +
        "  /csv                Output CSV (default: text)\n" +
        "  /html               Also write an HTML report\n" +
        "                      (default file: C:\\temp\\output.html)\n" +
        "  /output <path>      Write the report to <path>: the HTML file with /html,\n" +
        "                      otherwise the text, JSON or CSV output (default: console)\n" +
        "  /all                All local drives, or every drive on each target host\n" +
        "  /file <path>        Read targets from file, one per line (default: none)\n" +
        "  /summary            Volume usage; a /warn or /crit volume also lists\n" +
        "                      its top 3 folders (default: off)\n" +
        "  /warn [n]           Warning fullness threshold (default: 85)\n" +
        "  /crit [n]           Critical fullness threshold (default: 90)\n" +
        "  /deep [n]           List n directory levels, 1-32 (/deep alone: 2)\n" +
        "  /exact              On-disk sizes and hard-link dedup (slower)\n" +
        "  /largest [patterns] Largest matching files under each target\n" +
        "                      (README defaults; + adds; plain list replaces; * all)\n" +
        "  /largestcount <n>   How many /largest files per target, 1-1000 (default: 20)\n" +
        "  /timeout <n>[s|m|h] Per-target timeout, > 0 (default: none)\n" +
        "  /parallel <n>       Max concurrent scans, 1-64 (default: 4)\n" +
        "  /help               Show this help\n" +
        "  /version            Show version and architecture\n" +
        "  /update             Download and install the latest release\n" +
        "  /noupdatecheck      Do not check for updates during this run\n" +
        "\n" +
        "Exit codes:\n" +
        "  0  All targets completed\n" +
        "  1  Usage error or unreadable target file\n" +
        "  3  Any target incomplete, failed, or timed out\n" +
        "  4  Any volume at or above /crit (takes precedence over 3)";

    public const string MultiTargetWarning =
        "dirused: /deep and /exact walk every target in full; add /timeout <n> to bound each one, or use /summary for a quick fleet check";

    public static bool WarnIfMultiTarget(int targetCount, Options options, TextWriter error)
    {
        if (targetCount > 1 && (options.DeepGiven || options.Exact) && options.Timeout == null)
        {
            error.WriteLine(MultiTargetWarning);
            return true;
        }
        return false;
    }

    public static ParseResult Parse(string[] args)
    {
        var targets = new List<string>(); int top = 50; bool json = false, csv = false, html = false;
        string? output = null;
        string? file = null; bool summary = false; TimeSpan? timeout = null; int? parallel = null;
        int deep = 1; bool deepGiven = false; bool exact = false; int warn = 85, crit = 90;
        IReadOnlyList<string>? largest = null; bool largestGiven = false; int largestCount = 20;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i], name = arg.TrimStart('/', '-').ToLowerInvariant();
            if (arg.Equals("help", StringComparison.OrdinalIgnoreCase) || arg == "-h" || ((arg.StartsWith('/') && !arg.StartsWith("//", StringComparison.Ordinal) || arg.StartsWith('-')) && (name == "help" || name == "?"))) return new(Usage: true);
            if ((arg.StartsWith('/') && !arg.StartsWith("//", StringComparison.Ordinal)) || arg.StartsWith('-'))
            {
                switch (name)
                {
                    case "all": targets.Add("/all"); break;
                    case "noupdatecheck": break;
                    case "top": if (++i >= args.Length || !int.TryParse(args[i], out top) || top < 1) return new(Error: "/top requires a positive integer"); break;
                    case "json": json = true; break;
                    case "csv": csv = true; break;
                    case "html": html = true; break;
                    case "output":
                        if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith('-') || (args[i].StartsWith('/') && !args[i].StartsWith("//", StringComparison.Ordinal))) return new(Error: "/output requires a path");
                        output = args[i]; break;
                    case "purge": case "plan": return new(Error: "not available yet (M2)");
                    case "monitor": case "threshold": return new(Error: "not available yet (M3)");
                    case "gui": return new(Error: "not available yet (GUI)");
                    case "node": return new(Error: "not available yet (M1b)");
                    case "file":
                        if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i])) return new(Error: "/file requires a path");
                        if (args[i].StartsWith('-') || (args[i].StartsWith('/') && !args[i].StartsWith("//", StringComparison.Ordinal)))
                            return new(Error: $"/file requires a file name before the next switch (got '{args[i]}')");
                        file = args[i]; break;
                    case "summary": summary = true; break;
                    case "warn":
                        if (i + 1 < args.Length && args[i + 1].Length > 0 && args[i + 1].All(char.IsAsciiDigit))
                        {
                            if (!int.TryParse(args[++i], out warn) || warn < 1 || warn > 100) return new(Error: "/warn requires an integer between 1 and 100");
                        }
                        break;
                    case "crit":
                        if (i + 1 < args.Length && args[i + 1].Length > 0 && args[i + 1].All(char.IsAsciiDigit))
                        {
                            if (!int.TryParse(args[++i], out crit) || crit < 1 || crit > 100) return new(Error: "/crit requires an integer between 1 and 100");
                        }
                        break;
                    case "deep":
                        deepGiven = true;
                        if (i + 1 < args.Length)
                        {
                            string next = args[i + 1];
                            if (next.Length > 0 && next.All(char.IsAsciiDigit))
                            {
                                i++;
                                if (int.TryParse(next, out var d) && d >= 1 && d <= 32)
                                    deep = d;
                                else
                                    return new(Error: "/deep level must be an integer between 1 and 32");
                            }
                            else
                            {
                                deep = 2;
                            }
                        }
                        else
                        {
                            deep = 2;
                        }
                        break;
                    case "exact": exact = true; break;
                    case "largest":
                        largestGiven = true;
                        string? patternArg = null;
                        if (i + 1 < args.Length)
                        {
                            string next = args[i + 1];
                            bool isSwitch = (next.StartsWith('/') && !next.StartsWith("//", StringComparison.Ordinal)) || next.StartsWith('-');
                            if (!isSwitch && (next.IndexOfAny(new[] { '*', '?', ';', ',' }) >= 0 || next.StartsWith('+'))) { patternArg = next; i++; }
                        }
                        if (!Largest.ParsePatterns(patternArg, out largest, out var patternError)) return new(Error: patternError);
                        break;
                    case "largestcount":
                        if (++i >= args.Length || !int.TryParse(args[i], out largestCount) || largestCount < 1 || largestCount > 1000)
                            return new(Error: "/largestcount requires an integer from 1 to 1000");
                        break;
                    case "timeout":
                        if (++i >= args.Length || !TryParseTimeout(args[i], out var t))
                            return new(Error: "/timeout requires a positive duration (e.g. 30, 30s, 5m, 1h)");
                        timeout = t; break;
                    case "parallel":
                        if (++i >= args.Length || !int.TryParse(args[i], out var p) || p < 1 || p > 64)
                            return new(Error: "/parallel requires an integer between 1 and 64");
                        parallel = p; break;
                    default: return new(Error: $"Unknown switch: {arg}");
                }
                var unavailable = RejectUnavailable(new Options(targets, top, OutputFormat.Text, file, summary && !deepGiven && !exact, timeout, parallel, deep, exact, deepGiven, warn, crit, html, output, largest, largestCount));
                if (unavailable != null) return new(Error: unavailable);
            }
            else if (TargetParser.Parse(arg) != null) targets.Add(arg);
            else return new(Error: $"Invalid target: {arg}");
        }
        if (file != null)
        {
            if (!TryReadTargetFile(file, out var fileTargets, out var fileError)) return new(Error: fileError);
            foreach (var ft in fileTargets)
            {
                if (TargetParser.Parse(ft) == null) return new(Error: $"Invalid target: {ft}");
                targets.Add(ft);
            }
        }
        if (warn >= crit) return new(Error: "/warn must be below /crit");
        if (!largestGiven && args.Any(a => IsFlag(a, "largestcount"))) return new(Error: "/largestcount requires /largest");
        if (targets.Count == 0 || (json && csv)) return new(Error: ShortUsageText);
        bool effectiveSummary = summary && !deepGiven && !exact;
        return new(Options: new Options(targets, top, json ? OutputFormat.Json : csv ? OutputFormat.Csv : OutputFormat.Text, file, effectiveSummary, timeout, parallel, deep, exact, deepGiven, warn, crit, html, output ?? (html ? DefaultHtmlPath : null), largest, largestCount));
    }

    public static string? RejectUnavailable(Options options) => null;

    private static bool TryParseTimeout(string s, out TimeSpan timeout)
    {
        timeout = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        char last = s[^1];
        double multiplier = 1;
        string number = s;
        if (char.IsLetter(last))
        {
            number = s[..^1];
            switch (char.ToLowerInvariant(last))
            {
                case 's': multiplier = 1; break;
                case 'm': multiplier = 60; break;
                case 'h': multiplier = 3600; break;
                default: return false;
            }
        }
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0 || double.IsNaN(n) || double.IsInfinity(n))
            return false;
        double seconds = n * multiplier;
        if (seconds > TimeSpan.MaxValue.TotalSeconds) return false;
        timeout = TimeSpan.FromSeconds(seconds);
        return timeout > TimeSpan.Zero;
    }

    private static bool TryReadTargetFile(string path, out List<string> targets, out string? error)
    {
        targets = new List<string>();
        error = null;
        try
        {
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                targets.Add(line);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            error = $"Cannot read target file '{path}': {ex.Message}";
            return false;
        }
    }
}
