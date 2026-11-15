using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dirused.Cli;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dirused-test-" + Guid.NewGuid().ToString("N"));
    public ScannerTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string PathIn(string name) => Path.Combine(root, name);
    private static void Command(string command, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
    }
    private (int code, string output) Run(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        int code = Program.Run(args, output, error); return (code, output.ToString());
    }
    [Fact]
    public void JunctionAndHardLinkDoNotDoubleCount()
    {
        string a = PathIn("a"); Directory.CreateDirectory(a); string file = Path.Combine(a, "one"); File.WriteAllBytes(file, new byte[1024 * 1024]);
        Assert.True(CreateHardLink(PathIn("link"), file, IntPtr.Zero));
        Command("cmd.exe", $"/c mklink /J \"{PathIn("junction")}\" \"{a}\"");
        var result = new Scanner().Scan(root);
        Assert.True(result.Complete); Assert.Equal(1024 * 1024, result.Directories.Single(x => x.Path == root).OnDiskBytes);
        Assert.DoesNotContain(result.Directories, x => x.Path.Contains("junction", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void SparseAndLongPathsAreReported()
    {
        string sparse = PathIn("sparse"); using (File.Create(sparse)) { }
        Command("fsutil.exe", $"sparse setflag \"{sparse}\"");
        using (var f = new FileStream(sparse, FileMode.Open, FileAccess.Write)) f.SetLength(64L * 1024 * 1024);
        string deep = root; for (int i = 0; i < 7; i++) { deep = Path.Combine(deep, new string((char)('a' + i), 45)); Directory.CreateDirectory(deep); }
        string longFile = Path.Combine(deep, "file"); File.WriteAllText(longFile, "hello");
        var result = new Scanner().Scan(root);
        Assert.True(result.Complete); var entry = result.Files.Single(x => x.Path == sparse); Assert.True(entry.OnDiskBytes < entry.LogicalBytes);
        Assert.Contains(result.Files, x => x.Path == longFile && x.LogicalBytes == 5);
    }
    [Fact]
    public void OutputFormatsAndReadOnlySwitches()
    {
        File.WriteAllBytes(PathIn("large"), new byte[8192]); File.WriteAllBytes(PathIn("small"), new byte[1]);
        var before = Directory.GetFiles(root).ToDictionary(x => x, x => (new FileInfo(x).Length, File.GetLastWriteTimeUtc(x)));
        var text = Run(root, "/top", "1"); Assert.Equal(0, text.code); Assert.Contains("Largest directories", text.output); Assert.Contains("large", text.output); Assert.DoesNotContain("small", text.output);
        var json = Run(root, "/json"); Assert.Equal(0, json.code); using var doc = JsonDocument.Parse(json.output); Assert.Equal(2, doc.RootElement.GetProperty("targets")[0].GetProperty("files").GetArrayLength());
        Assert.StartsWith("kind,target,path,logical_bytes,on_disk_bytes", Run(root, "/csv").output);
        foreach (string flag in new[] { "/purge", "/monitor", "/threshold", "/gui", "/file" }) Assert.Equal(1, Run(root, flag).code);
        var after = Directory.GetFiles(root).ToDictionary(x => x, x => (new FileInfo(x).Length, File.GetLastWriteTimeUtc(x)));
        Assert.Equal(before.OrderBy(x => x.Key), after.OrderBy(x => x.Key));
    }
    [Fact]
    public void UnreadableDirectoryIsIncomplete()
    {
        string denied = PathIn("denied"); Directory.CreateDirectory(denied);
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        Command("icacls.exe", $"\"{denied}\" /deny \"{user}:(OI)(CI)(RX)\"");
        try { var result = Run(root); Assert.Equal(3, result.code); Assert.Contains("INCOMPLETE", result.output); Assert.Contains(denied, result.output); }
        finally { Command("icacls.exe", $"\"{denied}\" /remove:d \"{user}\""); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
