using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using Dirused.Core.Targets;
namespace Dirused.Cli;
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = ArgParser.Parse(args);
        if (parsed.Usage) { output.WriteLine(ArgParser.UsageText); return 0; }
        if (parsed.Error != null) { error.WriteLine(parsed.Error); return 1; }
        var options = parsed.Options!;
        try
        {
            var targets = TargetResolver.Resolve(options.Targets, out var errors);
            if (errors.Count > 0) { error.WriteLine(errors[0]); return 1; }
            var results = ScanRunner.Run(targets, (target, _) => new Scanner().Scan(target.Path), new RunOptions(options.Parallel, options.Timeout), CancellationToken.None)
                .Select(x => x.Result with { Directories = x.Result.Directories.Take(options.Top).ToList(), Files = x.Result.Files.Take(options.Top).ToList() }).ToList();
            IRenderer renderer = options.Format switch { OutputFormat.Json => new JsonRenderer(), OutputFormat.Csv => new CsvRenderer(), _ => new TextRenderer() };
            renderer.Render(output, results);
            return results.Any(r => !r.Complete) ? 3 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { error.WriteLine(ex.Message); return 1; }
    }
}
