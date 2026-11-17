using System.Text.Json;
using Dirused.Cli;
using Xunit;

namespace Dirused.Tests;

public sealed class ProgramHtmlTests
{
    [Fact]
    public void H10_HtmlKeepsConsoleOutputUnchanged()
    {
        WithFixture((dir, tmp) =>
        {
            var plain = Run(new[] { dir, "/summary" });
            var html = Run(new[] { dir, "/summary", "/html", "/output", Path.Combine(tmp, "r.html") });
            Assert.Equal(plain.Stdout, html.Stdout);
            Assert.Equal(plain.ExitCode, html.ExitCode);
            Assert.Contains("<table class=\"volumes\">", File.ReadAllText(Path.Combine(tmp, "r.html")));
            Assert.Contains("HTML report: ", html.Stderr);

            var csv = Run(new[] { dir, "/summary", "/csv" });
            var csvHtml = Run(new[] { dir, "/summary", "/csv", "/html", "/output", Path.Combine(tmp, "csv.html") });
            Assert.Equal(csv.Stdout, csvHtml.Stdout);
            Assert.Equal(csv.ExitCode, csvHtml.ExitCode);
        });
    }

    [Fact]
    public void H11_OutputWritesJsonOnlyToFile()
    {
        WithFixture((dir, tmp) =>
        {
            string path = Path.Combine(tmp, "x.json");
            var result = Run(new[] { dir, "/json", "/output", path });
            Assert.Equal("", result.Stdout);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Contains("Report: ", result.Stderr);
        });
    }

    [Fact]
    public void H14_UnwritableOutputFailsBeforeScan()
    {
        WithFixture((dir, tmp) =>
        {
            string parent = Path.Combine(tmp, "afile");
            File.WriteAllText(parent, "file");
            var result = Run(new[] { dir, "/html", "/output", Path.Combine(parent, "r.html") });
            Assert.Equal(1, result.ExitCode);
            Assert.StartsWith("Cannot write report", result.Stderr);
            Assert.Equal("", result.Stdout);
        });
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = Program.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static void WithFixture(Action<string, string> action)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "dirused-html-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(tmp, "target");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "one.txt"), "one");
        File.WriteAllText(Path.Combine(dir, "two.txt"), "two");
        try { action(dir, tmp); }
        finally { Directory.Delete(tmp, true); }
    }
}
