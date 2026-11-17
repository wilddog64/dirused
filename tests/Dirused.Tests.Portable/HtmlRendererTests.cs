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
            R(@"\\srv1\c$", 95), R(@"\\srv1\d$", 50), R(@"\\srv2\c$", 87)
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

    private static ScanResult R(string target, double percent) => new(target,
        new Volume(target.EndsWith("d$", StringComparison.OrdinalIgnoreCase) ? @"D:\" : @"C:\", (long)(percent * 100), (long)((100 - percent) * 100), 10000, percent),
        new() { new Entry("folder", 10, 10) }, new(), new(), true);
}
