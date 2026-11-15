using System.Globalization;
using System.Text.Json;
using Dirused.Core;

namespace Dirused.Cli;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var targets = new List<string>(); int top = 50; bool json = false, csv = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i]; string name = arg.TrimStart('/', '-').ToLowerInvariant();
            if (arg.Equals("help", StringComparison.OrdinalIgnoreCase) || arg == "-h" || ((arg.StartsWith('/') || arg.StartsWith("--")) && (name == "help" || name == "?"))) { Usage(output); return 0; }
            if (arg.StartsWith(@"\\", StringComparison.Ordinal)) { error.WriteLine("UNC targets arrive in M1b"); return 1; }
            if (arg.StartsWith('/') || arg.StartsWith("--") || arg.StartsWith('-'))
            {
                switch (name)
                {
                    case "all": targets.Add("/all"); break;
                    case "top": if (++i >= args.Length || !int.TryParse(args[i], out top) || top < 1) { error.WriteLine("/top requires a positive integer"); return 1; } break;
                    case "json": json = true; break;
                    case "csv": csv = true; break;
                    case "purge": case "plan": error.WriteLine("not available yet (M2)"); return 1;
                    case "monitor": case "threshold": error.WriteLine("not available yet (M3)"); return 1;
                    case "gui": error.WriteLine("not available yet (GUI)"); return 1;
                    case "file": case "summary": case "timeout": case "parallel": case "node": error.WriteLine("not available yet (M1b)"); return 1;
                    default: error.WriteLine($"Unknown switch: {arg}"); return 1;
                }
            }
            else if (arg == "." || (arg.Length >= 3 && char.IsAsciiLetter(arg[0]) && arg[1] == ':' && (arg[2] == '\\' || arg[2] == '/'))) targets.Add(arg);
            else { error.WriteLine($"Invalid target: {arg}"); return 1; }
        }
        if (targets.Count == 0 || (json && csv)) { Usage(error); return 1; }
        try
        {
            var expanded = targets.SelectMany(t => t == "/all" ? DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name) : new[] { t });
            var results = expanded.Select(t => new Scanner().Scan(t)).Select(r => r with { Directories = r.Directories.Take(top).ToList(), Files = r.Files.Take(top).ToList() }).ToList();
            if (json) output.WriteLine(JsonSerializer.Serialize(new { targets = results }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            else if (csv)
            {
                output.WriteLine("kind,target,path,logical_bytes,on_disk_bytes");
                foreach (var r in results) { foreach (var e in r.Directories) Row(output, "directory", r.Target, e); foreach (var e in r.Files) Row(output, "file", r.Target, e); }
            }
            else foreach (var r in results)
            {
                var v = r.Volume; output.WriteLine($"{v.Drive} used {Size(v.UsedBytes)}, free {Size(v.FreeBytes)}, total {Size(v.TotalBytes)}, {v.PercentUsed:F1}% used");
                output.WriteLine("Largest directories"); foreach (var e in r.Directories) Print(output, e);
                output.WriteLine("Largest files"); foreach (var e in r.Files) Print(output, e);
                if (!r.Complete) { output.WriteLine($"INCOMPLETE: {r.Errors.Count} directories could not be read"); foreach (var e in r.Errors.Take(10)) output.WriteLine($"{e.Path}: {e.Reason}"); }
            }
            return results.Any(r => !r.Complete) ? 3 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { error.WriteLine(ex.Message); return 1; }
    }
    private static void Usage(TextWriter w) => w.WriteLine("Usage: dirused <target>... [/top N] [/json|/csv]\nTargets: . | <drive>:\\path | <drive>:\\ | /all");
    private static string Size(long n) { string[] units = { "B", "KB", "MB", "GB", "TB", "PB" }; double value = n; int i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; } return $"{value:F1} {units[i]}"; }
    private static void Print(TextWriter w, Entry e) => w.WriteLine($"{e.Path}  logical {Size(e.LogicalBytes)}  on disk {Size(e.OnDiskBytes)}");
    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static void Row(TextWriter w, string kind, string target, Entry e) => w.WriteLine($"{kind},{Quote(target)},{Quote(e.Path)},{e.LogicalBytes.ToString(CultureInfo.InvariantCulture)},{e.OnDiskBytes.ToString(CultureInfo.InvariantCulture)}");
}
