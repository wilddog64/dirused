using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class LargestTests
{
    [Fact]
    public void DefaultsAndPatternOverrides()
    {
        foreach (var name in new[] { "server.log", "app.log.3", "catalina.out", "java_pid4412.hprof", "javacore.20261102.txt", "db.bak", "~$report.docx", "setup.exe.crdownload", "MEMORY.DMP" }) Assert.True(Largest.Matches(name, Largest.DefaultPatterns), name);
        foreach (var name in new[] { "core.dll", "logo.png", "backup.zip", "notes.txt", "catalog.json" }) Assert.False(Largest.Matches(name, Largest.DefaultPatterns), name);
        Assert.True(Largest.ParsePatterns("*.hprof;*.dmp", out var replace, out _)); Assert.Equal(2, replace.Count);
        Assert.True(Largest.ParsePatterns("+*.trc2,+*.out2", out var add, out _)); Assert.Contains("*.trc2", add);
        Assert.True(Largest.ParsePatterns("*", out var any, out _)); Assert.True(Largest.Matches("anything.xyz", any));
        Assert.True(Largest.ParsePatterns("catalina.out;", out var exact, out _)); Assert.Equal(new[] { "catalina.out" }, exact);
        Assert.False(Largest.ParsePatterns("+", out _, out var emptyError)); Assert.Contains("file names only", emptyError);
        Assert.True(Largest.ParsePatterns("+*.log;+", out var plus, out _)); Assert.Contains("*.log", plus);
        Assert.False(Largest.ParsePatterns("+*.a;*.b", out _, out var mix)); Assert.Contains("use + items", mix);
        Assert.False(Largest.ParsePatterns(@"logs\*.log", out _, out _)); Assert.False(Largest.ParsePatterns(";;", out _, out _));
    }

    [Fact]
    public void CollectorIsBoundedCountsAllAndBreaksTiesByPath()
    {
        var c = new LargestCollector(1, new[] { "*.log" });
        c.Offer(@"C:\z.log", "z.log", 10, null); c.Offer(@"C:\a.log", "a.log", 10, null);
        Assert.Equal(@"C:\a.log", c.Snapshot(true).Files.Single().Path);
        var reverse = new LargestCollector(1, new[] { "*.log" });
        reverse.Offer(@"C:\a.log", "a.log", 10, null); reverse.Offer(@"C:\z.log", "z.log", 10, null);
        Assert.Equal(@"C:\a.log", reverse.Snapshot(true).Files.Single().Path);
        var random = new Random(42); var input = Enumerable.Range(0, 1000).Select(i => new LargestFile($"{i}.{(i % 3 == 0 ? "log" : "txt")}", random.Next(1, 10000), null)).ToArray();
        var top = new LargestCollector(20, new[] { "*.log" });
        foreach (var f in input) top.Offer(f.Path, Path.GetFileName(f.Path), f.LogicalBytes, null);
        var result = top.Snapshot(true);
        var matching = input.Where(x => x.Path.EndsWith(".log", StringComparison.Ordinal)).ToArray();
        Assert.Equal(334, result.Matches); Assert.Equal(matching.Sum(x => x.LogicalBytes), result.MatchedBytes);
        Assert.Equal(matching.OrderByDescending(x => x.LogicalBytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(20).Select(x => x.Path), result.Files.Select(x => x.Path));
    }

    [Fact]
    public void ParserAndRenderersIncludeLargestOnlyWhenRequested()
    {
        Assert.Equal(Largest.DefaultPatterns, ArgParser.Parse(new[] { "/largest", @"C:\" }).Options!.Largest);
        var custom = ArgParser.Parse(new[] { @"C:\", "/largest", "*.bak" }).Options!;
        Assert.Equal(new[] { "*.bak" }, custom.Largest); Assert.Equal(new[] { @"C:\" }, custom.Targets);
        var exact = ArgParser.Parse(new[] { "/largest", "catalina.out;", "/json", @"C:\" }).Options!;
        Assert.Equal(new[] { "catalina.out" }, exact.Largest); Assert.Equal(OutputFormat.Json, exact.Format);
        Assert.Equal("/largestcount requires /largest", ArgParser.Parse(new[] { "/largestcount", "5", @"C:\" }).Error);
        Assert.Equal("/largestcount requires an integer from 1 to 1000", ArgParser.Parse(new[] { @"C:\", "/largest", "/largestcount", "0" }).Error);
        Assert.Equal("/largestcount requires an integer from 1 to 1000", ArgParser.Parse(new[] { @"C:\", "/largest", "/largestcount", "1001" }).Error);
        var r = new ScanResult(@"C:\", new Volume("C:", 0, 0, 0, 0), new(), new(), new(), true)
        { Largest = new LargestFiles(new[] { "*.log" }, 1284, 1024 * 1024, new() { new(@"C:\a&b.log", 1024 * 1024, null), new(@"C:\b.log", 0, DateTime.UtcNow) }, false) };
        using var text = new StringWriter(); new TextRenderer().Render(text, new[] { r });
        Assert.Contains("Largest candidate files (2 of 1,284 matches,", text.ToString()); Assert.Contains("(scan incomplete)", text.ToString()); Assert.Contains("  1.0M  -  C:\\a&b.log", text.ToString());
        using var json = new StringWriter(); new JsonRenderer().Render(json, new[] { r }); Assert.Contains("matchedBytes", json.ToString()); Assert.Contains("lastWriteUtc", json.ToString());
        using var csv = new StringWriter(); new CsvRenderer().Render(csv, new[] { r }); Assert.Contains("largest,", csv.ToString());
        using var html = new StringWriter(); new HtmlRenderer(false, 85, 90, 50).Render(html, new[] { r }); Assert.Contains("<h2>Largest candidate files</h2>", html.ToString()); Assert.Contains("a&amp;b.log", html.ToString());
        using var summaryText = new StringWriter(); new SummaryRenderer().Render(summaryText, new[] { r }); Assert.Contains(Format.Size(1024 * 1024), summaryText.ToString());
        using var none = new StringWriter(); new TextRenderer().Render(none, new[] { r with { Largest = new LargestFiles(new[] { "*.log" }, 0, 0, new(), true) } }); Assert.Contains("Largest candidate files: none matched", none.ToString());
        var off = r with { Largest = null }; using var offJson = new StringWriter(); new JsonRenderer().Render(offJson, new[] { off }); Assert.DoesNotContain("largest", offJson.ToString());
        var summary = new[] { r with { Level = FullnessLevel.Warn }, off with { Target = @"D:\", Volume = new Volume(@"D:", 0, 0, 0, 0), Level = FullnessLevel.Ok } };
        using var summaryHtml = new StringWriter(); new HtmlRenderer(true, 85, 90, 50).Render(summaryHtml, summary);
        Assert.Equal(1, summaryHtml.ToString().Split("<tr class=\"group\">").Length - 1);
        Assert.Contains(@"C:\", summaryHtml.ToString()); Assert.DoesNotContain(@"D:\", summaryHtml.ToString().Split("<h2>Largest candidate files</h2>")[1]);
    }
}
