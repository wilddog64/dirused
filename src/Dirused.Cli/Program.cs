using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using Dirused.Core.Targets;
using Dirused.Common.Update;
using System.Reflection;
using System.Runtime.InteropServices;
namespace Dirused.Cli;
public static class Program
{
    public static int Main(string[] args)
    {
        var version = (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev").Split('+')[0];
        using var source = new GitHubReleaseSource(version);
        return RunCore(args, Console.Out, Console.Error, null, source, version);
    }
    public static int Run(string[] args, TextWriter output, TextWriter error) => Run(args, output, error, null);
    public static int Run(string[] args, TextWriter output, TextWriter error, INodeShares? nodeShares) => RunCore(args, output, error, nodeShares, null, "0.0.0-dev");
    public static int RunWithReleaseSource(string[] args, TextWriter output, TextWriter error, IReleaseSource source, string version, string? updateStatePath = null) => RunCore(args, output, error, null, source, version, updateStatePath);
    public static int RunWithPurge(string[] args, TextWriter output, TextWriter error, string purgeLogPath, IPurgeFileSystem? fs = null, IServiceQuery? services = null, Func<string, bool>? isFixedDrive = null, IServiceControl? control = null, TextReader? input = null, bool? isInputRedirected = null, Func<DateTime>? localNow = null, Action<TimeSpan>? sleep = null, Func<DateTime>? utcNow = null) => RunCore(args, output, error, null, null, "0.0.0-dev", null, purgeLogPath, fs, services, isFixedDrive, control, input, isInputRedirected, localNow, sleep, utcNow);
    private static int RunCore(string[] args, TextWriter output, TextWriter error, INodeShares? nodeShares, IReleaseSource? source, string version, string? updateStatePath = null, string? purgeLogPath = null, IPurgeFileSystem? purgeFs = null, IServiceQuery? services = null, Func<string, bool>? isFixedDrive = null, IServiceControl? serviceControl = null, TextReader? input = null, bool? isInputRedirected = null, Func<DateTime>? localNow = null, Action<TimeSpan>? sleep = null, Func<DateTime>? utcNow = null)
    {
        IServiceQuery? effectiveServices = services ?? (OperatingSystem.IsWindows() ? new RealServiceQuery() : null);
        Func<string, bool>? effectiveFixedDrive = isFixedDrive;
        IServiceControl? effectiveControl = serviceControl ?? (OperatingSystem.IsWindows() ? new RealServiceControl() : null);
        TextReader effectiveInput = input ?? Console.In;
        bool effectiveInputRedirected = isInputRedirected ?? Console.IsInputRedirected;
        Func<DateTime> effectiveLocalNow = localNow ?? (() => DateTime.Now);
        Action<TimeSpan> effectiveSleep = sleep ?? (t => Thread.Sleep(t));
        if (source != null) CleanupUpdateFiles();
        if (args.Length == 1 && ArgParser.IsFlag(args[0], "version")) { output.WriteLine($"dirused {version} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})"); return 0; }
        if (args.Any(a => ArgParser.IsFlag(a, "update")))
        {
            if (args.Length != 1 || !ArgParser.IsFlag(args[0], "update")) { error.WriteLine("/update must be used by itself."); return 1; }
            if (source == null) { error.WriteLine("/update is available in the packaged executable only."); return 1; }
            return SelfUpdater.RunAsync(source, version, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", Environment.ProcessPath ?? "", output, error).GetAwaiter().GetResult();
        }
        if (args.Any(a => ArgParser.IsFlag(a, "version")))
        {
            if (args.Length == 1) { output.WriteLine($"dirused {version} ({RuntimeInformation.RuntimeIdentifier})"); return 0; }
            error.WriteLine("/version must be used by itself."); return 1;
        }
        if (args.Length == 0)
        {
            var (check, checker) = StartUpdateCheck(args, error, source, version, updateStatePath, false);
            error.WriteLine(ArgParser.ShortUsageText);
            checker?.WaitAfterReport(check, TimeSpan.FromSeconds(1));
            return 1;
        }
        var parsed = ArgParser.Parse(args);
        if (parsed.Usage)
        {
            var (check, checker) = StartUpdateCheck(args, error, source, version, updateStatePath, true);
            output.WriteLine(ArgParser.UsageText);
            checker?.WaitAfterReport(check, TimeSpan.FromSeconds(1));
            return 0;
        }
        if (parsed.Error != null) { error.WriteLine(parsed.Error); return 1; }
        var options = parsed.Options!;
        if (Schedule.NotAvailableForm(options) is { } form) { error.WriteLine($"{form} is not available in this version"); return 1; }
        foreach (var path in new[] { options.SaveList, options.FromList, options.Output }.Where(p => p != null))
        {
            try { _ = Path.GetFullPath(path!); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { error.WriteLine($"invalid path: {path}"); return 1; }
        }
        string effectivePurgeLog = purgeLogPath ?? PurgeLog.DefaultPath;
        if (options.SaveList != null && ((options.Output != null && Path.GetFullPath(options.SaveList).Equals(Path.GetFullPath(options.Output), StringComparison.OrdinalIgnoreCase)) || Path.GetFullPath(options.SaveList).Equals(Path.GetFullPath(effectivePurgeLog), StringComparison.OrdinalIgnoreCase))) { error.WriteLine("/savelist cannot be the output or purge log"); return 1; }
        if (options.FromList != null)
        {
            if ((options.Output != null && Path.GetFullPath(options.FromList).Equals(Path.GetFullPath(options.Output), StringComparison.OrdinalIgnoreCase)) || Path.GetFullPath(options.FromList).Equals(Path.GetFullPath(effectivePurgeLog), StringComparison.OrdinalIgnoreCase)) { error.WriteLine("/fromlist cannot be the output or purge log"); return 1; }
            if (options.Confirm && options.Output != null && Path.GetFullPath(options.Output).Equals(Path.GetFullPath(effectivePurgeLog), StringComparison.OrdinalIgnoreCase)) { error.WriteLine("/output cannot be the purge log"); return 1; }
            if (!PurgeList.Read(options.FromList, out var parsedList)) { error.WriteLine($"not a dirused delete list: {options.FromList}"); return 1; }
            if (options.ScheduleTime != null) return Schedule.Run(options, parsedList!, options.FromList, output, error, () => DateTimeOffset.Now, TimeZoneInfo.Local);
            if (!OperatingSystem.IsWindows()) { error.WriteLine("/purge is available on Windows only"); return 1; }
            return RunFromList(options, parsedList!, output, error, purgeLogPath ?? PurgeLog.DefaultPath, purgeFs, effectiveServices, effectiveFixedDrive, effectiveControl, effectiveInput, effectiveInputRedirected, effectiveLocalNow, effectiveSleep, utcNow);
        }
        if (options.Purge && !OperatingSystem.IsWindows()) { error.WriteLine("/purge is available on Windows only"); return 1; }
        if (options.Purge && options.Confirm && options.Output != null && Path.GetFullPath(options.Output).Equals(Path.GetFullPath(effectivePurgeLog), StringComparison.OrdinalIgnoreCase)) { error.WriteLine("/output cannot be the purge log"); return 1; }
        StreamWriter? purgeLog = null;
        if (options.Purge && options.Confirm)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(effectivePurgeLog))!); purgeLog = new StreamWriter(new FileStream(effectivePurgeLog, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { error.WriteLine($"Cannot open purge log '{effectivePurgeLog}': {ex.Message}; nothing was scanned or deleted"); return 1; }
        }
        StreamWriter? fileWriter = null;
        string? reportPath = null;
        string fullPath = "";
        if (options.Output != null && !ReportOutput.TryOpen(options.Output, out fileWriter, out fullPath, out var openError))
        {
            purgeLog?.Dispose();
            error.WriteLine(openError);
            return 1;
        }
        if (fileWriter != null) reportPath = fullPath;
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var renderOutput = fileWriter != null && !options.Html ? fileWriter : output;
            var resolved = TargetResolver.ResolveDetailed(options.Targets, nodeShares);
            var targets = resolved.Select(x => x.Target).ToList();
            ArgParser.WarnIfMultiTarget(targets.Count, options, error);
            var byTarget = new Dictionary<Dirused.Common.Targets.Target, ResolvedTarget>(ReferenceEqualityComparer.Instance);
            foreach (var item in resolved) byTarget.TryAdd(item.Target, item);
            Func<Dirused.Common.Targets.Target, CancellationToken, ScanResult> scan = options.Summary
                ? (target, _) => RemoteScan.Scan(byTarget[target], ProbeSummary)
                : (target, token) => RemoteScan.Scan(byTarget[target], options.Deep, options.Exact, token, options.Largest == null ? null : new LargestCollector(options.LargestCount, options.Largest));
            var (updateCheck, updateChecker) = StartUpdateCheck(args, error, source, version, updateStatePath, false);
            var results = ScanRunner.Run(targets, scan, new RunOptions(options.Parallel, options.Timeout), cts.Token)
                .Select(x => options.Format == OutputFormat.Text
                    ? x.Result
                    : x.Result with { Directories = x.Result.Directories.Take(options.Top).ToList(), Files = x.Result.Files.Take(options.Top).ToList() })
                .Select(x => x with { Level = Fullness.For(x, options.Warn, options.Crit) })
                .ToList();
            if (options.Summary && (options.Format != OutputFormat.Csv || options.Html))
                results = TopFolders.Attach(targets, results, (t, token) => RemoteScan.Scan(byTarget[t], 1, false, token, options.Largest == null ? null : new LargestCollector(options.LargestCount, options.Largest)), new RunOptions(options.Parallel, options.Timeout ?? TopFolders.DefaultTimeout), cts.Token).ToList();
            PurgeRun? purgeRun = null;
            if (options.Purge)
            {
                purgeRun = Purge.Apply(results, options.PurgeDays, options.PurgeMinBytes, options.Confirm, purgeFs ?? new RealPurgeFileSystem(), utcNow ?? (() => DateTime.UtcNow), options.Confirm ? line => purgeLog!.WriteLine(line) : null, cts.Token, effectivePurgeLog, effectiveServices, effectiveFixedDrive, effectiveControl, effectiveLocalNow, effectiveSleep, effectiveInput, effectiveInputRedirected, options.Yes, error);
                results = purgeRun.Results.ToList();
            }
            IRenderer renderer = options.Format switch
            {
                OutputFormat.Json => new JsonRenderer(options.Summary),
                OutputFormat.Csv => new CsvRenderer(options.Summary, options.Warn, options.Crit, options.Purge),
                _ => options.Summary ? new SummaryRenderer(options.Warn, options.Crit, options.Format == OutputFormat.Text && ReferenceEquals(renderOutput, Console.Out)) : new TextRenderer(options.Top, options.Exact, warn: options.Warn, crit: options.Crit, color: options.Format == OutputFormat.Text && ReferenceEquals(renderOutput, Console.Out))
            };
            TextWriter destination = options.Html ? output : renderOutput;
            if (options.Format == OutputFormat.Text && !options.Summary)
            {
                foreach (var result in results)
                    if (!result.Complete && result.Volume.TotalBytes == 0 && result.Errors.Count == 1) destination.WriteLine(result.Errors[0].Reason);
                    else renderer.Render(destination, new[] { result });
            }
            else
            {
                renderer.Render(destination, results);
                if (options.Format == OutputFormat.Csv)
                    WriteCsvErrors(error, results);
            }
            if (options.Html)
            {
                new HtmlRenderer(options.Summary, options.Warn, options.Crit, options.Top).Render(fileWriter!, results);
                error.WriteLine($"HTML report: {reportPath}");
            }
            else if (fileWriter != null) error.WriteLine($"Report: {reportPath}");
            if (!options.Confirm && purgeRun?.ProbeFailures > 0)
            {
                error.WriteLine($"in-use check failed for {purgeRun.ProbeFailures} file(s): {purgeRun.FirstProbeError}");
            }
            if (purgeRun?.HeldSummary != null && purgeRun.HeldSummary.Total > 0)
            {
                var h = purgeRun.HeldSummary;
                if (h.TomcatCount > 0 && h.Service != null)
                {
                    (options.Format != OutputFormat.Text ? error : renderOutput).WriteLine($"held files: {h.Total} (Tomcat: {h.Service} on {h.Host}, root {h.InstallRoot}: {h.TomcatCount} file(s), {Format.Size(h.TomcatBytes)}; other: {h.OtherCount})");
                }
                else
                {
                    (options.Format != OutputFormat.Text ? error : renderOutput).WriteLine($"held files: {h.Total} (other: {h.OtherCount})");
                }
            }
            if (options.SaveList != null && purgeRun != null)
            {
                try { var summary=PurgeList.Summarize(results); PurgeList.Write(options.SaveList, results, options.PurgeDays, options.PurgeMinBytes, version, args); if(summary.NoIdFiles>0) error.WriteLine($"{summary.NoIdFiles} file(s) not saved: no file ID (server does not report one)"); error.WriteLine(summary.SavedFiles==0?"delete list saved: 0 file(s)":$"delete list saved: {summary.SavedFiles} file(s), {Format.Size(summary.SavedBytes)} -> {options.SaveList}"); }
                catch(Exception ex){error.WriteLine(ex.Message);return 1;}
            }
            if (purgeRun != null)
            {
                if (options.Confirm) error.WriteLine($"Purge: deleted {purgeRun.Deleted} file(s), {Format.Size(purgeRun.DeletedBytes)} freed; {purgeRun.Skipped} skipped; {purgeRun.Failed} failed; {purgeRun.NotAttempted} not attempted. Log: {effectivePurgeLog}");
                else
                {
                    string hint = options.PurgeNeedsPatterns
                        ? " To delete, rerun with /largest <patterns> /purge /confirm, e.g. /largest *.log"
                        : " Add /confirm to delete them.";
                    error.WriteLine($"Purge dry run: {purgeRun.WouldDelete} file(s), {Format.Size(purgeRun.WouldDeleteBytes)} would be deleted; {purgeRun.Skipped} skipped" + (purgeRun.Failed == 0 ? "" : $"; {purgeRun.Failed} could not be checked") + "." + hint);
                }
            }
            if (updateCheck != null) updateChecker!.WaitAfterReport(updateCheck, TimeSpan.FromSeconds(1));
            if (purgeRun?.ServiceStartFailed == true) return 3;
            if (results.Any(r => r.Level == FullnessLevel.Crit)) return 4;
            return results.Any(r => !r.Complete) || purgeRun is { Failed: > 0 } or { NotAttempted: > 0 } ? 3 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { error.WriteLine(ex.Message); return 1; }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            fileWriter?.Dispose();
            purgeLog?.Dispose();
        }
    }

    private static int RunFromList(Options options, ParsedPurgeList list, TextWriter output, TextWriter error, string logPath, IPurgeFileSystem? fs, IServiceQuery? services = null, Func<string, bool>? isFixedDrive = null, IServiceControl? control = null, TextReader? input = null, bool inputRedirected = false, Func<DateTime>? localNow = null, Action<TimeSpan>? sleep = null, Func<DateTime>? utcNow = null)
    {
        var results = PurgeList.ToResults(list);
        StreamWriter? log=null; StreamWriter? report=null; string reportPath="";
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try {
            if(options.Confirm){try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);log=new StreamWriter(new FileStream(logPath,FileMode.Append,FileAccess.Write,FileShare.Read)){AutoFlush=true}; } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or NotSupportedException) { error.WriteLine($"Cannot open purge log '{logPath}': {ex.Message}; nothing was deleted"); return 1; }}
            TextWriter destination=output;
            if(options.Output!=null){if(!ReportOutput.TryOpen(options.Output,out report,out reportPath,out var openError)){error.WriteLine(openError);return 1;} if(!options.Html)destination=report!;}
            var run=Purge.Apply(results,list.Days,list.MinBytes,options.Confirm,fs??new RealPurgeFileSystem(),utcNow??(()=>DateTime.UtcNow),options.Confirm?x=>log!.WriteLine(x):null,cts.Token,logPath,services,isFixedDrive,control,localNow,sleep,input,inputRedirected,options.Yes,error) with { ListErrors=list.Errors.Count, BadListLines=list.Errors };
            IRenderer renderer=options.Format switch {OutputFormat.Json=>new JsonRenderer(false),OutputFormat.Csv=>new CsvRenderer(false,85,90,true),_=>new TextRenderer()};
            renderer.Render(destination,run.Results);
            if(options.Html){new HtmlRenderer(false,85,90,50).Render(report!,run.Results);error.WriteLine($"HTML report: {reportPath}");} else if(report!=null)error.WriteLine($"Report: {reportPath}");
            if (!options.Confirm && run.ProbeFailures > 0)
            {
                error.WriteLine($"in-use check failed for {run.ProbeFailures} file(s): {run.FirstProbeError}");
            }
            if (run.HeldSummary != null && run.HeldSummary.Total > 0)
            {
                var h = run.HeldSummary;
                if (h.TomcatCount > 0 && h.Service != null)
                {
                    (options.Format != OutputFormat.Text ? error : destination).WriteLine($"held files: {h.Total} (Tomcat: {h.Service} on {h.Host}, root {h.InstallRoot}: {h.TomcatCount} file(s), {Format.Size(h.TomcatBytes)}; other: {h.OtherCount})");
                }
                else
                {
                    (options.Format != OutputFormat.Text ? error : destination).WriteLine($"held files: {h.Total} (other: {h.OtherCount})");
                }
            }
            if(run.ListErrors>0){error.WriteLine("Bad list lines:");foreach(var e in run.BadListLines)error.WriteLine($"  line {e.Line}: {e.Reason}");}
            error.WriteLine(options.Confirm?$"Purge: deleted {run.Deleted} file(s), {Format.Size(run.DeletedBytes)} freed; {run.Skipped} skipped; {run.Failed} failed; {run.NotAttempted} not attempted. Log: {logPath}":$"Purge dry run: {run.WouldDelete} file(s), {Format.Size(run.WouldDeleteBytes)} would be deleted; {run.Skipped} skipped" + (run.Failed == 0 ? "" : $"; {run.Failed} could not be checked") + ".");
            if (run.ServiceStartFailed) return 3;
            return run.ListErrors>0||run.Failed>0||run.NotAttempted>0?3:0;
        } finally { Console.CancelKeyPress -= cancelHandler; report?.Dispose();log?.Dispose(); }
    }

    private static (Task? Check, UpdateCheck? Checker) StartUpdateCheck(string[] args, TextWriter error, IReleaseSource? source, string version, string? updateStatePath, bool force)
    {
        if (source == null || args.Any(a => ArgParser.IsFlag(a, "noupdatecheck")) || GitHubReleaseSource.Parse(version) == null || Environment.GetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK") == "1") return (null, null);
        var checker = new UpdateCheck(source, error, updateStatePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dirused", "update-check.json"), version, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64");
        return (checker.CheckAsync(force), checker);
    }

    private static void CleanupUpdateFiles()
    {
        if (!Path.GetFileName(Environment.ProcessPath ?? "").Equals("dirused.exe", StringComparison.OrdinalIgnoreCase)) return;
        SelfUpdater.Cleanup(Environment.ProcessPath!);
    }

    private static void WriteCsvErrors(TextWriter error, IEnumerable<ScanResult> results)
    {
        foreach (var result in results.Where(r => !r.Complete))
            foreach (var item in result.Errors) error.WriteLine($"{item.Path}: {item.Reason}");
    }

    private static ScanResult ProbeSummary(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? throw new IOException("Target has no volume root.");
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        var volume = Dirused.Core.Volumes.VolumeProbe.Probe(root);
        return new ScanResult(full, volume, new List<Entry>(), new List<Entry>(), new List<ScanError>(), true, false);
    }
}
