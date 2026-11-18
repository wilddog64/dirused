using System.Runtime.Versioning;
using Dirused.Common;
using Dirused.Cli;
using Xunit;

namespace Dirused.Tests;

[SupportedOSPlatform("windows")]
public class ProgramPurgeTests
{
    [Fact]
    public void PurgeLogMustOpenBeforeScanning()
    {
        var dir = Scratch();
        try
        {
            var blocker = Path.Combine(dir, "blocker"); File.WriteAllText(blocker, "x");
            var file = Path.Combine(dir, "old.log"); File.WriteAllText(file, "old"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
            var fs = new CountingPurgeFileSystem(new RealPurgeFileSystem());
            using var output = new StringWriter(); using var error = new StringWriter();
            int code = Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, output, error, Path.Combine(blocker, "purge.log"), fs);
            Assert.Equal(1, code); Assert.Contains("nothing was scanned or deleted", error.ToString()); Assert.True(File.Exists(file));
            Assert.Empty(output.ToString()); Assert.Equal(0, fs.ResolveCalls); Assert.Equal(0, fs.OpenCalls);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PurgeDryRunKeepsFilesAndConfirmDeletesListedOldFile()
    {
        var dir = Scratch();
        try
        {
            var small = Path.Combine(dir, "a.log"); var big = Path.Combine(dir, "big.log"); var recent = Path.Combine(dir, "b.log"); var other = Path.Combine(dir, "c.txt");
            File.WriteAllBytes(small, new byte[3 * 1024]); File.WriteAllBytes(big, new byte[5 * 1024]); File.WriteAllBytes(recent, new byte[8 * 1024]); File.WriteAllBytes(other, new byte[4 * 1024]);
            File.SetLastWriteTimeUtc(small, DateTime.UtcNow.AddDays(-3)); File.SetLastWriteTimeUtc(big, DateTime.UtcNow.AddDays(-3)); File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-3));
            var log = Path.Combine(dir, "purge.log");
            using var baselineOutput = new StringWriter(); using var baselineError = new StringWriter();
            int dryBaseline = Program.Run(new[] { dir, "/largest", "*.log" }, baselineOutput, baselineError);
            using (var output = new StringWriter()) using (var error = new StringWriter())
            {
                int dryCode = Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/minsize", "1KB" }, output, error, log);
                Assert.Equal(dryBaseline, dryCode);
                Assert.Contains("[would delete]", output.ToString()); Assert.True(File.Exists(small)); Assert.True(File.Exists(big)); Assert.False(File.Exists(log));
                var dryRows = output.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(line => !line.StartsWith("Largest candidate files", StringComparison.Ordinal)).Skip(1)
                    .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)).ToArray();
                string bRow = dryRows.Single(line => line.EndsWith(Path.DirectorySeparatorChar + "b.log", StringComparison.Ordinal) || line.Contains(Path.DirectorySeparatorChar + "b.log [", StringComparison.Ordinal));
                string bigRow = dryRows.Single(line => line.Contains(Path.DirectorySeparatorChar + "big.log", StringComparison.Ordinal));
                string aRow = dryRows.Single(line => line.Contains(Path.DirectorySeparatorChar + "a.log", StringComparison.Ordinal));
                Assert.Contains("[skipped: modified within 1 day(s)]", bRow); Assert.Contains("[would delete]", bigRow); Assert.Contains("[would delete]", aRow);
            }
            using (var output = new StringWriter()) using (var error = new StringWriter())
            {
                using var confirmBaselineOutput = new StringWriter(); using var confirmBaselineError = new StringWriter();
                int confirmBaseline = Program.Run(new[] { dir, "/largest", "*.log", "/largestcount", "2" }, confirmBaselineOutput, confirmBaselineError);
                int confirmCode = Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/largestcount", "2", "/purge", "/minsize", "1KB", "/confirm" }, output, error, log);
                Assert.Equal(confirmBaseline, confirmCode);
                Assert.False(File.Exists(big)); Assert.True(File.Exists(small)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(other));
                Assert.Single(File.ReadAllLines(log)); Assert.Contains("deleted 1 file(s)", error.ToString());
                var logged = File.ReadAllLines(log)[0].Split('\t'); Assert.Equal(big, logged[3]); Assert.EndsWith("deleted", logged[^1]);
            }
            string firstLine = File.ReadLines(log).First();
            using (var output = new StringWriter()) using (var error = new StringWriter())
            {
                Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/largestcount", "2", "/purge", "/minsize", "1KB", "/confirm" }, output, error, log);
                Assert.True(File.ReadAllLines(log).Length >= 2); Assert.Equal(firstLine, File.ReadLines(log).First());
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DefaultMinimumSizeSkipsSmallOldFilesAndOverrideDeletes()
    {
        var dir = Scratch();
        try
        {
            var file = Path.Combine(dir, "old.log"); File.WriteAllBytes(file, new byte[3 * 1024]); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
            var log = Path.Combine(dir, "purge.log");
            using (var output = new StringWriter()) using (var error = new StringWriter())
            {
                Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/confirm" }, output, error, log);
                Assert.True(File.Exists(file)); Assert.Contains("[skipped: smaller than 1.0 GB]", output.ToString());
                Assert.Contains("Purge: deleted 0 file(s)", error.ToString());
            }
            using (var output = new StringWriter()) using (var error = new StringWriter())
            {
                Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, output, error, log);
                Assert.False(File.Exists(file)); Assert.Contains("deleted 1 file(s)", error.ToString());
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RootSharedOpenDoesNotBlockConfirmDelete()
    {
        var dir = Scratch(); string oldCwd = Environment.CurrentDirectory;
        try
        {
            var file = Path.Combine(dir, "big.log"); File.WriteAllBytes(file, new byte[3 * 1024]); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
            Environment.CurrentDirectory = dir;
            using var watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
            using var output = new StringWriter(); using var error = new StringWriter();
            Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, output, error, Path.Combine(dir, "purge.log"));
            Assert.False(File.Exists(file)); Assert.Contains("deleted 1 file(s)", error.ToString());
        }
        finally { Environment.CurrentDirectory = oldCwd; Directory.Delete(dir, true); }
    }

    [Fact]
    public void LockedFileIsReportedFailedAndPreserved()
    {
        var dir = Scratch();
        try
        {
            var file = Path.Combine(dir, "locked.log"); File.WriteAllBytes(file, new byte[3 * 1024]); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
            int baseline;
            using (var output = new StringWriter()) using (var error = new StringWriter()) baseline = Program.Run(new[] { dir, "/largest", "*.log" }, output, error);
            using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var purgeOutput = new StringWriter(); using var purgeError = new StringWriter();
            int code = Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, purgeOutput, purgeError, Path.Combine(dir, "purge.log"));
            Assert.Equal(baseline == 4 ? 4 : 3, code); Assert.Contains("[failed:", purgeOutput.ToString()); Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void HardLinkedFileIsSkipped()
    {
        var dir = Scratch();
        try
        {
            var file = Path.Combine(dir, "original.log"); var hardLink = Path.Combine(dir, "linked.log");
            File.WriteAllBytes(file, new byte[3 * 1024]); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
            if (!CreateHardLinkW(hardLink, file, IntPtr.Zero)) throw Xunit.Sdk.SkipException.ForSkip("CreateHardLinkW is unavailable on this filesystem");
            using var output = new StringWriter(); using var error = new StringWriter();
            Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, output, error, Path.Combine(dir, "purge.log"));
            Assert.Contains("[skipped: hard-linked]", output.ToString()); Assert.True(File.Exists(file)); Assert.True(File.Exists(hardLink));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SwappedSubfolderJunctionIsRejectedByFinalPathCheck()
    {
        var dir = Scratch(); var outside = Scratch();
        string inside = Path.Combine(dir, "inside"); string moved = Path.Combine(dir, "inside-scanned"); string junction = inside;
        var insideFile = Path.Combine(inside, "big.log"); var outsideFile = Path.Combine(outside, "big.log");
        try
        {
            Directory.CreateDirectory(inside);
            var content = new byte[3 * 1024]; File.WriteAllBytes(insideFile, content); File.WriteAllBytes(outsideFile, content);
            DateTime stamp = DateTime.UtcNow.AddDays(-3); File.SetLastWriteTimeUtc(insideFile, stamp); File.SetLastWriteTimeUtc(outsideFile, stamp);
            if (!TryCreateJunction(Path.Combine(dir, "junction-probe"), outside, out var reason)) throw Xunit.Sdk.SkipException.ForSkip(reason);
            Directory.Delete(Path.Combine(dir, "junction-probe"));
            var fs = new SwapSubfolderOnOpenFileSystem(new RealPurgeFileSystem(), inside, moved, junction, outside);
            using var output = new StringWriter(); using var error = new StringWriter();
            Program.RunWithPurge(new[] { dir, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, output, error, Path.Combine(dir, "purge.log"), fs);
            Assert.Contains("[skipped: outside target]", output.ToString()); Assert.True(File.Exists(outsideFile)); Assert.True(File.Exists(Path.Combine(moved, "big.log")));

            var control = Scratch();
            try
            {
                var controlDir = Path.Combine(control, "inside"); Directory.CreateDirectory(controlDir); var controlFile = Path.Combine(controlDir, "big.log");
                File.WriteAllBytes(controlFile, content); File.SetLastWriteTimeUtc(controlFile, stamp);
                using var controlOutput = new StringWriter(); using var controlError = new StringWriter();
                Program.RunWithPurge(new[] { control, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, controlOutput, controlError, Path.Combine(control, "purge.log"));
                Assert.False(File.Exists(controlFile)); Assert.Contains("deleted 1 file(s)", controlError.ToString());
            }
            finally { Directory.Delete(control, true); }
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(dir, true); Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void LocalhostAdminShareConfirmDeletesScratchFile()
    {
        var dir = Scratch();
        try
        {
            var root = Path.GetPathRoot(dir)!; char drive = char.ToLowerInvariant(root[0]);
            string unc = $@"\\localhost\{drive}$\{dir[root.Length..]}".Replace('/', '\\');
            if (!Directory.Exists(unc)) throw Xunit.Sdk.SkipException.ForSkip($"Admin share is unreachable: {unc}");
            var file = Path.Combine(dir, "big.log"); File.WriteAllBytes(file, new byte[3 * 1024]); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
            string log = Path.Combine(dir, "purge.log");
            using var output = new StringWriter(); using var error = new StringWriter();
            Program.RunWithPurge(new[] { unc, "/largest", "*.log", "/purge", "/minsize", "1KB", "/confirm" }, output, error, log);
            Assert.False(File.Exists(file)); Assert.Contains("deleted", File.ReadLines(log).Single());
        }
        finally { Directory.Delete(dir, true); }
    }

    private static bool TryCreateJunction(string junction, string target, out string reason)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)!; string stdout = process.StandardOutput.ReadToEnd(); string stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        reason = process.ExitCode == 0 ? "" : $"Could not create directory junction: {stderr} {stdout}";
        return process.ExitCode == 0;
    }

    private static string Scratch()
    {
        string path = Path.Combine(Path.GetTempPath(), "dirused-purge-" + Guid.NewGuid().ToString("N"));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(path); return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr securityAttributes);

    private sealed class CountingPurgeFileSystem(IPurgeFileSystem inner) : IPurgeFileSystem
    {
        public int ResolveCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public string? ResolveRoot(string path, out string? error) { ResolveCalls++; return inner.ResolveRoot(path, out error); }
        public PurgeOpen Open(string path) { OpenCalls++; return inner.Open(path); }
    }

    private sealed class SwapSubfolderOnOpenFileSystem(IPurgeFileSystem inner, string source, string moved, string junction, string outside) : IPurgeFileSystem
    {
        private bool swapped;
        public string? ResolveRoot(string path, out string? error) => inner.ResolveRoot(path, out error);
        public PurgeOpen Open(string path)
        {
            if (!swapped && path.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                swapped = true; Directory.Move(source, moved);
                if (!TryCreateJunction(junction, outside, out string reason)) throw new IOException(reason);
            }
            return inner.Open(path);
        }
    }
}
