using System.Text.Json;
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
      "exact": false
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
        Assert.Equal(2, textLines.Length);

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
        Assert.Single(localLines);

        var remoteUpper = new ScanResult(@"\\A\d$", new Volume(@"\\A\d$\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x8888 }, new(), new(), new(), true);
        var remoteLower = new ScanResult(@"\\a\d$", new Volume(@"\\a\d$\", 1000, 1000, 2000, 50.0) { SerialNumber = 0x8888 }, new(), new(), new(), true);
        using var textRemote = new StringWriter();
        new SummaryRenderer().Render(textRemote, new[] { remoteUpper, remoteLower });
        var remoteLines = textRemote.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Single(remoteLines);
    }
}
