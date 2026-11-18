using Dirused.Common.Renderers;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class HtmlRendererTests
{
    [Fact]
    public void H6_RendersMultipleHostsInDescendingOrder()
    {
        var results = new[]
        {
            R(@"\\srv2\c$", 87), R(@"\\srv1\d$", 50), R(@"\\srv1\c$", 95)
        };
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 50).Render(writer, results);
        var html = writer.ToString();
        var nodes = html.Substring(html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal), html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal) - html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal));
        var volumeRows = html.Substring(html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal));
        Assert.True(nodes.IndexOf("srv1", StringComparison.Ordinal) < nodes.IndexOf("srv2", StringComparison.Ordinal));
        Assert.True(volumeRows.IndexOf("95.0%", StringComparison.Ordinal) < volumeRows.IndexOf("87.0%", StringComparison.Ordinal));
        Assert.True(volumeRows.IndexOf("87.0%", StringComparison.Ordinal) < volumeRows.IndexOf("50.0%", StringComparison.Ordinal));
        Assert.Contains("<tr class=\"crit\">", html);
        Assert.Contains("<tr class=\"warn\">", html);
        Assert.Contains("<td class=\"num\">50.0% <span class=\"bar\">", html);
    }

    [Fact]
    public void H7_EncodesResultStrings()
    {
        var result = R("<script>&\"", 50) with { Errors = new() { new("", "<script>&\"") }, Complete = false };
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 5).Render(writer, new[] { result });
        Assert.Contains("&lt;script&gt;&amp;&quot;", writer.ToString());
        Assert.DoesNotContain("<script>", writer.ToString());
    }

    [Fact]
    public void H8_FailuresAppearOnlyWhenPresent()
    {
        using var failed = new StringWriter();
        new HtmlRenderer(true, 85, 90, 5).Render(failed, new[] { R("\\\\host\\c$", 0) with { Complete = false, Errors = new() { new("", "offline") } } });
        Assert.Contains("class=\"failed\"", failed.ToString());
        Assert.Contains("offline", failed.ToString());
        using var complete = new StringWriter();
        new HtmlRenderer(true, 85, 90, 5).Render(complete, new[] { R("\\\\host\\c$", 50) });
        Assert.DoesNotContain("class=\"failed\"", complete.ToString());
    }

    [Fact]
    public void H9_LocalHostUsesMachineName()
    {
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 5).Render(writer, new[] { R(@"C:\", 50) });
        Assert.Contains($"<td>{System.Net.WebUtility.HtmlEncode(Environment.MachineName)}</td>", writer.ToString());
    }

    [Fact]
    public void H15_HostNamesDifferingOnlyInCaseShareOneNodeRow()
    {
        var results = new[] { R(@"\\srv1\c$", 95), R(@"\\SRV1\d$", 50) };
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 50).Render(writer, results);
        var html = writer.ToString();
        var nodes = html.Substring(html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal), html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal) - html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal));
        Assert.Equal(1, nodes.Split("</td></tr>").Length - 1);
        Assert.Contains("<td class=\"num\">2</td>", nodes);
    }

    [Fact]
    public void H16_ColoursFollowWarnAndCrit()
    {
        var results = new[] { R(@"\\srv1\c$", 95), R(@"\\srv2\c$", 87), R(@"\\srv3\c$", 10) };
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 50).Render(writer, results);
        var html = writer.ToString();
        var nodes = html.Substring(html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal), html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal) - html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal));
        Assert.Contains("<tr class=\"crit\"><td>srv1</td>", nodes);
        Assert.Contains("<tr class=\"warn\"><td>srv2</td>", nodes);
        Assert.Contains("<tr><td>srv3</td>", nodes);
        Assert.Contains("<span class=\"lvl\">CRIT</span>", html);
        Assert.Contains("<span class=\"lvl\">WARN</span>", html);
        Assert.Contains(".crit .bar>span{background:", html);
        Assert.Contains(".warn .bar>span{background:", html);

        using var lowered = new StringWriter();
        new HtmlRenderer(true, 5, 9, 50).Render(lowered, results);
        Assert.Contains("<tr class=\"crit\"><td>srv3</td>", lowered.ToString());
    }

    [Fact]
    public void H17_OkVolumesAreGreen()
    {
        var results = new[] { R(@"\\srv1\c$", 95), R(@"\\srv2\c$", 87), R(@"\\srv3\c$", 10) };
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 50).Render(writer, results);
        var html = writer.ToString();
        var volumes = html.Substring(html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal));
        Assert.Contains("<tr class=\"ok\"><td>srv3</td>", volumes);
        Assert.Contains("<span class=\"lvl\">OK</span>", volumes);
        Assert.Contains("<tr class=\"crit\"><td>srv1</td>", volumes);
        Assert.Contains("<tr class=\"warn\"><td>srv2</td>", volumes);
        Assert.Contains(".ok .bar>span{background:", html);
        Assert.Contains(".ok .lvl{", html);
        Assert.DoesNotContain(".ok{", html);

        var nodes = html.Substring(html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal), html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal) - html.IndexOf("<table class=\"nodes\"", StringComparison.Ordinal));
        Assert.Contains("<tr><td>srv3</td>", nodes);
    }

    [Fact]
    public void H18_IncompleteVolumeHasNoLevel()
    {
        var incomplete = R(@"\\srv1\c$", 10) with { Complete = false };
        var zeroTotal = R(@"\\srv2\c$", 10) with { Volume = new Volume(@"C:\", 0, 0, 0, 0) };
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 50).Render(writer, new[] { incomplete, zeroTotal });
        var html = writer.ToString();
        Assert.DoesNotContain("class=\"ok\"", html);
        var volumes = html.Substring(html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal));
        var zeroTotalRow = volumes.Split("<tr>", StringSplitOptions.None).Single(row => row.Contains("<td>srv2</td>", StringComparison.Ordinal));
        Assert.Contains("</span></td><td></td><td>", zeroTotalRow);
        Assert.Contains("<td>srv1</td>", html);
        Assert.Contains("class=\"failed\"", html);
    }

    [Fact]
    public void H19_SmallFillHasMinimumWidth()
    {
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 50).Render(writer, new[] { R(@"\\srv1\c$", 1) });
        Assert.Contains(".bar>span{", writer.ToString());
        Assert.Contains(".bar>span{display:block;height:100%;min-width:3px;", writer.ToString());
    }

    [Fact]
    public void H20_ZeroFillDrawsNoSliver()
    {
        var zeroPercent = R(@"\\srv1\c$", 0);
        var zeroTotal = R(@"\\srv2\c$", 0) with { Volume = new Volume(@"C:\", 0, 0, 0, 0) };
        using var writer = new StringWriter();
        new HtmlRenderer(true, 85, 90, 50).Render(writer, new[] { zeroPercent, zeroTotal, R(@"\\srv3\c$", 1) });
        var html = writer.ToString();
        var volumes = html.Substring(html.IndexOf("<table class=\"volumes\"", StringComparison.Ordinal));

        foreach (var host in new[] { "srv1", "srv2" })
        {
            var row = volumes.Split("</tr>", StringSplitOptions.None).Single(row => row.Contains($"<td>{host}</td>", StringComparison.Ordinal));
            Assert.Contains("<span class=\"bar\"></span>", row);
            Assert.DoesNotContain("<span class=\"bar\"><span", row);
        }

        Assert.Contains("<span class=\"bar\"><span style=\"width:1%\"></span></span>", volumes);
    }

    private static ScanResult R(string target, double percent) => new(target,
        new Volume(target.EndsWith("d$", StringComparison.OrdinalIgnoreCase) ? @"D:\" : @"C:\", (long)(percent * 100), (long)((100 - percent) * 100), 10000, percent),
        new() { new Entry("folder", 10, 10) }, new(), new(), true);
}
