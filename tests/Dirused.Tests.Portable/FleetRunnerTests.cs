using System.Text;
using System.Text.Json;
using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Common.Targets;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class FleetRunnerTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "dirused-fleet-" + Guid.NewGuid().ToString("N"));

    public FleetRunnerTests() => Directory.CreateDirectory(tempDir);

    public void Dispose()
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }

    [Fact]
    public void E1_FileReadsTargetsWithBomCommentsAndBlankLinesAfterTyped()
    {
        string file = Path.Combine(tempDir, "targets.txt");
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(file, "\n# comment line\n  C:\\b  \n\n   # indented comment\nD:\\c\n", utf8Bom);

        var parsed = ArgParser.Parse(new[] { "C:\\a", "/file", file });
        Assert.Null(parsed.Error);
        var opts = Assert.IsType<Options>(parsed.Options);
        Assert.Equal(new[] { "C:\\a", "C:\\b", "D:\\c" }, opts.Targets);
    }

    [Fact]
    public void E1_MissingTargetFileReturnsErrorNamingIt()
    {
        string missing = Path.Combine(tempDir, "does-not-exist.txt");
        var parsed = ArgParser.Parse(new[] { "/file", missing });
        Assert.Null(parsed.Options);
        Assert.NotNull(parsed.Error);
        Assert.Contains("does-not-exist.txt", parsed.Error);
    }

    [Fact]
    public void E3_TimeoutKeepsPartialAndFailureDoesNotStopOthers()
    {
        var slow = TargetParser.Parse("C:\\slow")!;
        var broken = TargetParser.Parse("C:\\broken")!;
        var quick = TargetParser.Parse("C:\\quick")!;
        var partialEntry = new Entry("C:\\slow\\partial", 100, 4096);

        var results = ScanRunner.Run(
            new[] { slow, broken, quick },
            (t, ct) =>
            {
                if (t == slow)
                {
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    return new ScanResult(t.Display, new Volume(t.Root, 1, 1, 2, 50), new() { partialEntry }, new(), new(), true);
                }
                if (t == broken) throw new IOException("network path not found");
                return new ScanResult(t.Display, new Volume(t.Root, 1, 1, 2, 50), new(), new(), new(), true);
            },
            new RunOptions(Parallel: 4, Timeout: TimeSpan.FromMilliseconds(200)),
            CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.False(results[0].Result.Complete);
        Assert.Contains(partialEntry, results[0].Result.Directories);
        Assert.Contains(results[0].Result.Errors, e => e.Reason.Contains("TIMED OUT", StringComparison.Ordinal));

        Assert.False(results[1].Result.Complete);
        Assert.Contains(results[1].Result.Errors, e => e.Reason.Contains("network path not found", StringComparison.Ordinal));

        Assert.True(results[2].Result.Complete);

        // Positive control: all quick targets complete.
        var ok = ScanRunner.Run(
            new[] { quick },
            (t, _) => new ScanResult(t.Display, new Volume(t.Root, 1, 1, 2, 50), new(), new(), new(), true),
            new RunOptions(Parallel: 4, Timeout: TimeSpan.FromSeconds(5)),
            CancellationToken.None);
        Assert.All(ok, r => Assert.True(r.Result.Complete));
    }

    [Fact]
    public void E4_ParallelCapsConcurrencyAndPreservesInputOrder()
    {
        var targets = Enumerable.Range(0, 8).Select(i => TargetParser.Parse($"C:\\t{i}")!).ToList();

        int RunWithLimit(int limit, out IReadOnlyList<TargetResult> outResults)
        {
            int active = 0, maxActive = 0;
            object gate = new();
            outResults = ScanRunner.Run(
                targets,
                (t, _) =>
                {
                    int cur = Interlocked.Increment(ref active);
                    lock (gate) { if (cur > maxActive) maxActive = cur; }
                    // Finish earlier indices slightly slower to force out-of-order completion.
                    int idx = int.Parse(t.Display[^1..]);
                    Thread.Sleep((8 - idx) * 15);
                    Interlocked.Decrement(ref active);
                    return new ScanResult(t.Display, new Volume(t.Root, 1, 1, 2, 50), new(), new(), new(), true);
                },
                new RunOptions(Parallel: limit),
                CancellationToken.None);
            return maxActive;
        }

        int max2 = RunWithLimit(2, out var res2);
        Assert.True(max2 <= 2, $"Expected max concurrency <= 2, got {max2}");
        Assert.Equal(targets.Select(t => t.Display), res2.Select(r => r.Target.Display));

        // Positive control: Parallel 8 reaches more than 2 concurrent calls.
        int max8 = RunWithLimit(8, out var res8);
        Assert.True(max8 > 2, $"Expected max concurrency > 2 with Parallel 8, got {max8}");
        Assert.Equal(targets.Select(t => t.Display), res8.Select(r => r.Target.Display));
    }

    [Fact]
    public void DoubleSlashGoesToTargetParserNotSwitch()
    {
        var res = ArgParser.Parse(new[] { "//server/share" });
        Assert.DoesNotContain("Unknown switch", res.Error ?? string.Empty);

        // Positive control: /top 5 still parses as a switch.
        var opts = Assert.IsType<Options>(ArgParser.Parse(new[] { ".", "/top", "5" }).Options);
        Assert.Equal(5, opts.Top);
    }

    [Fact]
    public void E5_HelpListsAvailableSwitchesTargetsAndExitCodesUnder80Columns()
    {
        var help = ArgParser.Parse(new[] { "/help" });
        Assert.True(help.Usage);
        string text = ArgParser.UsageText;

        foreach (string required in new[] { "/top", "/json", "/csv", "/all", "/file", "/summary", "/timeout", "/parallel", "//server/share", "c:/" })
            Assert.Contains(required, text);
        Assert.Contains("exit", text, StringComparison.OrdinalIgnoreCase);

        foreach (string refused in new[] { "/purge", "/monitor", "/gui" })
            Assert.DoesNotContain(refused, text);

        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            Assert.True(line.Length < 80, $"Line exceeds 79 columns ({line.Length}): {line}");

        // Positive control: invalid target and empty args.
        Assert.Equal("Invalid target: C:foo", ArgParser.Parse(new[] { "C:foo" }).Error);
        var empty = ArgParser.Parse(Array.Empty<string>());
        Assert.Equal(ArgParser.ShortUsageText, empty.Error);
        Assert.Contains("Run dirused /help for more.", empty.Error);
    }

    [Fact]
    public void SummaryRenderersDeduplicateSortAndFormat()
    {
        var r1 = new ScanResult("C:\\a", new Volume("C:\\", 300, 700, 1000, 30.0) { SerialNumber = 111 }, new(), new(), new(), true);
        var r2 = new ScanResult("C:\\b", new Volume("C:\\", 300, 700, 1000, 30.0) { SerialNumber = 111 }, new(), new(), new(), true);
        var r3 = new ScanResult("D:\\", new Volume("D:\\", 800, 200, 1000, 80.0) { SerialNumber = 222 }, new(), new(), new(), true);
        var rFail = new ScanResult("Z:\\", new Volume("Z:\\", 0, 0, 0, 0), new(), new(), new() { new ScanError("Z:\\", "Access denied") }, false);
        var all = new[] { r1, r2, r3, rFail };

        using var textOut = new StringWriter();
        new SummaryRenderer().Render(textOut, all);
        var lines = textOut.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("D:\\", lines[0]);
        Assert.StartsWith("C:\\", lines[1]);
        Assert.Equal("Z:\\: Access denied", lines[2]);

        using var csvOut = new StringWriter();
        new CsvRenderer(summary: true).Render(csvOut, all);
        Assert.StartsWith("kind,target,used_bytes,free_bytes,total_bytes,percent_used", csvOut.ToString());

        using var jsonOut = new StringWriter();
        new JsonRenderer(summary: true).Render(jsonOut, all);
        using var doc = JsonDocument.Parse(jsonOut.ToString());
        Assert.True(doc.RootElement.GetProperty("summary").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("targets").GetArrayLength());
    }

    [Fact]
    public void CtrlCOnAnIncompleteScanStillReportsCancelled()
    {
        var target = TargetParser.Parse("C:\\walk")!;
        using var cts = new CancellationTokenSource();
        var results = ScanRunner.Run(
            new[] { target },
            (t, _) =>
            {
                cts.Cancel();
                return new ScanResult(t.Display, new Volume(t.Root, 1, 1, 2, 50), new(), new(), new(), false);
            },
            new RunOptions(Parallel: 1),
            cts.Token);
        Assert.False(results[0].Result.Complete);
        Assert.Contains(results[0].Result.Errors, e => e.Reason == "CANCELLED");

        // Positive control: no Ctrl+C, no CANCELLED.
        var ok = ScanRunner.Run(
            new[] { target },
            (t, _) => new ScanResult(t.Display, new Volume(t.Root, 1, 1, 2, 50), new(), new(), new(), true),
            new RunOptions(Parallel: 1),
            CancellationToken.None);
        Assert.True(ok[0].Result.Complete);
        Assert.Empty(ok[0].Result.Errors);
    }

    [Fact]
    public void SummaryReportsIncompleteTargetWithVolumeAsFailure()
    {
        var timedOut = new ScanResult("E:\\slow", new Volume("E:\\", 400, 600, 1000, 40.0) { SerialNumber = 333 }, new(), new(), new() { new ScanError("E:\\slow", "TIMED OUT after 5s") }, false);
        var sameVolume = new ScanResult("E:\\ok", new Volume("E:\\", 400, 600, 1000, 40.0) { SerialNumber = 333 }, new(), new(), new(), true);

        using var textOut = new StringWriter();
        new SummaryRenderer().Render(textOut, new[] { timedOut, sameVolume });
        var lines = textOut.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Contains("E:\\slow: TIMED OUT after 5s", lines);

        // Positive control: the complete target on the same volume still prints its volume line.
        Assert.Contains(lines, l => l.StartsWith("E:\\ used", StringComparison.Ordinal));
        Assert.Equal(2, lines.Length);
    }
}
