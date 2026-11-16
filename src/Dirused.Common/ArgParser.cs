using System.Globalization;
using System.Security;
using System.Text;
using Dirused.Common.Targets;
namespace Dirused.Common;
public static class ArgParser
{
    public const string ShortUsageText =
        "Usage: dirused <target>... [/file <path>] [/summary] [/top N] [/json|/csv]\n" +
        "Run dirused /help for more.";

    public const string UsageText =
        "Usage: dirused [target...] [switches]\n" +
        "\n" +
        "Targets:\n" +
        "  . | <rel>           Current or relative path\n" +
        "  C:\\x | c:/x | C:    Local path or drive root\n" +
        "  \\\\server\\share      UNC share (or //server/share)\n" +
        "  \\\\node | //node     All admin shares on a host\n" +
        "  /all                All drives: local when no targets, else every drive on each target host\n" +
        "\n" +
        "Switches (use /, - or -- in any case):\n" +
        "  /top <n>            Show top N entries (default: 50)\n" +
        "  /json               Output JSON (default: text)\n" +
        "  /csv                Output CSV (default: text)\n" +
        "  /all                All drives: local when no targets, else every drive on each target host\n" +
        "  /file <path>        Read targets from file, one per line (default: none)\n" +
        "  /summary            Volume usage only, no directory walk (default: off)\n" +
        "  /timeout <n>[s|m|h] Per-target timeout, > 0 (default: none)\n" +
        "  /parallel <n>       Max concurrent scans, 1-64 (default: 4)\n" +
        "  /help               Show this help\n" +
        "\n" +
        "Exit codes:\n" +
        "  0  All targets completed\n" +
        "  1  Usage error or unreadable target file\n" +
        "  3  Any target incomplete, failed, or timed out";

    public static ParseResult Parse(string[] args)
    {
        var targets = new List<string>(); int top = 50; bool json = false, csv = false;
        string? file = null; bool summary = false; TimeSpan? timeout = null; int? parallel = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i], name = arg.TrimStart('/', '-').ToLowerInvariant();
            if (arg.Equals("help", StringComparison.OrdinalIgnoreCase) || arg == "-h" || ((arg.StartsWith('/') && !arg.StartsWith("//", StringComparison.Ordinal) || arg.StartsWith('-')) && (name == "help" || name == "?"))) return new(Usage: true);
            if ((arg.StartsWith('/') && !arg.StartsWith("//", StringComparison.Ordinal)) || arg.StartsWith('-'))
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
                    case "file":
                        if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i])) return new(Error: "/file requires a path");
                        file = args[i]; break;
                    case "summary": summary = true; break;
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
                var unavailable = RejectUnavailable(new Options(targets, top, OutputFormat.Text, file, summary, timeout, parallel));
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
        if (targets.Count == 0 || (json && csv)) return new(Error: ShortUsageText);
        return new(Options: new Options(targets, top, json ? OutputFormat.Json : csv ? OutputFormat.Csv : OutputFormat.Text, file, summary, timeout, parallel));
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
