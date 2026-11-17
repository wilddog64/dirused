using System.Text.Json;
using Dirused.Cli;
using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests;

public sealed class FullnessTests
{
    private static ScanResult Result(double percent, uint serial = 1, bool complete = true) =>
        new("C:\\", new Volume("C:", (long)percent, 100 - (long)percent, 100, percent) { SerialNumber = serial }, new(), new(), new(), complete);

    private static (int code, string output, string error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = Program.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Test_FullnessLevelBoundaries()
    {
        Assert.Equal(FullnessLevel.Ok, Fullness.Of(84.9, 85, 90));
        Assert.Equal(FullnessLevel.Warn, Fullness.Of(85, 85, 90));
        Assert.Equal(FullnessLevel.Warn, Fullness.Of(89.9, 85, 90));
        Assert.Equal(FullnessLevel.Crit, Fullness.Of(90, 85, 90));
    }

    [Fact]
    public void Test_SummaryTagsWarnAndCritLines()
    {
        var results = new[] { Result(50, 1), Result(86, 2), Result(95, 3) };
        using var output = new StringWriter();
        new SummaryRenderer().Render(output, results);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("C: used 50.0 B, free 50.0 B, total 100.0 B, 50.0% used", lines.Single(x => x.Contains("50.0%")));
        Assert.EndsWith(" [WARN]", lines.Single(x => x.Contains("86.0%")));
        Assert.EndsWith(" [CRIT]", lines.Single(x => x.Contains("95.0%")));
        Assert.Equal("3 volume(s): 1 at or above 90%, 1 at or above 85%", lines[^1]);
    }

    [Fact]
    public void Test_TextRendererTagsCritLine()
    {
        using var output = new StringWriter();
        new TextRenderer().Render(output, new[] { Result(95) });
        Assert.EndsWith(" [CRIT]", output.ToString().Split(Environment.NewLine)[0]);
        Assert.DoesNotContain("volume(s):", output.ToString());
    }

    [Fact]
    public void Test_RedirectedOutputHasNoEscapeCodes()
    {
        using var output = new StringWriter();
        new SummaryRenderer(color: true).Render(output, new[] { Result(86), Result(95, 2) });
        Assert.False(output.ToString().Contains('\u001b'));
        Assert.Contains(" [WARN]", output.ToString());
        Assert.Contains(" [CRIT]", output.ToString());
    }

    [Fact]
    public void Test_WarnCritSwitchesValidated()
    {
        foreach (var (args, message) in new[]
        {
            (new[] { ".", "/warn", "0" }, "/warn requires an integer between 1 and 100"),
            (new[] { ".", "/crit", "101" }, "/crit requires an integer between 1 and 100"),
            (new[] { ".", "/warn", "x" }, "/warn requires an integer between 1 and 100"),
            (new[] { ".", "/warn", "95" }, "/warn must be below /crit"),
            (new[] { ".", "/warn", "90", "/crit", "90" }, "/warn must be below /crit")
        })
        {
            var parsed = ArgParser.Parse(args);
            Assert.Equal(message, parsed.Error);
        }
        var custom = ArgParser.Parse(new[] { ".", "/warn", "80", "/crit", "95" }).Options!;
        Assert.Equal(80, custom.Warn);
        Assert.Equal(95, custom.Crit);
        var defaults = ArgParser.Parse(new[] { "." }).Options!;
        Assert.Equal(85, defaults.Warn);
        Assert.Equal(90, defaults.Crit);
    }

    [Fact]
    public void Test_CritVolumeExitsFour()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-fullness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var critical = Run(root, "/summary", "/warn", "1", "/crit", "2");
            Assert.Contains("%", critical.output);
            var percent = double.Parse(critical.output.Split('%')[0].Split(' ').Last(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(percent >= 2);
            Assert.Equal(4, critical.code);
            Assert.Contains(" [CRIT]", critical.output);

            if (percent < 99)
            {
                var healthy = Run(root, "/summary", "/warn", "99", "/crit", "100");
                Assert.Equal(0, healthy.code);
                Assert.DoesNotContain(" [WARN]", healthy.output);
                Assert.DoesNotContain(" [CRIT]", healthy.output);
            }
            else Console.WriteLine("Positive control skipped: temp volume is at least 99% full.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Test_JsonAndCsvCarryLevel()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-fullness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var json = Run(root, "/summary", "/json", "/warn", "1", "/crit", "2");
            using var doc = JsonDocument.Parse(json.output);
            Assert.Equal("crit", doc.RootElement.GetProperty("targets")[0].GetProperty("level").GetString());
            var csv = Run(root, "/summary", "/csv", "/warn", "1", "/crit", "2");
            var lines = csv.output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.EndsWith(",level", lines[0]);
            Assert.EndsWith(",crit", lines[1]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Test_CritWinsOverIncomplete()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-fullness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(4, Run(root, @"\\nonexistent-host-dirused\share", "/summary", "/warn", "1", "/crit", "2").code);
            Assert.Equal(3, Run(root, @"\\nonexistent-host-dirused\share", "/summary").code);
        }
        finally { Directory.Delete(root, true); }
    }
}
