using Dirused.Common;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public class ScheduleTests
{
    private static ParsedPurgeList List(params string[] paths) => new(1, 1, paths.Select(p => new LargestFile(p, 1L << 30, null)).ToArray(), Array.Empty<BadPurgeListLine>());
    private static TimeZoneInfo Pacific() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneInfo.TryConvertIanaIdToWindowsId("America/Los_Angeles", out var win) ? win : "America/Los_Angeles");
    private static StringWriter Writer() => new() { NewLine = "\n" };
    private static DateTimeOffset MondayNow => new(2026, 11, 23, 21, 0, 30, TimeSpan.FromHours(-8));

    [Fact]
    public void PreviewPrintsPacificAndUtcAndUsesFilePathHost()
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l.txt", "/purge", "/schedule", "22:30" }).Options!;
        using var output = Writer(); using var error = Writer();
        var sampleRows = Enumerable.Range(0, 12).Select(i => new LargestFile($@"\\us03p02ffm01\share\file{i}", i == 0 ? (long)(176.2 * (1L << 30)) : 0, null)).ToArray();
        int code = Schedule.Run(options, new ParsedPurgeList(1, 1, sampleRows, Array.Empty<BadPurgeListLine>()), "l.txt", output, error, () => MondayNow, Pacific());
        Assert.Equal(0, code); Assert.Empty(error.ToString());
        Assert.Equal(new[]
        {
            "Scheduled purge preview (nothing is scheduled yet)",
            "  Run:      Mon 2026-11-23 22:30 PST (06:30 UTC Tue), in 1 h 29 min",
            "  Expires:  Tue 2026-11-24 21:00 PST (05:00 UTC Wed)",
            "  Host:     us03p02ffm01",
            "  List:     l.txt",
            "            12 file(s), 176.2 GB",
            "  Held:     not checked now; files held open at run time are skipped",
            "It runs only while you are logged on to this machine (disconnected is fine).",
            "Preview only. Add /confirm to schedule it."
        }, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.All(output.ToString().Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 80));
    }

    [Fact]
    public void ConfirmPreviewDoesNotClaimToSchedule()
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "/confirm" }).Options!;
        using var output = Writer(); using var error = Writer();
        Assert.Equal(1, Schedule.Run(options, List(@"C:\a"), "l", output, error, () => MondayNow, Pacific()));
        Assert.DoesNotContain("Preview only.", output.ToString());
        Assert.Equal("/schedule /confirm is not available in this version; nothing was scheduled\n", error.ToString());
    }

    [Fact]
    public void ParserAcceptsOnlyScheduleZonesAndRejectsOtherOperations()
    {
        var parsed = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "pst" });
        Assert.Equal("PST", parsed.Options!.ScheduleZone);
        Assert.Equal("/fromlist can only be combined with /purge, /confirm, /html, /output, /json, /csv, /yes and /schedule", ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "BOGUS" }).Error);
        Assert.Equal("/schedule requires /fromlist", ArgParser.Parse(new[] { "/schedule", "/list" }).Error);
        Assert.Equal("Unknown switch: /list", ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "/list" }).Error);
    }

    [Fact]
    public void RejectsMultipleHostsAndEmptyLists()
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30" }).Options!;
        using var output = Writer(); using var error = Writer();
        Assert.Equal(1, Schedule.Run(options, List(@"\\hostA\a", @"\\HOSTB\b"), "l", output, error, () => MondayNow, Pacific()));
        Assert.Contains("(hostA, HOSTB)", error.ToString()); Assert.Empty(output.ToString());
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(1, Schedule.Run(options, List(), "l", output, error, () => MondayNow, Pacific()));
        Assert.Equal("/schedule: the list has no files to delete\n", error.ToString());
    }

    [Fact]
    public void RecognizesExtendedUncAndDrivePaths()
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30" }).Options!;
        using var output = Writer(); using var error = Writer();
        Assert.Equal(0, Schedule.Run(options, List(@"\\?\UNC\h1\x", @"//H1/y"), "l", output, error, () => MondayNow, Pacific()));
        Assert.Contains("Host:     h1", output.ToString());
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(0, Schedule.Run(options, List(@"\\?\C:\x"), "l", output, error, () => MondayNow, Pacific()));
        Assert.Contains("Host:     this machine", output.ToString());
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(0, Schedule.Run(options, List(@"\\.\C:\x"), "l", output, error, () => MondayNow, Pacific()));
        Assert.Contains("Host:     this machine", output.ToString());
        foreach (var path in new[] { @"\\", @"\\\x", @"\\?\UNC\", @"\\.\pipe\x" })
        {
            output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
            Assert.Equal(1, Schedule.Run(options, List(path), "l", output, error, () => MondayNow, Pacific()));
            Assert.Empty(output.ToString());
            Assert.Equal($"/schedule: cannot tell which server {path} is on\n", error.ToString());
        }
    }

    [Fact]
    public void ConfirmAndYesAlsoRefusesScheduling()
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "/confirm", "/yes" }).Options!;
        using var output = Writer(); using var error = Writer();
        Assert.Equal(1, Schedule.Run(options, List(@"C:\a"), "l", output, error, () => MondayNow, Pacific()));
        Assert.DoesNotContain("Preview only.", output.ToString());
        Assert.Equal("/schedule /confirm is not available in this version; nothing was scheduled\n", error.ToString());
    }

    [Fact]
    public void TimeRulesCoverZonesDstAndBoundaries()
    {
        var winter = new DateTimeOffset(2026, 11, 23, 21, 0, 0, TimeSpan.FromHours(-8));
        AssertPreview(new[] { "20:00" }, winter, Pacific(), "Tue 2026-11-24 20:00 PST (04:00 UTC Wed)");
        AssertPreview(new[] { "13:00", "PT" }, new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(-7)), Pacific(), "Wed 2026-07-15 13:00 PDT (20:00 UTC Wed)");
        AssertPreview(new[] { "22:30", "PST" }, winter, Pacific(), "Mon 2026-11-23 22:30 PST (06:30 UTC Tue)");
        AssertPreview(new[] { "22:30", "PST" }, new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(-7)), Pacific(), "Wed 2026-07-15 23:30 PDT (06:30 UTC Thu)");
        AssertPreview(new[] { "22:30", "PDT" }, new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(-7)), Pacific(), "Wed 2026-07-15 22:30 PDT (05:30 UTC Thu)");
        AssertPreview(new[] { "22:30", "UTC" }, winter, Pacific(), "Tue 2026-11-24 14:30 PST (22:30 UTC Tue)");
        AssertError(new[] { "22:30" }, winter, TimeZoneInfo.Utc, "/schedule: this machine is not on Pacific time; add PT, PST, PDT or UTC to the time");
        AssertPreview(new[] { "22:30" }, winter, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"), "Mon 2026-11-23 22:30 PST (06:30 UTC Tue)");
        AssertError(new[] { "02:30", "PT" }, new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-8)), Pacific(), "/schedule: 2026-03-08 02:30 does not exist in Pacific time (clocks go forward); pick another time");
        AssertError(new[] { "01:30", "PT" }, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-7)), Pacific(), "/schedule: 2026-11-01 01:30 happens twice in Pacific time; write PST or PDT");
        AssertError(new[] { "01:30", "PT" }, new DateTimeOffset(2026, 11, 1, 1, 45, 0, TimeSpan.FromHours(-7)), Pacific(), "/schedule: 2026-11-01 01:30 happens twice in Pacific time; write PST or PDT");
        AssertError(new[] { "01:30", "PT" }, new DateTimeOffset(2026, 11, 1, 1, 15, 0, TimeSpan.FromHours(-8)), Pacific(), "/schedule: 2026-11-01 01:30 happens twice in Pacific time; write PST or PDT");
        AssertPreview(new[] { "01:30", "PT" }, new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.FromHours(-8)), Pacific(), "Mon 2026-11-02 01:30 PST");
        AssertError(new[] { "02:30", "PT" }, new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.FromHours(-8)), Pacific(), "/schedule: 2026-03-08 02:30 does not exist in Pacific time (clocks go forward); pick another time");
        AssertPreview(new[] { "02:30", "PT" }, new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.FromHours(-7)), Pacific(), "Mon 2026-03-09 02:30 PDT");
        AssertPreview(new[] { "01:30", "PDT" }, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-7)), Pacific(), "Sun 2026-11-01 01:30 PDT (08:30 UTC Sun)");
        AssertPreview(new[] { "21:02" }, new DateTimeOffset(2026, 11, 23, 21, 0, 0, TimeSpan.FromHours(-8)), Pacific(), "Mon 2026-11-23 21:02 PST");
        AssertError(new[] { "21:02" }, new DateTimeOffset(2026, 11, 23, 21, 0, 1, TimeSpan.FromHours(-8)), Pacific(), "/schedule: 21:02 is less than 2 minutes away; pick a later time");
        AssertPreview(new[] { "20:45" }, new DateTimeOffset(2026, 11, 23, 21, 0, 0, TimeSpan.FromHours(-8)), Pacific(), "Tue 2026-11-24 20:45 PST");
        AssertError(new[] { "20:46" }, new DateTimeOffset(2026, 11, 23, 21, 0, 59, TimeSpan.FromHours(-8)), Pacific(), "/schedule: 20:46 is too far away; the plan expires 24 h after it is made, so pick a time within 23 h 45 min");
        AssertPreview(new[] { "21:45" }, winter, Pacific(), "in 45 min");
        AssertPreview(new[] { "23:00" }, winter, Pacific(), "in 2 h 0 min");
    }

    [Fact]
    public void DayBeforeSpringForwardRejectsNextDaysGap()
    {
        AssertError(new[] { "02:30", "PT" }, new DateTimeOffset(2026, 3, 7, 22, 0, 0, TimeSpan.FromHours(-8)), Pacific(), "/schedule: 2026-03-08 02:30 does not exist in Pacific time (clocks go forward); pick another time");
    }

    [Fact]
    public void DayBeforeFallBackRejectsNextDaysOverlap()
    {
        AssertError(new[] { "01:30", "PT" }, new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.FromHours(-7)), Pacific(), "/schedule: 2026-11-01 01:30 happens twice in Pacific time; write PST or PDT");
    }

    [Fact]
    public void ListPathWrapsAt68AndBadLinesAreReportedAfterPreview()
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30" }).Options!;
        string path = new('x', 150);
        using var output = Writer(); using var error = Writer();
        Assert.Equal(0, Schedule.Run(options, List(@"C:\a"), path, output, error, () => MondayNow, Pacific()));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var pathLines = lines.Where(l => l.StartsWith("  List:     ") || l.StartsWith("            ")).Take(3).ToArray();
        Assert.Equal("  List:     " + path[..68], pathLines[0]);
        Assert.Equal("            " + path.Substring(68, 68), pathLines[1]);
        Assert.Equal("            " + path[136..], pathLines[2]);
        Assert.Equal(path, string.Concat(pathLines.Select((line, i) => line[(i == 0 ? 12 : 12)..])));

        var badList = new ParsedPurgeList(1, 1, new[] { new LargestFile(@"C:\a", 1L << 30, null) }, new[] { new BadPurgeListLine(4, "bad size"), new BadPurgeListLine(8, "bad file ID") });
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(3, Schedule.Run(options, badList, "l", output, error, () => MondayNow, Pacific()));
        Assert.Contains("Preview only. Add /confirm to schedule it.", output.ToString());
        Assert.Equal("Bad list lines:" + Environment.NewLine + "  line 4: bad size" + Environment.NewLine + "  line 8: bad file ID" + Environment.NewLine, error.ToString());
        var confirm = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "/confirm" }).Options!;
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(1, Schedule.Run(confirm, badList, "l", output, error, () => MondayNow, Pacific()));
        Assert.DoesNotContain("Preview only.", output.ToString());
        Assert.StartsWith("Scheduled purge preview", output.ToString());
        Assert.EndsWith("(disconnected is fine).\n", output.ToString());
        Assert.Equal("Bad list lines:\n  line 4: bad size\n  line 8: bad file ID\n/schedule /confirm is not available in this version; nothing was scheduled\n", error.ToString());
    }

    [Fact]
    public void PreviewDoesNotTouchListOrRowPathsOrUseIoApis()
    {
        string temp = Path.Combine(Path.GetTempPath(), "dirused-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string listPath = Path.Combine(temp, "missing-list.txt");
            string rowPath = Path.Combine(temp, "missing-row.bin");
            var before = Snapshot(temp);
            var options = ArgParser.Parse(new[] { "/fromlist", listPath, "/purge", "/schedule", "22:30" }).Options!;
            using var output = Writer(); using var error = Writer();
            Assert.Equal(0, Schedule.Run(options, List(rowPath), listPath, output, error, () => MondayNow, Pacific()));
            Assert.Contains("Scheduled purge preview", output.ToString());
            Assert.Equal(before, Snapshot(temp));

            string? root = Path.GetDirectoryName(typeof(ScheduleTests).Assembly.Location);
            while (root != null && !File.Exists(Path.Combine(root, "src", "Dirused.Common", "Schedule.cs"))) root = Path.GetDirectoryName(root);
            Assert.NotNull(root);
            string source = File.ReadAllText(Path.Combine(root!, "src", "Dirused.Common", "Schedule.cs"));
            foreach (var token in new[] { "File.", "Directory.", "Console.", "Process.", "Registry", "FileInfo", "DirectoryInfo", "FileStream", "StreamWriter", "StreamReader", "System.IO.File", "DllImport", "TaskService" }) Assert.DoesNotContain(token, source);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void AssertPreview(string[] schedule, DateTimeOffset now, TimeZoneInfo local, string expected)
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule" }.Concat(schedule).ToArray()).Options!;
        using var output = Writer(); using var error = Writer();
        Assert.Equal(0, Schedule.Run(options, List(@"C:\a"), "l", output, error, () => now, local));
        Assert.Empty(error.ToString());
        Assert.Contains(expected, output.ToString());
    }

    private static void AssertError(string[] schedule, DateTimeOffset now, TimeZoneInfo local, string expected)
    {
        var options = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule" }.Concat(schedule).ToArray()).Options!;
        using var output = Writer(); using var error = Writer();
        Assert.Equal(1, Schedule.Run(options, List(@"C:\a"), "l", output, error, () => now, local));
        Assert.Empty(output.ToString());
        Assert.Equal(expected + Environment.NewLine, error.ToString());
    }

    private static (string Name, long Size, DateTime Modified)[] Snapshot(string directory) => Directory.GetFileSystemEntries(directory)
        .Select(path => (Path.GetFileName(path), new FileInfo(path).Length, Directory.GetLastWriteTimeUtc(path)))
        .OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
}
