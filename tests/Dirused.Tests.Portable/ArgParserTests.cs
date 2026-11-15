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
    [Fact] public void UncPassesThroughParser()
    {
        const string target = @"\\localhost\c$";
        var result = ArgParser.Parse(new[] { target });
        Assert.Null(result.Error);
        Assert.Contains(target, Assert.IsType<Options>(result.Options).Targets);
    }
    [Fact] public void ParsesTopAndJson()
    {
        var options = Assert.IsType<Options>(ArgParser.Parse(new[] { ".", "/top", "5", "/json" }).Options);
        Assert.Equal(5, options.Top); Assert.Equal(OutputFormat.Json, options.Format);
    }
    [Fact] public void RunnerPreservesOrder()
    {
        var targets = new[] { TargetParser.Parse(".")!, TargetParser.Parse(@"C:\")! };
        var results = ScanRunner.Run(targets, (t, _) => new ScanResult(t.Display, new Volume("", 0, 0, 0, 0), new(), new(), new(), true), new RunOptions(), CancellationToken.None);
        Assert.Equal(targets, results.Select(r => r.Target));
    }
}
