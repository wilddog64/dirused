using System.Text.Json;
using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using Xunit;
namespace Dirused.Tests.Portable;
public class RendererTests
{
    private static readonly ScanResult Result = new(".", new Volume("C:\\", 1024, 1024, 2048, 50), new() { new Entry("dir", 1, 1024) }, new() { new Entry("a\"b", 1, 1024) }, new(), true);

    [Fact]
    public void TextMatchesM1d()
    {
        using var w = new StringWriter();
        new TextRenderer().Render(w, new[] { Result });
        Assert.Equal(string.Join(Environment.NewLine, new[]
        {
            "C:\\  used 1.0K, free 1.0K, total 2.0K, 50.0% used",
            "a\"b  1B",
            "dir  1B",
            ""
        }), w.ToString());
    }

    [Fact]
    public void Test_TextShowsSystemOnlyLine()
    {
        const string path = @"C:\System Volume Information";
        var result = Result with { SystemOnly = new() { path } };
        using var w = new StringWriter();
        new TextRenderer().Render(w, new[] { result });
        Assert.Contains(path + ": system only (not counted)", w.ToString());
        Assert.DoesNotContain("INCOMPLETE", w.ToString());
    }

    [Fact]
    public void CsvMatchesM1a()
    {
        using var w = new StringWriter();
        new CsvRenderer().Render(w, new[] { Result });
        Assert.Equal(string.Join(Environment.NewLine, new[] { "kind,target,path,logical_bytes,on_disk_bytes", "directory,\".\",\"dir\",1,1024", "file,\".\",\"a\"\"b\",1,1024", "" }), w.ToString());
    }

    [Fact]
    public void JsonMatchesM1d()
    {
        using var w = new StringWriter();
        new JsonRenderer().Render(w, new[] { Result });
        const string expected = """
{
  "targets": [
    {
      "target": ".",
      "volume": {
        "drive": "C:\\",
        "usedBytes": 1024,
        "freeBytes": 1024,
        "totalBytes": 2048,
        "percentUsed": 50
      },
      "directories": [
        {
          "path": "dir",
          "logicalBytes": 1,
          "onDiskBytes": 1024
        }
      ],
      "files": [
        {
          "path": "a\u0022b",
          "logicalBytes": 1,
          "onDiskBytes": 1024
        }
      ],
      "errors": [],
      "complete": true,
      "exact": false,
      "systemOnly": [],
      "level": null
    }
  ]
}
""";
        Assert.Equal(expected.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine, w.ToString());
    }

    [Theory]
    [InlineData(0L, "0B")]
    [InlineData(1023L, "1023B")]
    [InlineData(1024L, "1.0K")]
    [InlineData(27852L, "27K")]
    [InlineData(1288490188L, "1.2G")]
    [InlineData(227633266688L, "212G")]
    public void Test6_FormatShortExamples(long bytes, string expected)
    {
        Assert.Equal(expected, Format.Short(bytes));
    }

    [Fact]
    public void TextRowsSortByDisplayedSize()
    {
        var vol = new Volume("C:\\", 1024, 1024, 2048, 50);
        var entries = new List<Entry>
        {
            new("a-small", 100, 4096),
            new("b-larger", 4000, 4096),
            new("sparse", 64L * 1024 * 1024, 4096)
        };
        using var w = new StringWriter();
        new TextRenderer(exact: true).Render(w, new[] { new ScanResult("T", vol, new(), entries, new(), true, true) });
        var lines = w.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.StartsWith("sparse", lines[1]);
        Assert.StartsWith("b-larger", lines[2]);
        Assert.StartsWith("a-small", lines[3]);
    }

    [Fact]
    public void Test7_TextRowPaddingAndConsoleWrapping()
    {
        var vol = new Volume("C:\\", 1024, 1024, 2048, 50);
        var entries = new List<Entry>
        {
            new("longer/path", 27852, 28672),
            new("short", 1024 * 1024, 1024 * 1024)
        };
        var target1 = new ScanResult("T1", vol, new(), entries, new(), true);

        // 1. Sizes right-aligned in one column:
        // longest path: "longer/path" (11 chars) + 2 = 13.
        // sizes: " 27K" (len 4) and "1.0M" (len 4).
        using var w1 = new StringWriter();
        new TextRenderer().Render(w1, new[] { target1 });
        var lines = w1.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal("short        1.0M", lines[1]);
        Assert.Equal("longer/path   27K", lines[2]);

        // 2. Redirected output pads:
        using var wRedirected = new StringWriter();
        new TextRenderer(consoleWidth: null).Render(wRedirected, new[] { target1 });
        var linesRedirected = wRedirected.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal("short        1.0M", linesRedirected[1]);
        Assert.Equal("longer/path   27K", linesRedirected[2]);

        // 3. Over-wide console row uses two spaces instead of padding:
        // Padded row for "longer/path" is 17 chars. Console width 15 makes it over-wide.
        using var wConsole = new StringWriter();
        new TextRenderer(consoleWidth: 15).Render(wConsole, new[] { target1 });
        var linesConsole = wConsole.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal("longer/path  27K", linesConsole[2]);

        // 4. Padding is per-target, not across all targets:
        var target2 = new ScanResult("T2", vol, new(), new() { new("very_long_path_here", 1024, 1024) }, new(), true);
        using var wMulti = new StringWriter();
        new TextRenderer().Render(wMulti, new[] { target1, target2 });
        var multiLines = wMulti.ToString().TrimEnd().Split(Environment.NewLine);
        // target1 lines are index 1 and 2, target2 lines are index 4.
        // target1 path "short" should be padded to 13 (target1's max path 11 + 2), NOT target2's max path (19 + 2 = 21).
        Assert.Equal("short        1.0M", multiLines[1]);
    }

    [Fact]
    public void Test10_ExactRowCompressedFixtureVsNormal()
    {
        var vol = new Volume("C:\\", 1024, 1024, 2048, 50) { ClusterSize = 4096 };
        var compressed = new Entry("compressed.bin", 64 * 1024 * 1024, 4096);
        var normal = new Entry("normal.bin", 27852, 28672);
        var scanExact = new ScanResult(".", vol, new(), new() { compressed, normal }, new(), true, Exact: true);

        using var wExact = new StringWriter();
        new TextRenderer().Render(wExact, new[] { scanExact });
        var output = wExact.ToString();
        Assert.Contains("compressed.bin  64M (4.0K on disk)", output);
        Assert.Contains("normal.bin      27K", output);
        Assert.DoesNotContain("normal.bin      27K (", output);

        // Control: without exact, compressed fixture shows nothing extra
        var scanNonExact = new ScanResult(".", vol, new(), new() { compressed, normal }, new(), true, Exact: false);
        using var wNonExact = new StringWriter();
        new TextRenderer().Render(wNonExact, new[] { scanNonExact });
        Assert.DoesNotContain("on disk", wNonExact.ToString());
    }

    [Fact]
    public void E1_SummaryKeepsHostsSharingVolumeSerialNumberInTextJsonAndCsv()
    {
        var rA = new ScanResult(@"\\a\d$", new Volume(@"\\a\d$\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x12345678 }, new(), new(), new(), true);
        var rB = new ScanResult(@"\\b\d$", new Volume(@"\\b\d$\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x12345678 }, new(), new(), new(), true);
        var results = new[] { rA, rB };

        using var textOut = new StringWriter();
        new SummaryRenderer().Render(textOut, results);
        var textLines = textOut.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(3, textLines.Length);
        Assert.Equal("2 volume(s): 0 at or above 90%, 0 at or above 85%", textLines[^1]);

        using var jsonOut = new StringWriter();
        new JsonRenderer(summary: true).Render(jsonOut, results);
        using var doc = JsonDocument.Parse(jsonOut.ToString());
        var targets = doc.RootElement.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());

        using var csvOut = new StringWriter();
        new CsvRenderer(summary: true).Render(csvOut, results);
        var csvLines = csvOut.ToString().TrimEnd().Split(Environment.NewLine);
        var volumeRows = csvLines.Where(l => l.StartsWith("volume,")).ToList();
        Assert.Equal(2, volumeRows.Count);
    }

    [Fact]
    public void E2_SummaryDeduplicatesLocalDriveLettersOnSameVolumeAndCaseInsensitiveRemoteHost()
    {
        var local1 = new ScanResult(@"C:\", new Volume(@"C:\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x9999 }, new(), new(), new(), true);
        var local2 = new ScanResult(@"C:\mnt", new Volume(@"C:\mnt\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x9999 }, new(), new(), new(), true);
        using var textLocal = new StringWriter();
        new SummaryRenderer().Render(textLocal, new[] { local1, local2 });
        var localLines = textLocal.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(2, localLines.Length);
        Assert.Equal("1 volume(s): 0 at or above 90%, 0 at or above 85%", localLines[^1]);

        var remoteUpper = new ScanResult(@"\\A\d$", new Volume(@"\\A\d$\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x8888 }, new(), new(), new(), true);
        var remoteLower = new ScanResult(@"\\a\d$", new Volume(@"\\a\d$\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x8888 }, new(), new(), new(), true);
        using var textRemote = new StringWriter();
        new SummaryRenderer().Render(textRemote, new[] { remoteUpper, remoteLower });
        var remoteLines = textRemote.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(2, remoteLines.Length);
        Assert.Equal("1 volume(s): 0 at or above 90%, 0 at or above 85%", remoteLines[^1]);
    }

    [Fact]
    public void PurgeHidesFilesBelowMinSizeInTextAndHtml()
    {
        const long gb = 1024L * 1024 * 1024;
        const long mb = 1024L * 1024;
        const long kb = 1024L;
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var f1 = new LargestFile(@"C:\t\del.log", 2L * gb, date) { PurgeStatus = "would delete" };
        var f2 = new LargestFile(@"C:\t\recent.log", 3L * gb, date) { PurgeStatus = "skipped: modified within 1 day(s)" };
        var f3 = new LargestFile(@"C:\t\small1.log", 5L * mb, date) { PurgeStatus = "skipped: smaller than 1.0 GB" };
        var f4 = new LargestFile(@"C:\t\small2.log", 1L * kb, date) { PurgeStatus = "skipped: smaller than 1.0 GB" };
        var files = new List<LargestFile> { f1, f2, f3, f4 };
        long totalBytes = files.Sum(f => f.LogicalBytes);

        var rMixed = new ScanResult(@"C:\t", new Volume(@"C:\", 0, 0, 0, 0), new(), new(), new(), true)
        {
            Largest = new LargestFiles(new[] { "*.log" }, files.Count, totalBytes, files, true)
            {
                PurgeBytes = 2L * gb,
                PurgeDryRun = true
            }
        };

        // Text rendering
        using var textOut = new StringWriter();
        new TextRenderer().Render(textOut, new[] { rMixed });
        var text = textOut.ToString();
        Assert.Contains("Largest candidate files (2 of 4 matches, 5.0G, 2 smaller than 1.0 GB not shown, would free 2.0G):", text);
        Assert.Contains(@"C:\t\del.log", text);
        Assert.Contains(@"C:\t\recent.log", text);
        Assert.DoesNotContain(@"C:\t\small1.log", text);
        Assert.DoesNotContain(@"C:\t\small2.log", text);
        Assert.DoesNotContain("skipped: smaller than", text);

        // HTML rendering
        using var htmlOut = new StringWriter();
        new HtmlRenderer(false, 85, 90, 50).Render(htmlOut, new[] { rMixed });
        var html = htmlOut.ToString();
        Assert.Contains("2 of 4 matches, 5.0 GB, 2 smaller than 1.0 GB not shown, would free 2.0 GB", html);
        Assert.Contains(@"C:\t\del.log", html);
        Assert.Contains(@"C:\t\recent.log", html);
        Assert.DoesNotContain(@"C:\t\small1.log", html);
        Assert.DoesNotContain(@"C:\t\small2.log", html);
        Assert.DoesNotContain("smaller than 1.0 GB</td>", html);

        // All rows skipped: smaller than 1.0 GB
        var allHiddenFiles = new List<LargestFile> { f3, f4 };
        var rAllHidden = new ScanResult(@"C:\t", new Volume(@"C:\", 0, 0, 0, 0), new(), new(), new(), true)
        {
            Largest = new LargestFiles(new[] { "*.log" }, allHiddenFiles.Count, allHiddenFiles.Sum(f => f.LogicalBytes), allHiddenFiles, true)
            {
                PurgeBytes = 0L,
                PurgeDryRun = true
            }
        };

        using var allHiddenTextOut = new StringWriter();
        new TextRenderer().Render(allHiddenTextOut, new[] { rAllHidden });
        var allHiddenText = allHiddenTextOut.ToString();
        Assert.Contains("Largest candidate files (0 of 2 matches, 5.0M, 2 smaller than 1.0 GB not shown, would free 0B):", allHiddenText);
        Assert.Contains("  no files of at least 1.0 GB", allHiddenText);
        Assert.DoesNotContain(@"C:\t\small1.log", allHiddenText);

        using var allHiddenHtmlOut = new StringWriter();
        new HtmlRenderer(false, 85, 90, 50).Render(allHiddenHtmlOut, new[] { rAllHidden });
        var allHiddenHtml = allHiddenHtmlOut.ToString();
        Assert.Contains("0 of 2 matches, 5.0 MB, 2 smaller than 1.0 GB not shown, would free 0.0 B", allHiddenHtml);
        Assert.Contains("<tr><td colspan=\"5\">no files of at least 1.0 GB</td></tr>", allHiddenHtml);
        Assert.DoesNotContain(@"C:\t\small1.log", allHiddenHtml);

        // Positive control: same files without /purge (PurgeStatus = null)
        var noPurgeFiles = new List<LargestFile>
        {
            new(@"C:\t\del.log", 2L * gb, date),
            new(@"C:\t\recent.log", 3L * gb, date),
            new(@"C:\t\small1.log", 5L * mb, date),
            new(@"C:\t\small2.log", 1L * kb, date)
        };
        var rNoPurge = new ScanResult(@"C:\t", new Volume(@"C:\", 0, 0, 0, 0), new(), new(), new(), true)
        {
            Largest = new LargestFiles(new[] { "*.log" }, noPurgeFiles.Count, totalBytes, noPurgeFiles, true)
        };

        using var noPurgeTextOut = new StringWriter();
        new TextRenderer().Render(noPurgeTextOut, new[] { rNoPurge });
        var noPurgeText = noPurgeTextOut.ToString();
        Assert.Contains("Largest candidate files (4 of 4 matches, 5.0G):", noPurgeText);
        Assert.Contains(@"C:\t\del.log", noPurgeText);
        Assert.Contains(@"C:\t\recent.log", noPurgeText);
        Assert.Contains(@"C:\t\small1.log", noPurgeText);
        Assert.Contains(@"C:\t\small2.log", noPurgeText);
        Assert.DoesNotContain("not shown", noPurgeText);

        using var noPurgeHtmlOut = new StringWriter();
        new HtmlRenderer(false, 85, 90, 50).Render(noPurgeHtmlOut, new[] { rNoPurge });
        var noPurgeHtml = noPurgeHtmlOut.ToString();
        Assert.Contains("4 of 4 matches, 5.0 GB", noPurgeHtml);
        Assert.DoesNotContain("<th>Purge</th>", noPurgeHtml);
        Assert.DoesNotContain("not shown", noPurgeHtml);
        Assert.Contains(@"C:\t\del.log", noPurgeHtml);
        Assert.Contains(@"C:\t\recent.log", noPurgeHtml);
        Assert.Contains(@"C:\t\small1.log", noPurgeHtml);
        Assert.Contains(@"C:\t\small2.log", noPurgeHtml);

        // JSON and CSV for mixed set contain all four paths
        using var jsonOut = new StringWriter();
        new JsonRenderer().Render(jsonOut, new[] { rMixed });
        var json = jsonOut.ToString();
        using var doc = JsonDocument.Parse(json);
        var jsonFiles = doc.RootElement.GetProperty("targets")[0].GetProperty("largest").GetProperty("files").EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains(@"C:\t\del.log", jsonFiles);
        Assert.Contains(@"C:\t\recent.log", jsonFiles);
        Assert.Contains(@"C:\t\small1.log", jsonFiles);
        Assert.Contains(@"C:\t\small2.log", jsonFiles);

        using var csvOut = new StringWriter();
        new CsvRenderer(purge: true).Render(csvOut, new[] { rMixed });
        var csv = csvOut.ToString();
        Assert.Contains(@"C:\t\del.log", csv);
        Assert.Contains(@"C:\t\recent.log", csv);
        Assert.Contains(@"C:\t\small1.log", csv);
        Assert.Contains(@"C:\t\small2.log", csv);
    }

    [Fact]
    public void TextRenderingOfPurgeResultWithSmallRecentFileHidesRowAndCountsInNotShown()
    {
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        const long gb = 1024L * 1024 * 1024;
        const long mb = 1024L * 1024;
        var f1 = new LargestFile(@"C:\t\del.log", 2L * gb, now.AddDays(-3));
        var f2 = new LargestFile(@"C:\t\small-recent.log", 5L * mb, now);
        var files = new List<LargestFile> { f1, f2 };
        var rawResult = new ScanResult(@"C:\t", new Volume(@"C:\", 0, 0, 0, 0), new(), new(), new(), true)
        {
            Largest = new LargestFiles(new[] { "*.log" }, files.Count, files.Sum(f => f.LogicalBytes), files, true)
        };
        var run = Purge.Apply(new[] { rawResult }, 1, 1L * gb, false, new PurgeTests.FakeFs(), () => now, null, CancellationToken.None);

        using var w = new StringWriter();
        new TextRenderer().Render(w, run.Results);
        var text = w.ToString();

        Assert.Contains("Largest candidate files (1 of 2 matches, 2.0G, 1 smaller than 1.0 GB not shown, would free 2.0G):", text);
        Assert.Contains(@"C:\t\del.log", text);
        Assert.DoesNotContain(@"C:\t\small-recent.log", text);
        Assert.DoesNotContain("skipped: smaller than", text);
    }
}
