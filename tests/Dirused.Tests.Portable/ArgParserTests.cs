using Dirused.Common;
using Dirused.Common.Targets;
using Dirused.Core;
using Xunit;
namespace Dirused.Tests.Portable;
public class ArgParserTests
{
    [Fact]
    public void BareWarnAndCritUseDefaultsWithoutConsumingFollowingArguments()
    {
        var a1 = ArgParser.Parse(new[] { @"C:\", "/crit", "/warn" });
        Assert.Null(a1.Error);
        Assert.Equal(85, a1.Options!.Warn);
        Assert.Equal(90, a1.Options.Crit);
        Assert.Equal(new[] { @"C:\" }, a1.Options.Targets);

        string file = Path.Combine(Path.GetTempPath(), "servers.list");
        File.WriteAllText(file, @"C:\" + Environment.NewLine);
        try
        {
            var fileArgument = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
            var a2 = ArgParser.Parse(new[] { "/summary", "/crit", "/warn", "/file", fileArgument });
            Assert.Null(a2.Error);
            Assert.Equal(85, a2.Options!.Warn);
            Assert.Equal(90, a2.Options.Crit);
            Assert.Equal(fileArgument, a2.Options.File);
            Assert.True(a2.Options.Summary);
        }
        finally { File.Delete(file); }

        var a3 = ArgParser.Parse(new[] { "/warn", @"C:\" });
        Assert.Null(a3.Error);
        Assert.Equal(85, a3.Options!.Warn);
        Assert.Equal(new[] { @"C:\" }, a3.Options.Targets);
    }

    [Fact]
    public void WarnCritNumbersAndValidationRemainSupported()
    {
        var a4 = ArgParser.Parse(new[] { "/warn", "70", "/crit", "80", @"C:\" });
        Assert.Null(a4.Error);
        Assert.Equal(70, a4.Options!.Warn);
        Assert.Equal(80, a4.Options.Crit);
        Assert.Equal("/crit requires an integer between 1 and 100", ArgParser.Parse(new[] { "/crit", "150", @"C:\" }).Error);
        Assert.Equal("/warn must be below /crit", ArgParser.Parse(new[] { "/crit", "/warn", "95", @"C:\" }).Error);
    }

    [Fact]
    public void PurgeSwitches()
    {
        var basic = ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge" });
        Assert.Null(basic.Error); Assert.Equal(1L << 30, Property<long>(basic.Options!, "PurgeMinBytes"));
        Assert.True(Property<bool>(basic.Options!, "Purge"));
        Assert.Equal(1, Property<int>(basic.Options!, "PurgeDays")); Assert.False(Property<bool>(basic.Options!, "Confirm"));
        Assert.False(Property<bool>(basic.Options!, "PurgeNeedsPatterns"));
        var seven = ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "7", "/confirm" });
        Assert.Null(seven.Error); Assert.Equal(7, Property<int>(seven.Options!, "PurgeDays")); Assert.True(Property<bool>(seven.Options!, "Confirm"));
        Assert.False(Property<bool>(seven.Options!, "PurgeNeedsPatterns"));
        var logConfirm = ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/confirm" });
        Assert.Null(logConfirm.Error); Assert.False(Property<bool>(logConfirm.Options!, "PurgeNeedsPatterns"));
        Assert.Equal("/purge requires /largest", ArgParser.Parse(new[] { @"C:\", "/purge" }).Error);
        Assert.Equal("/confirm requires /purge", ArgParser.Parse(new[] { @"C:\", "/confirm" }).Error);
        var noPattern = ArgParser.Parse(new[] { @"C:\", "/largest", "/purge" });
        Assert.Null(noPattern.Error); Assert.True(Property<bool>(noPattern.Options!, "PurgeNeedsPatterns"));
        Assert.Equal("/purge /confirm requires /largest with file patterns, e.g. /largest *.log", ArgParser.Parse(new[] { @"C:\", "/largest", "/purge", "/confirm" }).Error);
        var catalinaNoConfirm = ArgParser.Parse(new[] { @"C:\", "/largest", "catalina.out", "/purge" });
        Assert.Null(catalinaNoConfirm.Error); Assert.True(Property<bool>(catalinaNoConfirm.Options!, "PurgeNeedsPatterns"));
        Assert.Equal("/purge /confirm requires /largest with file patterns, e.g. /largest *.log", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/largest", "/purge", "/confirm" }).Error);
        var lastLargest = ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/largest", "/purge" });
        Assert.Null(lastLargest.Error); Assert.True(Property<bool>(lastLargest.Options!, "PurgeNeedsPatterns"));
        Assert.Null(ArgParser.Parse(new[] { @"C:\", "/largest", "/largest", "*.log", "/purge", "/confirm" }).Error);
        Assert.Equal("/purge /confirm requires /largest with file patterns, e.g. /largest *.log", ArgParser.Parse(new[] { @"C:\", "/largest", "catalina.out", "/purge", "/confirm" }).Error);
        foreach (var p in new[] { "*", "*.*", "**", "*?", "+*", "+*.*", "*.log;*" })
        {
            var matchAll = ArgParser.Parse(new[] { @"C:\", "/largest", p, "/purge" });
            Assert.Null(matchAll.Error); Assert.True(Property<bool>(matchAll.Options!, "PurgeNeedsPatterns"));
            Assert.Equal("/purge /confirm cannot be used with a match-all pattern; name the file types to delete, e.g. /largest *.log", ArgParser.Parse(new[] { @"C:\", "/largest", p, "/purge", "/confirm" }).Error);
        }
        var addPattern = ArgParser.Parse(new[] { @"C:\", "/largest", "+*.zip", "/purge" });
        Assert.Null(addPattern.Error); Assert.True(Property<bool>(addPattern.Options!, "PurgeNeedsPatterns"));
        Assert.Equal("/purge /confirm needs the full pattern list; '+' adds to the default list, e.g. write /largest *.log;*.zip", ArgParser.Parse(new[] { @"C:\", "/largest", "+*.zip", "/purge", "/confirm" }).Error);
        var logZip = ArgParser.Parse(new[] { @"C:\", "/largest", "*.log;*.zip", "/purge" });
        Assert.Null(logZip.Error); Assert.False(Property<bool>(logZip.Options!, "PurgeNeedsPatterns"));
        var logZipConfirm = ArgParser.Parse(new[] { @"C:\", "/largest", "*.log;*.zip", "/purge", "/confirm" });
        Assert.Null(logZipConfirm.Error); Assert.False(Property<bool>(logZipConfirm.Options!, "PurgeNeedsPatterns"));
        Assert.Equal("/purge requires a number of days from 1 to 3650", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "0" }).Error);
        Assert.Equal("/purge requires a number of days from 1 to 3650", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "3651" }).Error);
        Assert.Equal("put the number of days right after /purge (e.g. /purge 7)", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/confirm", "7" }).Error);
        Assert.Equal("/purge with /summary /csv needs /html, which lists the files", ArgParser.Parse(new[] { @"C:\", "/summary", "/csv", "/largest", "*.log", "/purge" }).Error);
        Assert.Equal("/output cannot be the purge log", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/confirm", "/output", PurgeLog.DefaultPath }).Error);
        Assert.Null(ArgParser.Parse(new[] { @"C:\", "/largest", "*.log;*.hprof", "/purge" }).Error);
        var sizes = new[] { ("100MB", 100L << 20), ("999mb", 999L << 20), ("2GB", 2L << 30), ("1KB", 1L << 10) };
        foreach (var (size, bytes) in sizes)
        {
            var parsed = ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/minsize", size });
            Assert.Null(parsed.Error); Assert.Equal(bytes, Property<long>(parsed.Options!, "PurgeMinBytes"));
        }
        foreach (var size in new[] { "100", "1.5GB", "0MB", "-1GB", "9999999999TB" })
            Assert.Equal("/minsize needs a size like 100MB or 2GB", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/minsize", size }).Error);
        Assert.Equal("/minsize needs a size like 100MB or 2GB", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/minsize" }).Error);
        Assert.Equal("/minsize needs a size like 100MB or 2GB", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/minsize", "/confirm" }).Error);
        Assert.Equal("/minsize requires /purge", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/minsize", "1GB" }).Error);
        Assert.Equal("/minsize requires /purge", ArgParser.Parse(new[] { @"C:\", "/minsize", "1GB" }).Error);
        Assert.Contains("  /purge [days]       Delete the files /largest lists that were last\n", ArgParser.UsageText);
        Assert.Contains("                      and are at least /minsize big (default: 1GB).\n", ArgParser.UsageText);
        Assert.Contains("  /minsize <size>     With /purge: smallest file to delete, e.g. 100MB,\n", ArgParser.UsageText);
        Assert.Contains("                      999MB, 2GB (default: 1GB). Text and HTML output\n", ArgParser.UsageText);
        Assert.Contains("  /confirm            With /purge: really delete. Deletes are permanent\n", ArgParser.UsageText);
        Assert.Contains("Purge examples:\n  dirused D:\\ /largest *.log /purge\n", ArgParser.UsageText);
        Assert.Contains("      Delete the listed .log files of 1GB or more not modified in the last day\n", ArgParser.UsageText);
        Assert.Contains("  dirused D:\\ /largest *.log /purge /minsize 100MB\n", ArgParser.UsageText);
    }

    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    [Theory]
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
    public void FileFollowedBySwitchIsRejected()
    {
        var slashSwitch = ArgParser.Parse(new[] { "/summary", "/file", "/html", "server-list.txt" });
        Assert.Contains("/file requires a file name", slashSwitch.Error);
        Assert.Contains("'/html'", slashSwitch.Error);
        var dashSwitch = ArgParser.Parse(new[] { "/file", "-json", "x.txt" });
        Assert.Contains("/file requires a file name", dashSwitch.Error);
        Assert.Contains("'-json'", dashSwitch.Error);

        var scratch = Path.Combine(Path.GetTempPath(), "dirused-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var file = Path.Combine(scratch, "servers.list");
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(file, "localhost" + Environment.NewLine);
        try
        {
            var fileArgument = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
            var valid = ArgParser.Parse(new[] { "/file", fileArgument, "/html" });
            Assert.Null(valid.Error);
            Assert.Equal(fileArgument, valid.Options!.File);
            Assert.True(valid.Options.Html);
            Assert.StartsWith("Cannot read target file '", ArgParser.Parse(new[] { "/file", @"\\nohost\share\list.txt" }).Error);
            Assert.StartsWith("Cannot read target file '", ArgParser.Parse(new[] { "/file", "//nohost/share/list.txt" }).Error);
            Assert.Equal("/file requires a path", ArgParser.Parse(new[] { "/file" }).Error);
        }
        finally { Directory.Delete(scratch, true); }
    }

    [Fact]
    public void DeleteListPathArgumentsRejectSwitchesAndEmptyValues()
    {
        Assert.Equal("/fromlist requires a path", ArgParser.Parse(new[] { "/fromlist", "/confirm", "/purge" }).Error);
        Assert.Equal("/savelist requires a path", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/savelist", "/exact" }).Error);
        Assert.Equal("/savelist requires a path", ArgParser.Parse(new[] { @"C:\", "/largest", "*.log", "/purge", "/savelist", "" }).Error);
        Assert.Equal("/output requires a path", ArgParser.Parse(new[] { "/fromlist", "f", "/purge", "/output", "/fromlist" }).Error);
        var positive = ArgParser.Parse(new[] { "/fromlist", "f", "/purge", "/output", "r.txt" });
        Assert.Null(positive.Error);
        Assert.Equal("r.txt", positive.Options!.Output);
    }

    [Fact]
    public void ParsesTopAndJson()
    {
        var options = Assert.IsType<Options>(ArgParser.Parse(new[] { ".", "/top", "5", "/json" }).Options);
        Assert.Equal(5, options.Top); Assert.Equal(OutputFormat.Json, options.Format);
    }

    [Fact]
    public void HtmlAndOutputSwitches()
    {
        var a = ArgParser.Parse(new[] { @"C:\", "/html" }).Options!;
        Assert.True(a.Html);
        Assert.Equal(ArgParser.DefaultHtmlPath, a.Output);
        Assert.Equal("r.html", ArgParser.Parse(new[] { @"C:\", "/html", "/output", "r.html" }).Options!.Output);
        Assert.Equal("/output requires a path", ArgParser.Parse(new[] { @"C:\", "/output" }).Error);
        Assert.Equal("/output requires a path", ArgParser.Parse(new[] { @"C:\", "/output", "" }).Error);
        Assert.Equal("/output requires a path", ArgParser.Parse(new[] { @"C:\", "/output", "/html" }).Error);
        Assert.Equal("/output requires a path", ArgParser.Parse(new[] { @"C:\", "/output", "-summary" }).Error);
        Assert.Equal("//srv/share/r.html", ArgParser.Parse(new[] { @"C:\", "/output", "//srv/share/r.html" }).Options!.Output);
        var json = ArgParser.Parse(new[] { @"C:\", "/json", "/output", "x.json" }).Options!;
        Assert.Equal(OutputFormat.Json, json.Format);
        Assert.False(json.Html);
        Assert.Equal("x.json", json.Output);
        var plain = ArgParser.Parse(new[] { @"C:\" }).Options!;
        Assert.False(plain.Html);
        Assert.Null(plain.Output);
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

    [Fact]
    public void M11_YesSwitchRequiresPurgeConfirm()
    {
        Assert.Equal("/yes needs /purge /confirm", ArgParser.Parse(new[] { "C:\\", "/yes" }).Error);
        Assert.Equal("/yes needs /purge /confirm", ArgParser.Parse(new[] { "C:\\", "/purge", "/largest", "*.log", "/yes" }).Error);
        Assert.Equal("/confirm requires /purge", ArgParser.Parse(new[] { "C:\\", "/confirm", "/yes" }).Error);
        Assert.Equal("/yes needs /purge /confirm", ArgParser.Parse(new[] { "/fromlist", "f", "/purge", "/yes" }).Error);

        var scanOk = ArgParser.Parse(new[] { "C:\\", "/largest", "*.log", "/purge", "/confirm", "/yes" });
        Assert.Null(scanOk.Error);
        Assert.True(scanOk.Options!.Yes);

        var fromListOk = ArgParser.Parse(new[] { "/fromlist", "f", "/purge", "/confirm", "/yes" });
        Assert.Null(fromListOk.Error);
        Assert.True(fromListOk.Options!.Yes);
    }

    [Fact]
    public void M10b0_ScheduleParserRulesAndUsage()
    {
        Assert.Equal("/schedule can be given only once", ArgParser.Parse(new[] { "/schedule", "22:30", "/schedule", "23:00" }).Error);
        Assert.Null(ArgParser.Parse(new[] { "/schedule", "/list" }).Error);
        Assert.Null(ArgParser.Parse(new[] { "/schedule", "/cancel", "x" }).Error);
        Assert.Equal("/schedule requires a time, e.g. /schedule 22:30 PT", ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule" }).Error);
        foreach (var token in new[] { "/html", "-x", "/bogus" })
            Assert.Equal("/schedule requires a time, e.g. /schedule 22:30 PT", ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", token }).Error);
        Assert.Equal("/schedule: time must be HH:MM (00:00-23:59), optionally followed by PT, PST, PDT or UTC", ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "9:00" }).Error);
        Assert.Equal("/schedule can only be combined with /fromlist, /purge, /confirm and /yes", ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "/html" }).Error);
        Assert.Equal("/yes needs /purge /confirm", ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "/yes" }).Error);
        var valid = ArgParser.Parse(new[] { "/fromlist", "l", "/purge", "/schedule", "22:30", "pst" });
        Assert.Equal("PST", valid.Options!.ScheduleZone);
        Assert.Contains("  /schedule <time>    With /fromlist /purge: preview a one-time run at HH:MM\n", ArgParser.UsageText);
        Assert.Contains("                      [PT|PST|PDT|UTC]; add /confirm to schedule it\n", ArgParser.UsageText);
        Assert.DoesNotContain("/schedule", ArgParser.ShortUsageText);
    }

    [Fact]
    public void M10b2_NewScheduleCommandsParseAndRejectInRowOrder()
    {
        var cases = new (string[] Args, string Error)[]
        {
            (new[] { "/runplan", "a", "/runplan", "b" }, "/runplan cannot be combined with other switches"),
            (new[] { "/runplan", "a", "/json" }, "/runplan cannot be combined with other switches"),
            (new[] { "/runplan" }, "/runplan requires a plan id"),
            (new[] { "/runplan", "/x" }, "/runplan requires a plan id"),
            (new[] { "/list" }, "/list and /cancel go with /schedule"),
            (new[] { "/cancel", "x" }, "/list and /cancel go with /schedule"),
            (new[] { "/trust" }, "/trust and /untrust go with /schedule"),
            (new[] { "/untrust", "a" }, "/trust and /untrust go with /schedule"),
            (new[] { "/schedule", "/list", "22:30" }, "/schedule /list cannot be combined with other switches"),
            (new[] { "/schedule", "/list", "/trust" }, "/schedule /list cannot be combined with other switches"),
            (new[] { "/schedule", "/cancel" }, "/schedule /cancel requires a plan id"),
            (new[] { "/schedule", "/cancel", "-v" }, "/schedule /cancel requires a plan id"),
            (new[] { "/schedule", "/cancel", "x", "/json" }, "/schedule /cancel cannot be combined with other switches"),
            (new[] { "/schedule", "/trust", "a", "b" }, "/schedule /trust cannot be combined with other switches"),
            (new[] { "/schedule", "/trust", "/json" }, "/schedule /trust cannot be combined with other switches"),
            (new[] { "/schedule", "/untrust" }, "/schedule /untrust requires an account"),
            (new[] { "/schedule", "/untrust", "a", "/list" }, "/schedule /list cannot be combined with other switches"),
            (new[] { "/schedule", "/untrust", "a", "/json" }, "/schedule /untrust cannot be combined with other switches")
        };
        foreach (var (args, error) in cases) Assert.Equal(error, ArgParser.Parse(args).Error);

        var accepted = new (string[] Args, Options Expected)[]
        {
            (new[] { "/runplan", "20261123-2230-a1b2" }, new Options(Array.Empty<string>(), RunPlan: "20261123-2230-a1b2")),
            (new[] { "/schedule", "/list" }, new Options(Array.Empty<string>(), ScheduleList: true)),
            (new[] { "/list", "/schedule" }, new Options(Array.Empty<string>(), ScheduleList: true)),
            (new[] { "/schedule", "/cancel", "20261123-2230-a1b2" }, new Options(Array.Empty<string>(), ScheduleCancelId: "20261123-2230-a1b2")),
            (new[] { "/schedule", "/trust" }, new Options(Array.Empty<string>(), ScheduleTrust: true)),
            (new[] { "/schedule", "/trust", @"PROD\Domain Admins" }, new Options(Array.Empty<string>(), ScheduleTrust: true, ScheduleTrustAccount: @"PROD\Domain Admins")),
            (new[] { "/schedule", "/untrust", "S-1-5-21-1-2-3-1104" }, new Options(Array.Empty<string>(), ScheduleUntrust: "S-1-5-21-1-2-3-1104"))
        };
        foreach (var (args, expected) in accepted)
        {
            var parsed = ArgParser.Parse(args);
            Assert.Null(parsed.Error);
            var actual = Assert.IsType<Options>(parsed.Options);
            int setFormCount = (actual.ScheduleList ? 1 : 0) + (actual.ScheduleCancelId != null ? 1 : 0) +
                (actual.RunPlan != null ? 1 : 0) + (actual.ScheduleTrust ? 1 : 0) + (actual.ScheduleUntrust != null ? 1 : 0);
            Assert.Equal(1, setFormCount);
            Assert.Equal(expected with { Targets = actual.Targets }, actual);
        }
        Assert.Null(ArgParser.Parse(new[] { "/schedule", "/trust" }).Options!.ScheduleTrustAccount);
        Assert.Equal(@"PROD\Domain Admins", ArgParser.Parse(new[] { "/schedule", "/trust", @"PROD\Domain Admins" }).Options!.ScheduleTrustAccount);

        Assert.Contains("  /schedule /list     Show scheduled purges\n", ArgParser.UsageText);
        Assert.Contains("  /schedule /cancel <id>  Cancel a scheduled purge\n", ArgParser.UsageText);
        Assert.Contains("  /schedule /trust [\"acct\"]  Trust a folder writer (or list them)\n", ArgParser.UsageText);
        Assert.Contains("  /schedule /untrust \"acct\"  Stop trusting one\n", ArgParser.UsageText);
        Assert.DoesNotContain("/runplan", ArgParser.UsageText);
        Assert.DoesNotContain("/runplan", ArgParser.ShortUsageText);
        var usageLines = ArgParser.UsageText.Split('\n');
        Assert.All(usageLines, line => Assert.True(line.Length <= 80, $"UsageText line exceeds 80 columns: '{line}' ({line.Length})"));
        int scheduleContinuation = Array.IndexOf(usageLines, "                      [PT|PST|PDT|UTC]; add /confirm to schedule it");
        Assert.True(scheduleContinuation >= 0);
        Assert.Equal(new[]
        {
            "  /schedule /list     Show scheduled purges",
            "  /schedule /cancel <id>  Cancel a scheduled purge",
            "  /schedule /trust [\"acct\"]  Trust a folder writer (or list them)",
            "  /schedule /untrust \"acct\"  Stop trusting one"
        }, usageLines.Skip(scheduleContinuation + 1).Take(4));
    }
}
