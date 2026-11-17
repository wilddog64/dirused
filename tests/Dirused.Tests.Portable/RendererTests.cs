using System.Text.Json;
using Dirused.Common.Renderers;
using Dirused.Core;
using Xunit;
namespace Dirused.Tests.Portable;
public class RendererTests
{
    private static readonly ScanResult Result = new(".", new Volume("C:\\", 1024, 1024, 2048, 50), new() { new Entry("dir", 1, 1024) }, new() { new Entry("a\"b", 1, 1024) }, new(), true);
    [Fact] public void TextMatchesM1a()
    {
        using var w = new StringWriter(); new TextRenderer().Render(w, new[] { Result });
        Assert.Equal(string.Join(Environment.NewLine, new[] { "C:\\ used 1.0 KB, free 1.0 KB, total 2.0 KB, 50.0% used", "Largest directories", "dir  logical 1.0 B  on disk 1.0 KB", "Largest files", "a\"b  logical 1.0 B  on disk 1.0 KB", "" }), w.ToString());
    }
    [Fact] public void CsvMatchesM1a()
    {
        using var w = new StringWriter(); new CsvRenderer().Render(w, new[] { Result });
        Assert.Equal(string.Join(Environment.NewLine, new[] { "kind,target,path,logical_bytes,on_disk_bytes", "directory,\".\",\"dir\",1,1024", "file,\".\",\"a\"\"b\",1,1024", "" }), w.ToString());
    }
    [Fact] public void JsonMatchesM1a()
    {
        using var w = new StringWriter(); new JsonRenderer().Render(w, new[] { Result });
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
      "complete": true
    }
  ]
}
""";
        Assert.Equal(expected.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine, w.ToString());
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
