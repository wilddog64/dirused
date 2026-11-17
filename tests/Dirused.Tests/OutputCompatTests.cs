using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dirused.Cli;
using Dirused.Common.Renderers;
using Dirused.Core;
using Dirused.Core.Volumes;
using Xunit;
namespace Dirused.Tests;
public class OutputCompatTests
{
    [Fact]
    public void Test_CsvStderrNamesStoppedFolder()
    {
        var volume = new Volume("C:", 0, 0, 0, 0);
        var result = new ScanResult("C:\\share", volume, new List<Entry>(), new List<Entry>(), new List<ScanError>
        {
            new("C:\\share\\folder", "not finished: scan stopped")
        }, false);
        using var error = new StringWriter();
        var writeCsvErrors = typeof(Program).GetMethod("WriteCsvErrors", BindingFlags.NonPublic | BindingFlags.Static)!;

        writeCsvErrors.Invoke(null, new object[] { error, new[] { result } });

        Assert.Equal($"C:\\share\\folder: not finished: scan stopped{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public void FormatsMatchM1aForTreeWithLinks()
    {
        string root = Path.Combine(Path.GetTempPath(), "dirused-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sub = Path.Combine(root, "sub"); Directory.CreateDirectory(sub);
            string file = Path.Combine(sub, "file"); File.WriteAllBytes(file, new byte[4096]);
            Assert.True(CreateHardLink(Path.Combine(sub, "link"), file, IntPtr.Zero));
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "junction")}\" \"{sub}\"") { UseShellExecute = false, CreateNoWindow = true })!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
            var scan = new Scanner().Scan(root); Assert.True(scan.Complete);
            foreach (string flag in new[] { "", "/json", "/csv" })
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                Assert.Equal(0, Program.Run(flag == "" ? new[] { root } : new[] { root, flag }, output, error));
                using var expected = new StringWriter();
                if (flag == "/json") expected.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { targets = new[] { scan } }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true }));
                else if (flag == "/csv") { expected.WriteLine("kind,target,path,logical_bytes,on_disk_bytes"); foreach (var e in scan.Directories) Row(expected, "directory", root, e); foreach (var e in scan.Files) Row(expected, "file", root, e); }
                else { new TextRenderer().Render(expected, new[] { scan }); }
                // Free space can change between the two scans; compare volume-independent output.
                if (flag == "") Assert.Equal(expected.ToString().Substring(expected.ToString().IndexOf(Environment.NewLine, StringComparison.Ordinal)), output.ToString().Substring(output.ToString().IndexOf(Environment.NewLine, StringComparison.Ordinal)));
                else if (flag == "/json") { var a = System.Text.Json.Nodes.JsonNode.Parse(expected.ToString())!; var b = System.Text.Json.Nodes.JsonNode.Parse(output.ToString())!; b["targets"]![0]!["volume"] = a["targets"]![0]!["volume"]!.DeepClone(); Assert.Equal(a.ToJsonString(), b.ToJsonString()); }
                else Assert.Equal(expected.ToString(), output.ToString());
            }
        }
        finally { var junction = Path.Combine(root, "junction"); if (Directory.Exists(junction)) Directory.Delete(junction); Directory.Delete(root, true); }
    }
    [Fact] public void VolumeProbeSupportsUncAndReportsErrors()
    {
        var local = VolumeProbe.Probe(@"C:\"); var unc = VolumeProbe.Probe(@"\\localhost\c$\");
        Assert.Equal(local.TotalBytes, unc.TotalBytes); Assert.Equal(local.SerialNumber, unc.SerialNumber); Assert.Equal(local.ClusterSize, unc.ClusterSize);
        Assert.True(Math.Abs(local.FreeBytes - unc.FreeBytes) <= local.TotalBytes / 100);
        Assert.Contains(Assert.Throws<VolumeProbeException>(() => VolumeProbe.Probe(@"\\nosuchhost-xyz\c$\")).NativeErrorCode, new[] { 51, 53, 64, 67, 1231 });
    }
    private static void Row(TextWriter w, string kind, string target, Entry e) => w.WriteLine($"{kind},\"{target.Replace("\"", "\"\"")}\",\"{e.Path.Replace("\"", "\"\"")}\",{e.LogicalBytes},{e.OnDiskBytes}");
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
