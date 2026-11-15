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
        try
        {
            var resolved = TargetResolver.ResolveDetailed(options.Targets, nodeShares);
            var targets = resolved.Select(x => x.Target).ToList();
            var results = ScanRunner.Run(targets, (target, _) => RemoteScan.Scan(resolved.First(x => ReferenceEquals(x.Target, target))), new RunOptions(options.Parallel, options.Timeout), CancellationToken.None)
                .Select(x => x.Result with { Directories = x.Result.Directories.Take(options.Top).ToList(), Files = x.Result.Files.Take(options.Top).ToList() }).ToList();
            IRenderer renderer = options.Format switch { OutputFormat.Json => new JsonRenderer(), OutputFormat.Csv => new CsvRenderer(), _ => new TextRenderer() };
            if (options.Format == OutputFormat.Text)
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
    }
}
