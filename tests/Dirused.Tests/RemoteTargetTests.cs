using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Dirused.Cli;
using Dirused.Core;
using Dirused.Core.Targets;
using Xunit;
namespace Dirused.Tests;
public class RemoteTargetTests
{
    private sealed class Shares : INodeShares
    {
        public IEnumerable<NodeShare> Enumerate(string node) => new[] { new NodeShare("ADMIN$", 0x80000000), new NodeShare("IPC$", 0x80000003), new NodeShare("C$", 0x80000000), new NodeShare("data", 0), new NodeShare("D$", 0x80000000) };
    }
    private sealed class Denied : INodeShares { public IEnumerable<NodeShare> Enumerate(string node) => throw new Win32Exception(5); }
    [Fact] public void FiltersAndOrdersAdminDiskShares()
    {
        var result = TargetResolver.ResolveDetailed(new[] { @"\\node" }, new Shares());
        Assert.Equal(new[] { @"\\node\C$\", @"\\node\D$\" }, result.Select(x => x.Target.Display));
        Assert.Contains("ACCESS DENIED", TargetResolver.ResolveDetailed(new[] { @"\\node" }, new Denied())[0].Error);
        var invalid = Assert.Single(TargetResolver.ResolveDetailed(new[] { "C:relative" }));
        Assert.Equal("Invalid target: C:relative", invalid.Error);
    }
    [Fact] public void RealNodeEnumerationMatchesNetShare()
    {
        using var process = Process.Start(new ProcessStartInfo("net.exe", "share") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        var expected = Regex.Matches(output, @"(?m)^\s*([A-Za-z]\$)\s+")
            .Select(m => m.Groups[1].Value.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var actual = TargetResolver.ResolveDetailed(new[] { @"\\localhost" });
        Assert.NotEmpty(expected); Assert.All(actual, x => Assert.Null(x.Error));
        Assert.Equal(expected, actual.Select(x => x.Target.Root.Split('\\', StringSplitOptions.RemoveEmptyEntries).Last().ToUpperInvariant()));
    }
    [Fact] public void AccessDeniedIsReportedByProgram()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(3, Program.Run(new[] { @"\\node" }, output, error, new Denied()));
        Assert.Contains(@"ACCESS DENIED: \\node", output.ToString());
    }
    [Fact] public void UnreachableShareDoesNotStopOtherTargets()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { @"C:\Windows\Web" }, output, error));
        output.GetStringBuilder().Clear();
        Assert.Equal(3, Program.Run(new[] { @"C:\Windows\Web", @"\\nosuchhost-xyz\c$" }, output, error));
        Assert.Contains("Largest directories", output.ToString()); Assert.Contains(@"UNREACHABLE: \\nosuchhost-xyz\c$ (53)", output.ToString());
    }
    [Fact] public void LoopbackMatchesLocalAndCountsHardLinksOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-remote-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string junction = Path.Combine(root, "junction");
            string sub = Path.Combine(root, "sub"); Directory.CreateDirectory(sub);
            string file = Path.Combine(sub, "one"); File.WriteAllBytes(file, new byte[1024 * 1024]); Assert.True(CreateHardLink(Path.Combine(root, "two"), file, IntPtr.Zero));
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{sub}\"") { UseShellExecute = false, CreateNoWindow = true })!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
            string unc = @"\\localhost\" + char.ToLowerInvariant(root[0]) + "$" + root[2..];
            var local = new Scanner().Scan(root); var remote = new Scanner().Scan(unc);
            Assert.True(local.Complete); Assert.True(remote.Complete);
            Assert.Equal(1024 * 1024, local.Directories.Single(x => x.Path == root).LogicalBytes);
            Assert.Equal(local.Directories.Single(x => x.Path == root).LogicalBytes, remote.Directories.Single(x => x.Path == unc).LogicalBytes);
            Assert.Equal(local.Directories.Single(x => x.Path == root).OnDiskBytes, remote.Directories.Single(x => x.Path == unc).OnDiskBytes);
            Assert.Equal(local.Files.Sum(x => x.LogicalBytes), remote.Files.Sum(x => x.LogicalBytes));
            Assert.Equal(local.Files.Sum(x => x.OnDiskBytes), remote.Files.Sum(x => x.OnDiskBytes));
            Assert.Equal(RunTotals(root), RunTotals(unc));
        }
        finally { var junction = Path.Combine(root, "junction"); if (Directory.Exists(junction)) Directory.Delete(junction); Directory.Delete(root, true); }
    }
    [Fact] public void DriveSeparatorFormsHaveEqualTotals()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-forms-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "file"), new byte[1024 * 1024]);
            var expected = RunTotals(root);
            Assert.Equal(1024 * 1024, expected.logical);
            Assert.Equal(expected, RunTotals(root.Replace('\\', '/')));
            Assert.Equal(expected, RunTotals(char.ToLowerInvariant(root[0]) + root[1..]));
        }
        finally { Directory.Delete(root, true); }
    }
    private static (long logical, long disk) RunTotals(string path)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { path, "/json" }, output, error));
        using var json = JsonDocument.Parse(output.ToString());
        var entries = json.RootElement.GetProperty("targets")[0].GetProperty("directories");
        var root = entries.EnumerateArray().Single(x => x.GetProperty("path").GetString()!.Equals(path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        return (root.GetProperty("logicalBytes").GetInt64(), root.GetProperty("onDiskBytes").GetInt64());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
