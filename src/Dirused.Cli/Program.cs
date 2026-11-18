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
    private static int RunCore(string[] args, TextWriter output, TextWriter error, INodeShares? nodeShares, IReleaseSource? source, string version, string? updateStatePath = null)
    {
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
            var (check, checker) = StartUpdateCheck(args, error, source, version, updateStatePath);
            error.WriteLine(ArgParser.ShortUsageText);
            checker?.WaitAfterReport(check, TimeSpan.FromSeconds(1));
            return 1;
        }
        var parsed = ArgParser.Parse(args);
        if (parsed.Usage)
        {
            var (check, checker) = StartUpdateCheck(args, error, source, version, updateStatePath);
            output.WriteLine(ArgParser.UsageText);
            checker?.WaitAfterReport(check, TimeSpan.FromSeconds(1));
            return 0;
        }
        if (parsed.Error != null) { error.WriteLine(parsed.Error); return 1; }
        var options = parsed.Options!;
        StreamWriter? fileWriter = null;
        string? reportPath = null;
        string fullPath = "";
        if (options.Output != null && !ReportOutput.TryOpen(options.Output, out fileWriter, out fullPath, out var openError))
        {
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
            var (updateCheck, updateChecker) = StartUpdateCheck(args, error, source, version, updateStatePath);
            var results = ScanRunner.Run(targets, scan, new RunOptions(options.Parallel, options.Timeout), cts.Token)
                .Select(x => options.Format == OutputFormat.Text
                    ? x.Result
                    : x.Result with { Directories = x.Result.Directories.Take(options.Top).ToList(), Files = x.Result.Files.Take(options.Top).ToList() })
                .Select(x => x with { Level = Fullness.For(x, options.Warn, options.Crit) })
                .ToList();
            if (options.Summary && (options.Format != OutputFormat.Csv || options.Html))
                results = TopFolders.Attach(targets, results, (t, token) => RemoteScan.Scan(byTarget[t], 1, false, token, options.Largest == null ? null : new LargestCollector(options.LargestCount, options.Largest)), new RunOptions(options.Parallel, options.Timeout ?? TopFolders.DefaultTimeout), cts.Token).ToList();
            IRenderer renderer = options.Format switch
            {
                OutputFormat.Json => new JsonRenderer(options.Summary),
                OutputFormat.Csv => new CsvRenderer(options.Summary, options.Warn, options.Crit),
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
            if (updateCheck != null) updateChecker!.WaitAfterReport(updateCheck, TimeSpan.FromSeconds(1));
            if (results.Any(r => r.Level == FullnessLevel.Crit)) return 4;
            return results.Any(r => !r.Complete) ? 3 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { error.WriteLine(ex.Message); return 1; }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            fileWriter?.Dispose();
        }
    }

    private static (Task? Check, UpdateCheck? Checker) StartUpdateCheck(string[] args, TextWriter error, IReleaseSource? source, string version, string? updateStatePath)
    {
        if (source == null || args.Any(a => ArgParser.IsFlag(a, "noupdatecheck")) || GitHubReleaseSource.Parse(version) == null || Environment.GetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK") == "1") return (null, null);
        var checker = new UpdateCheck(source, error, updateStatePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dirused", "update-check.json"), version, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64");
        return (checker.CheckAsync(), checker);
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
