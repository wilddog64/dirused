using Dirused.Common;
using Dirused.Common.Targets;
using Dirused.Core;
using Xunit;
namespace Dirused.Tests.Portable;
public class ArgParserTests
{
    [Theory]
    [InlineData("/purge", "not available yet (M2)")]
    [InlineData("/monitor", "not available yet (M3)")]
    [InlineData("/threshold", "not available yet (M3)")]
    [InlineData("/gui", "not available yet (GUI)")]
    public void Unavailable(string flag, string expected) => Assert.Equal(expected, ArgParser.Parse(new[] { ".", flag }).Error);

    [Fact]
    public void UncPassesThroughParser()
    {
        const string target = @"\\localhost\c$";
        var result = ArgParser.Parse(new[] { target });
        Assert.Null(result.Error);
        Assert.Contains(target, Assert.IsType<Options>(result.Options).Targets);
    }

    [Fact]
    public void ParsesTopAndJson()
    {
        var options = Assert.IsType<Options>(ArgParser.Parse(new[] { ".", "/top", "5", "/json" }).Options);
        Assert.Equal(5, options.Top); Assert.Equal(OutputFormat.Json, options.Format);
    }

    [Fact]
    public void RunnerPreservesOrder()
    {
        var targets = new[] { TargetParser.Parse(".")!, TargetParser.Parse(@"C:\")! };
        var results = ScanRunner.Run(targets, (t, _) => new ScanResult(t.Display, new Volume("", 0, 0, 0, 0), new(), new(), new(), true), new RunOptions(), CancellationToken.None);
        Assert.Equal(targets, results.Select(r => r.Target));
    }

    [Fact]
    public void Test8_DeepAndSummaryOverridesAndHelpWidth()
    {
        // /deep 0, /deep 33 rejected
        Assert.Equal("/deep level must be an integer between 1 and 32", ArgParser.Parse(new[] { ".", "/deep", "0" }).Error);
        Assert.Equal("/deep level must be an integer between 1 and 32", ArgParser.Parse(new[] { ".", "/deep", "33" }).Error);

        // /deep at the end of the line -> level 2
        var endResult = ArgParser.Parse(new[] { ".", "/deep" });
        Assert.Null(endResult.Error);
        Assert.Equal(2, endResult.Options!.Deep);

        // /deep C:\x -> level 2 with target C:\x
        var targetResult = ArgParser.Parse(new[] { "/deep", @"C:\x" });
        Assert.Null(targetResult.Error);
        Assert.Equal(2, targetResult.Options!.Deep);
        Assert.Contains(@"C:\x", targetResult.Options.Targets);

        // /deep /json -> level 2 and JSON
        var jsonResult = ArgParser.Parse(new[] { ".", "/deep", "/json" });
        Assert.Null(jsonResult.Error);
        Assert.Equal(2, jsonResult.Options!.Deep);
        Assert.Equal(OutputFormat.Json, jsonResult.Options.Format);

        // /summary /deep 2 and /summary /exact parse, and their options say walk (Summary overridden)
        var sumDeep = ArgParser.Parse(new[] { ".", "/summary", "/deep", "2" });
        Assert.Null(sumDeep.Error);
        Assert.False(sumDeep.Options!.Summary);
        Assert.Equal(2, sumDeep.Options.Deep);

        var sumExact = ArgParser.Parse(new[] { ".", "/summary", "/exact" });
        Assert.Null(sumExact.Error);
        Assert.False(sumExact.Options!.Summary);
        Assert.True(sumExact.Options.Exact);

        // /summary alone still selects the volume-only path (control)
        var sumAlone = ArgParser.Parse(new[] { ".", "/summary" });
        Assert.Null(sumAlone.Error);
        Assert.True(sumAlone.Options!.Summary);

        // help lines within 80 columns
        foreach (var line in ArgParser.UsageText.Split('\n'))
            Assert.True(line.Length <= 80, $"UsageText line exceeds 80 columns: '{line}' ({line.Length})");
        foreach (var line in ArgParser.ShortUsageText.Split('\n'))
            Assert.True(line.Length <= 80, $"ShortUsageText line exceeds 80 columns: '{line}' ({line.Length})");
    }

    [Fact]
    public void Test9_MultiTargetWarning()
    {
        // Two targets plus /deep 2 and no /timeout -> stderr line once
        var opts1 = ArgParser.Parse(new[] { "a", "b", "/deep", "2" }).Options!;
        using (var err1 = new StringWriter())
        {
            bool warned = ArgParser.WarnIfMultiTarget(2, opts1, err1);
            Assert.True(warned);
            var errText = err1.ToString();
            Assert.Contains(ArgParser.MultiTargetWarning, errText);
            int count = (errText.Length - errText.Replace(ArgParser.MultiTargetWarning, "").Length) / ArgParser.MultiTargetWarning.Length;
            Assert.Equal(1, count);
        }

        // With /timeout 5m -> no line
        var opts2 = ArgParser.Parse(new[] { "a", "b", "/deep", "2", "/timeout", "5m" }).Options!;
        using (var err2 = new StringWriter())
        {
            bool warned = ArgParser.WarnIfMultiTarget(2, opts2, err2);
            Assert.False(warned);
            Assert.DoesNotContain(ArgParser.MultiTargetWarning, err2.ToString());
        }

        // One target -> no line (controls)
        var opts3 = ArgParser.Parse(new[] { "a", "/deep", "2" }).Options!;
        using (var err3 = new StringWriter())
        {
            bool warned = ArgParser.WarnIfMultiTarget(1, opts3, err3);
            Assert.False(warned);
            Assert.DoesNotContain(ArgParser.MultiTargetWarning, err3.ToString());
        }

        // Two targets with /exact and no /timeout -> stderr line once
        var opts4 = ArgParser.Parse(new[] { "a", "b", "/exact" }).Options!;
        using (var err4 = new StringWriter())
        {
            bool warned = ArgParser.WarnIfMultiTarget(2, opts4, err4);
            Assert.True(warned);
            Assert.Contains(ArgParser.MultiTargetWarning, err4.ToString());
        }

        // Two targets with no /deep and no /exact -> no line
        var opts5 = ArgParser.Parse(new[] { "a", "b" }).Options!;
        using (var err5 = new StringWriter())
        {
            bool warned = ArgParser.WarnIfMultiTarget(2, opts5, err5);
            Assert.False(warned);
            Assert.DoesNotContain(ArgParser.MultiTargetWarning, err5.ToString());
        }
    }
}
