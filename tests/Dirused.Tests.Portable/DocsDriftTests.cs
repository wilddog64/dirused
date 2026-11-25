using System.Text.RegularExpressions;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class DocsDriftTests
{
    private static readonly string[] KnownRefusals =
    {
        "refused: error during run",
        "refused: list changed since it was scheduled",
        "refused: not a dirused plan",
        "refused: not a plan id",
        "refused: not started from the plan's program copy",
        "refused: not the scheduled time",
        "refused: plan already has a report",
        "refused: plan already started",
        "refused: plan folder contains a link",
        "refused: plan folder permissions changed",
        "refused: program copy changed since it was scheduled"
    };

    private static readonly string[] KnownStates =
    {
        "`scheduled`", "`running`", "`done (", "`cancelled`", "`expired`",
        "`missed (did not run)`", "`task missing`", "`interrupted (",
        "`permissions changed — not run`", "`unreadable plan`", "`refused: "
    };

    internal static string? Section(string purgeDoc)
    {
        var lines = Normalize(purgeDoc).Split('\n');
        var start = Array.FindIndex(lines, line => line == "## Scheduled purge");
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        return string.Join('\n', lines.Skip(start).Take((end < 0 ? lines.Length : end) - start));
    }

    internal static List<string> MissingUsageLines(string usageText, string readme)
    {
        var usageLines = Normalize(usageText).Split('\n');
        var expected = new List<string>();
        for (var i = 0; i < usageLines.Length; i++)
        {
            if (!usageLines[i].StartsWith("  /schedule", StringComparison.Ordinal)) continue;
            expected.Add(usageLines[i].Trim());
            for (var j = i + 1; j < usageLines.Length; j++)
            {
                var line = usageLines[j];
                if (line.Length == 0 || !char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith('/')) break;
                expected.Add(line.Trim());
                i = j;
            }
        }
        Assert.True(expected.Count(x => x.StartsWith("/schedule", StringComparison.Ordinal)) >= 5,
            "Expected at least five /schedule usage switch lines.");
        var readmeLines = Normalize(readme).Split('\n').Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        return expected.Where(line => !readmeLines.Contains(line)).Distinct(StringComparer.Ordinal).ToList();
    }

    internal static List<string> RefusalPrefixes(IEnumerable<string> sourceTexts)
    {
        var pattern = new Regex(@"\$?""(refused: [^""{\r\n]*)", RegexOptions.CultureInvariant);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourceTexts)
        foreach (Match match in pattern.Matches(source))
        {
            var value = match.Groups[1].Value;
            var boundary = value.IndexOf(" (", StringComparison.Ordinal);
            if (boundary >= 0) value = value[..boundary];
            value = value.TrimEnd(' ', ':');
            if (value != "refused") prefixes.Add(value);
        }
        return prefixes.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    internal static List<string> MissingRefusals(IEnumerable<string> prefixes, string section)
    {
        var text = Normalize(section);
        return prefixes.Distinct(StringComparer.Ordinal).Where(prefix => !text.Contains(prefix, StringComparison.Ordinal)).ToList();
    }

    internal static List<string> MissingStates(string section)
    {
        var text = Normalize(section);
        return KnownStates.Where(state => !text.Contains(state, StringComparison.Ordinal)).ToList();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "dirused.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (string Root, string Purge, string Readme, string Parser) ReadDocsInputs()
    {
        var root = FindRepoRoot();
        var purgePath = Path.Combine(root, "docs", "purge.md");
        var readmePath = Path.Combine(root, "README.md");
        var parserPath = Path.Combine(root, "src", "Dirused.Common", "ArgParser.cs");
        Assert.True(File.Exists(purgePath), $"Missing required file: {purgePath}");
        Assert.True(File.Exists(readmePath), $"Missing required file: {readmePath}");
        Assert.True(File.Exists(parserPath), $"Missing required file: {parserPath}");
        return (root, Normalize(File.ReadAllText(purgePath)), Normalize(File.ReadAllText(readmePath)), Normalize(File.ReadAllText(parserPath)));
    }

    private static string GetSectionOrFail(string purgeDoc)
    {
        var section = Section(purgeDoc);
        Assert.NotNull(section);
        return section!;
    }

    [Fact]
    public void Readme_contains_every_schedule_usage_line()
    {
        var (_, _, readme, _) = ReadDocsInputs();
        var missing = MissingUsageLines(ArgParser.UsageText, readme);
        Assert.True(missing.Count == 0, "README is missing /schedule usage lines: " + string.Join(", ", missing));
    }

    [Fact]
    public void Purge_doc_section_names_every_refusal()
    {
        var (root, purge, _, _) = ReadDocsInputs();
        var section = GetSectionOrFail(purge);
        var sourcePaths = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)));
        var prefixes = RefusalPrefixes(sourcePaths.Select(File.ReadAllText));
        Assert.True(prefixes.Count >= 10, $"Refusal scan found only {prefixes.Count} prefixes.");
        var missing = MissingRefusals(prefixes, section);
        Assert.True(missing.Count == 0, "Scheduled purge section is missing refusal prefixes: " + string.Join(", ", missing));
    }

    [Fact]
    public void Purge_doc_section_names_every_list_state()
    {
        var (_, purge, _, _) = ReadDocsInputs();
        var missing = MissingStates(GetSectionOrFail(purge));
        Assert.True(missing.Count == 0, "Scheduled purge section is missing list states: " + string.Join(", ", missing));
    }

    [Fact]
    public void Refusal_scan_finds_the_known_prefixes()
    {
        var (root, _, _, _) = ReadDocsInputs();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText);
        var prefixes = RefusalPrefixes(sources);
        Assert.True(prefixes.Count >= 10, $"Refusal scan found only {prefixes.Count} prefixes.");
        var missing = KnownRefusals.Except(prefixes, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0, "Refusal scan missed known prefixes: " + string.Join(", ", missing));
    }

    [Fact]
    public void Synthetic_helpers_cover_contract_items_and_exact_heading()
    {
        var complete = string.Join('\n', KnownRefusals.Concat(KnownStates));
        Assert.Empty(MissingRefusals(KnownRefusals, complete));
        Assert.Empty(MissingStates(complete));
        foreach (var item in KnownRefusals)
            Assert.Equal(new[] { item }, MissingRefusals(KnownRefusals, complete.Replace(item, "", StringComparison.Ordinal)));
        foreach (var item in KnownStates)
            Assert.Equal(new[] { item }, MissingStates(complete.Replace(item, "", StringComparison.Ordinal)));
        Assert.Null(Section("# Purge\n\n## Scheduled purge (preview)\ntext"));
    }

    [Fact]
    public void Synthetic_refusal_scan_stops_at_interpolation_and_parenthesis()
    {
        var found = RefusalPrefixes(new[] { "Refuse($\"refused: x y: {id}\")", "a == \"b\" ? \"c\" : \"refused: z (w {q})\"" });
        Assert.Equal(new[] { "refused: x y", "refused: z" }, found);
    }

    [Fact]
    public void Synthetic_usage_lines_ignore_indentation_but_report_missing_lines()
    {
        const string usage = "  /schedule <time> preview\n                      [PT|PST]; schedule\n  /schedule /list list\n  /schedule /cancel <id> cancel\n  /schedule /trust trust\n  /schedule /untrust untrust";
        const string readme = "    /schedule <time> preview\n    [PT|PST]; schedule\n    /schedule /list list\n    /schedule /cancel <id> cancel\n    /schedule /trust trust\n    /schedule /untrust untrust";
        Assert.Empty(MissingUsageLines(usage, readme));
        Assert.Equal(new[] { "/schedule /list list" }, MissingUsageLines(usage, readme.Replace("    /schedule /list list\n", "", StringComparison.Ordinal)));
    }
}
