using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using Dirused.Core.Targets;
namespace Dirused.Cli;
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);
    public static int Run(string[] args, TextWriter output, TextWriter error) => Run(args, output, error, null);
    public static int Run(string[] args, TextWriter output, TextWriter error, INodeShares? nodeShares)
    {
        var parsed = ArgParser.Parse(args);
        if (parsed.Usage) { output.WriteLine(ArgParser.UsageText); return 0; }
        if (parsed.Error != null) { error.WriteLine(parsed.Error); return 1; }
        var options = parsed.Options!;
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var resolved = TargetResolver.ResolveDetailed(options.Targets, nodeShares);
            var targets = resolved.Select(x => x.Target).ToList();
            ArgParser.WarnIfMultiTarget(targets.Count, options, error);
            var byTarget = new Dictionary<Dirused.Common.Targets.Target, ResolvedTarget>(ReferenceEqualityComparer.Instance);
            foreach (var item in resolved) byTarget.TryAdd(item.Target, item);
            Func<Dirused.Common.Targets.Target, CancellationToken, ScanResult> scan = options.Summary
                ? (target, _) => RemoteScan.Scan(byTarget[target], ProbeSummary)
                : (target, token) => RemoteScan.Scan(byTarget[target], options.Deep, options.Exact, token);
            var results = ScanRunner.Run(targets, scan, new RunOptions(options.Parallel, options.Timeout), cts.Token)
                .Select(x => options.Format == OutputFormat.Text
                    ? x.Result
                    : x.Result with { Directories = x.Result.Directories.Take(options.Top).ToList(), Files = x.Result.Files.Take(options.Top).ToList() })
                .ToList();
            IRenderer renderer = options.Format switch
            {
                OutputFormat.Json => new JsonRenderer(options.Summary),
                OutputFormat.Csv => new CsvRenderer(options.Summary),
                _ => options.Summary ? new SummaryRenderer() : new TextRenderer(options.Top, options.Exact)
            };
            if (options.Format == OutputFormat.Text && !options.Summary)
            {
                foreach (var result in results)
                    if (!result.Complete && result.Volume.TotalBytes == 0 && result.Errors.Count == 1) output.WriteLine(result.Errors[0].Reason);
                    else renderer.Render(output, new[] { result });
            }
            else
            {
                renderer.Render(output, results);
                if (options.Format == OutputFormat.Csv)
                    foreach (var result in results.Where(r => !r.Complete))
                        foreach (var item in result.Errors) error.WriteLine(item.Reason);
            }
            return results.Any(r => !r.Complete) ? 3 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { error.WriteLine(ex.Message); return 1; }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
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
