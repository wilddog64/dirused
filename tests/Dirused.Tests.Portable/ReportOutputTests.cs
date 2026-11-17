using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class ReportOutputTests
{
    [Fact]
    public void H12_FileAsParentIsUnwritable()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string parent = Path.Combine(root, "afile");
        File.WriteAllText(parent, "x");
        try
        {
            bool ok = ReportOutput.TryOpen(Path.Combine(parent, "r.html"), out var writer, out _, out var error);
            Assert.False(ok);
            Assert.Null(writer);
            Assert.StartsWith("Cannot write report", error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void H13_CreatesParentFolderAndFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-output-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "new", "sub", "r.html");
        try
        {
            Assert.True(ReportOutput.TryOpen(path, out var writer, out _, out var error), error);
            writer!.Dispose();
            Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
