using System.Diagnostics;
using System.Text.Json;
using Dirused.Cli;
using Xunit;

namespace Dirused.Tests;

public sealed class SummaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dirused-summary-" + Guid.NewGuid().ToString("N"));

    public SummaryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static void Command(string command, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static (int code, string output, string error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = Program.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void E2_SummarySkipsWalkOverDenyAclAndFormatsCsvAndJson()
    {
        string denied = Path.Combine(root, "denied");
        string sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(denied);
        Directory.CreateDirectory(sub);
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        Command("icacls.exe", $"\"{denied}\" /deny \"{user}:(OI)(CI)(RX)\"");
        try
        {
            var summary = Run(root, sub, "/summary", "/warn", "99", "/crit", "100");
            Assert.Equal(0, summary.code);
            Assert.DoesNotContain("INCOMPLETE", summary.output);
            // Same volume serial prints once.
            var lines = summary.output.TrimEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("1 volume(s): ", lines[1], StringComparison.Ordinal);

            // Positive control: without /summary, walking hits the deny-ACL directory and exits 3.
            var walk = Run(root);
            Assert.Equal(3, walk.code);
            Assert.Contains("INCOMPLETE", walk.output);

            // /csv header as documented.
            var csv = Run(root, "/summary", "/csv");
            Assert.Equal(0, csv.code);
            Assert.StartsWith("kind,target,used_bytes,free_bytes,total_bytes,percent_used,level", csv.output);

            // /json has summary: true and empty directories/files.
            var json = Run(root, "/summary", "/json", "/warn", "99", "/crit", "100");
            Assert.Equal(0, json.code);
            using var doc = JsonDocument.Parse(json.output);
            Assert.True(doc.RootElement.GetProperty("summary").GetBoolean());
            var target = doc.RootElement.GetProperty("targets")[0];
            Assert.Equal(0, target.GetProperty("directories").GetArrayLength());
            Assert.Equal(0, target.GetProperty("files").GetArrayLength());
        }
        finally
        {
            Command("icacls.exe", $"\"{denied}\" /remove:d \"{user}\"");
        }
    }
    [Fact]
    public void E3_SummaryListsTopFoldersOfCritVolumeOnly()
    {
        foreach (var (name, size) in new[] { ("a", 3 * 1024 * 1024), ("b", 2 * 1024 * 1024), ("c", 1024 * 1024), ("d", 512 * 1024) })
        {
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "data.bin"), new byte[size]);
        }
        var critical = Run(root, "/summary", "/warn", "1", "/crit", "2");
        Assert.Equal(4, critical.code);
        var lines = critical.output.TrimEnd().Split(Environment.NewLine);
        Assert.EndsWith(" [CRIT]", lines[0]);
        Assert.EndsWith("\\a", lines[1]);
        Assert.EndsWith("\\b", lines[2]);
        Assert.EndsWith("\\c", lines[3]);
        Assert.DoesNotContain(lines, line => line.EndsWith("\\d", StringComparison.Ordinal));
        Assert.StartsWith("1 volume(s): ", lines[^1], StringComparison.Ordinal);

        var ok = Run(root, "/summary", "/warn", "99", "/crit", "100");
        Assert.Equal(0, ok.code);
        Assert.Equal(2, ok.output.TrimEnd().Split(Environment.NewLine).Length);
    }

}
