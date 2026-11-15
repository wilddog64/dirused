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
        Assert.Equal(expected.Replace("\n", Environment.NewLine) + Environment.NewLine, w.ToString());
    }
}
