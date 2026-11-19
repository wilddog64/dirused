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
        "            [/largest [<patterns>] [/purge [days] [/minsize <size>] [/confirm]]]\n" +
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
        "  /purge [days]       Delete the files /largest lists that were last\n" +
        "                      modified more than [days] days ago (default: 1)\n" +
        "                      and are at least /minsize big (default: 1GB).\n" +
        "                      Patterns are needed only with /confirm; without\n" +
        "                      them the dry run lists what /largest finds.\n" +
        "                      Without /confirm this is a dry run: nothing is\n" +
        "                      deleted, the report shows what would be.\n" +
        "  /minsize <size>     With /purge: smallest file to delete, e.g. 100MB,\n" +
        "                      999MB, 2GB (default: 1GB). Text and HTML output\n" +
        "                      do not list files smaller than /minsize; lower\n" +
        "                      /minsize to see them; JSON and CSV list every file.\n" +
        "  /confirm            With /purge: really delete. Deletes are permanent\n" +
        "                      (admin shares like \\\\host\\d$ skip the Recycle Bin).\n" +
        "                      Each deletion is logged to C:\\temp\\dirused-purge.log\n" +
        "  /timeout <n>[s|m|h] Per-target timeout, > 0 (default: none)\n" +
        "  /parallel <n>       Max concurrent scans, 1-64 (default: 4)\n" +
        "  /help               Show this help\n" +
        "  /version            Show version and architecture\n" +
        "  /update             Download and install the latest release\n" +
        "  /noupdatecheck      Do not check for updates during this run\n" +
        "\n" +
        "Purge examples:\n" +
        "  dirused D:\\ /largest *.log /purge\n" +
        "      Dry run: list what would be deleted, delete nothing\n" +
        "  dirused D:\\ /largest *.log /purge /confirm\n" +
        "      Delete the listed .log files of 1GB or more not modified in the last day\n" +
        "  dirused D:\\ /largest *.log /purge /minsize 100MB\n" +
        "      Dry run: also include listed .log files from 100MB\n" +
        "  dirused /file servers.txt /largest *.log;*.hprof /purge 7 /confirm\n" +
        "      On every server, delete listed .log and .hprof files older than 7 days\n" +
        "Purge only deletes files in the /largest list. It skips files\n" +
        "smaller than /minsize, files modified within [days], files changed since\n" +
        "the scan, links, hard-linked files and anything outside the target.\n" +
        "A file in use is reported as failed.\n" +
        "\n" +
        "Exit codes:\n" +
        "  0  All targets completed\n" +
        "  1  Usage error or unreadable target file\n" +
        "  3  Any target incomplete, failed, timed out, or a /purge did not finish\n" +
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
        IReadOnlyList<string>? largest = null; bool largestGiven = false, largestExplicit = false; int largestCount = 20;
        bool purge = false, confirm = false, minSizeGiven = false, largestHasAdd = false; int purgeDays = 1; long purgeMinBytes = 1L << 30;
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
                    case "purge":
                        purge = true;
                        if (i + 1 < args.Length && args[i + 1].Length > 0 && args[i + 1].All(char.IsAsciiDigit))
                        {
                            if (!int.TryParse(args[++i], out purgeDays) || purgeDays < 1 || purgeDays > 3650) return new(Error: "/purge requires a number of days from 1 to 3650");
                        }
                        break;
                    case "confirm": confirm = true; break;
                    case "minsize":
                        minSizeGiven = true;
                        if (i + 1 >= args.Length || !TryParseMinSize(args[++i], out purgeMinBytes)) return new(Error: "/minsize needs a size like 100MB or 2GB");
                        break;
                    case "plan": return new(Error: "not available yet (M2)");
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
                        largestExplicit = false;
                        if (i + 1 < args.Length)
                        {
                            string next = args[i + 1];
                            bool isSwitch = (next.StartsWith('/') && !next.StartsWith("//", StringComparison.Ordinal)) || next.StartsWith('-');
                            if (!isSwitch && (next.IndexOfAny(new[] { '*', '?', ';', ',' }) >= 0 || next.StartsWith('+'))) { patternArg = next; largestExplicit = true; i++; }
                        }
                        largestHasAdd = patternArg?.Split(new[] { ';', ',' }, StringSplitOptions.None).Any(p => p.TrimStart().StartsWith('+')) == true;
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
        if (minSizeGiven && !purge) return new(Error: "/minsize requires /purge");
        if (purge && !largestGiven) return new(Error: "/purge requires /largest");
        if (confirm && !purge) return new(Error: "/confirm requires /purge");
        bool purgeNeedsPatterns = purge && !confirm && (!largestExplicit || largest!.Any(p => !p.Any(c => c is not ('*' or '?' or '.'))) || largestHasAdd);
        if (purge && confirm)
        {
            if (!largestExplicit) return new(Error: "/purge /confirm requires /largest with file patterns, e.g. /largest *.log");
            if (largest!.Any(p => !p.Any(c => c is not ('*' or '?' or '.')))) return new(Error: "/purge /confirm cannot be used with a match-all pattern; name the file types to delete, e.g. /largest *.log");
            if (largestHasAdd) return new(Error: "/purge /confirm needs the full pattern list; '+' adds to the default list, e.g. write /largest *.log;*.zip");
        }
        if (purge && targets.Any(t => t.Length > 0 && t.All(char.IsAsciiDigit))) return new(Error: "put the number of days right after /purge (e.g. /purge 7)");
        bool effectiveSummary = summary && !deepGiven && !exact;
        if (purge && effectiveSummary && csv && !html) return new(Error: "/purge with /summary /csv needs /html, which lists the files");
        if (purge && confirm && output != null && Path.GetFullPath(output).Equals(Path.GetFullPath(PurgeLog.DefaultPath), StringComparison.OrdinalIgnoreCase)) return new(Error: "/output cannot be the purge log");
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
        return new(Options: new Options(targets, top, json ? OutputFormat.Json : csv ? OutputFormat.Csv : OutputFormat.Text, file, effectiveSummary, timeout, parallel, deep, exact, deepGiven, warn, crit, html, output ?? (html ? DefaultHtmlPath : null), largest, largestCount, purge, purgeDays, confirm, purgeMinBytes, purgeNeedsPatterns));
    }

    public static string? RejectUnavailable(Options options) => null;

    private static bool TryParseMinSize(string value, out long bytes)
    {
        bytes = 0;
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^([0-9]+)(KB|MB|GB|TB)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number == 0) return false;
        long multiplier = match.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1L << 10, "MB" => 1L << 20, "GB" => 1L << 30, "TB" => 1L << 40, _ => 0 };
        try { bytes = checked(number * multiplier); return true; }
        catch (OverflowException) { return false; }
    }

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
