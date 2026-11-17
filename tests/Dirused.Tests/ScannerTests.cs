using System.ComponentModel;
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
    public void Dispose()
    {
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
            dir.Delete();
        Directory.Delete(root, true);
    }
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
    public void Test1_DefaultTextListsTopLevelOnly()
    {
        string a = PathIn("a");
        string ax = Path.Combine(a, "x");
        string axy = Path.Combine(ax, "y");
        Directory.CreateDirectory(axy);
        File.WriteAllBytes(Path.Combine(axy, "big.bin"), new byte[1024 * 1024]);

        string b = PathIn("b");
        Directory.CreateDirectory(b);
        File.WriteAllBytes(Path.Combine(b, "small.txt"), new byte[100]);

        string rootTxt = PathIn("root.txt");
        File.WriteAllBytes(rootTxt, new byte[200]);

        var (code, output) = Run(root);
        Assert.Equal(0, code);

        Assert.Contains(a, output);
        Assert.Contains(b, output);
        Assert.Contains(rootTxt, output);
        Assert.DoesNotContain(ax, output);
        Assert.DoesNotContain(axy, output);
        Assert.DoesNotContain("big.bin", output);
        Assert.DoesNotContain("small.txt", output);

        int idxA = output.IndexOf(a, StringComparison.Ordinal);
        int idxB = output.IndexOf(b, StringComparison.Ordinal);
        int idxRootTxt = output.IndexOf(rootTxt, StringComparison.Ordinal);
        Assert.True(idxA < idxB, "a should appear before b");
        Assert.True(idxA < idxRootTxt, "a should appear before rootTxt");

        var lines = output.Split(Environment.NewLine);
        var aLine = lines.Single(l => l.StartsWith(a));
        Assert.Contains("1.0M", aLine);
    }

    [Fact]
    public void Test2_FastDefaultNoPerFileCallsVsExact()
    {
        string tree = PathIn("t2_tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "x", "y"));
        File.WriteAllBytes(Path.Combine(tree, "a", "x", "y", "big.bin"), new byte[1024 * 1024]);
        Directory.CreateDirectory(Path.Combine(tree, "b"));
        File.WriteAllBytes(Path.Combine(tree, "b", "small.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(tree, "root.txt"), new byte[200]);

        var scannerDefault = new Scanner();
        var resDefault = scannerDefault.Scan(tree);
        Assert.True(resDefault.Complete);
        Assert.Equal(0, scannerDefault.PerFileCalls);

        var scannerExact = new Scanner();
        var resExact = scannerExact.Scan(tree, exact: true);
        Assert.True(resExact.Complete);
        Assert.True(scannerExact.PerFileCalls > 0);
    }

    [Fact]
    public void Test3_Deep2ListsSubLevelAndEqualsDeepAlone()
    {
        string tree = PathIn("t3_tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "x", "y"));
        File.WriteAllBytes(Path.Combine(tree, "a", "x", "y", "big.bin"), new byte[1024 * 1024]);
        Directory.CreateDirectory(Path.Combine(tree, "b"));
        File.WriteAllBytes(Path.Combine(tree, "b", "small.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(tree, "root.txt"), new byte[200]);

        var (codeDeep2, outDeep2) = Run(tree, "/deep", "2");
        Assert.Equal(0, codeDeep2);
        string ax = Path.Combine(tree, "a", "x");
        string axy = Path.Combine(tree, "a", "x", "y");
        Assert.Contains(ax, outDeep2);
        Assert.DoesNotContain(axy, outDeep2);

        var (codeDeepAlone, outDeepAlone) = Run(tree, "/deep");
        Assert.Equal(0, codeDeepAlone);
        Assert.Equal(outDeep2, outDeepAlone);

        var (codeNoDeep, outNoDeep) = Run(tree);
        Assert.Equal(0, codeNoDeep);
        var (codeDeep1, outDeep1) = Run(tree, "/deep", "1");
        Assert.Equal(0, codeDeep1);
        Assert.Equal(outNoDeep, outDeep1);
    }

    [Fact]
    public void Test4_HardLinkExactVsDefaultCount()
    {
        string dir = PathIn("t4");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "orig.bin");
        File.WriteAllBytes(file, new byte[1024 * 1024]);
        string link = Path.Combine(dir, "link.bin");
        Assert.True(CreateHardLink(link, file, IntPtr.Zero));

        var resExact = new Scanner().Scan(dir, exact: true);
        Assert.True(resExact.Complete);
        long exactTotal = resExact.Files.Sum(f => f.LogicalBytes);
        Assert.Equal(1024 * 1024, exactTotal);

        var resDefault = new Scanner().Scan(dir, exact: false);
        Assert.True(resDefault.Complete);
        long defaultTotal = resDefault.Files.Sum(f => f.LogicalBytes);
        Assert.Equal(1024 * 1024, defaultTotal);
    }

    [Fact]
    public void Test_DefaultDedupAndNoPerFileCallsAndExactWithoutOpens()
    {
        string tree = PathIn("tree_dedup");
        string subA = Path.Combine(tree, "subA");
        string subB = Path.Combine(tree, "subB");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        string fileA = Path.Combine(subA, "orig.bin");
        File.WriteAllBytes(fileA, new byte[1024 * 1024]);
        string fileB = Path.Combine(subB, "link.bin");
        Assert.True(CreateHardLink(fileB, fileA, IntPtr.Zero));

        var scannerDefault = new Scanner();
        var resDefault = scannerDefault.Scan(tree, deep: 2, exact: false);
        Assert.True(resDefault.Complete);
        long totalLogical = resDefault.Files.Sum(f => f.LogicalBytes);
        Assert.Equal(1024 * 1024, totalLogical);
        var fileRows = resDefault.Files.Where(f => f.Path == fileA || f.Path == fileB).ToList();
        Assert.Equal(2, fileRows.Count);
        Assert.Single(fileRows, f => f.LogicalBytes > 0);
        Assert.Single(fileRows, f => f.LogicalBytes == 0);

        Assert.Equal(0, scannerDefault.PerFileCalls);

        var scannerExact = new Scanner();
        var resExact = scannerExact.Scan(tree, deep: 2, exact: true);
        Assert.True(resExact.Complete);
        Assert.Equal(1024 * 1024, resExact.Files.Sum(f => f.LogicalBytes));
        var exactFileRows = resExact.Files.Where(f => f.Path == fileA || f.Path == fileB).ToList();
        Assert.Equal(2, exactFileRows.Count);
        var nonZero = Assert.Single(exactFileRows, f => f.LogicalBytes > 0);
        Assert.True(nonZero.OnDiskBytes >= 1024 * 1024);
        Assert.Single(exactFileRows, f => f.LogicalBytes == 0);
        Assert.Equal(1, scannerExact.PerFileCalls);
    }

    [Fact]
    public void Test_LongDirectorySeveralListingBuffers()
    {
        string dir = PathIn("long_dir");
        Directory.CreateDirectory(dir);
        const int fileCount = 3000;
        long expectedTotal = 0;
        for (int i = 0; i < fileCount; i++)
        {
            string name = $"file_{i:D6}_{new string('x', 88)}";
            string filePath = Path.Combine(dir, name);
            byte[] bytes = new byte[(i % 10) + 1];
            File.WriteAllBytes(filePath, bytes);
            expectedTotal += bytes.Length;
        }

        var scanner = new Scanner();
        var res = scanner.Scan(dir, deep: 1);
        Assert.True(res.Complete);
        Assert.Equal(fileCount, res.Files.Count);
        Assert.Equal(expectedTotal, res.Files.Sum(f => f.LogicalBytes));
    }

    [Fact]
    public void Test_MissingFileIdIsNotDeduplicated()
    {
        string dir = PathIn("no_file_id");
        Directory.CreateDirectory(dir);
        var entries = new List<DirEntry>
        {
            new("a.bin", FileAttributes.Normal, 100, 0),
            new("b.bin", FileAttributes.Normal, 200, 0),
            new("c.bin", FileAttributes.Normal, 300, ulong.MaxValue),
            new("d.bin", FileAttributes.Normal, 400, ulong.MaxValue),
            new("e.bin", FileAttributes.Normal, 500, 42),
            new("f.bin", FileAttributes.Normal, 600, 42),
        };
        var scanner = new Scanner(_ => entries);
        var res = scanner.Scan(dir, deep: 1);
        Assert.True(res.Complete);
        Assert.Equal(100 + 200 + 300 + 400 + 500, res.Files.Sum(f => f.LogicalBytes));
        Assert.Equal(0, res.Files.Single(f => f.Path.EndsWith("f.bin")).LogicalBytes);
    }

    [Fact]
    public void Test_StoppedScanNamesFolderInProgress()
    {
        string a = PathIn("a");
        string deep = Path.Combine(a, "deep");
        string b = PathIn("b");
        using var cts = new CancellationTokenSource();
        var scanner = new Scanner(path =>
        {
            if (path == root) return new[] { new DirEntry("a", FileAttributes.Directory, 0, 0), new DirEntry("b", FileAttributes.Directory, 0, 0) };
            if (path == a) return new[] { new DirEntry("deep", FileAttributes.Directory, 0, 0), new DirEntry("later", FileAttributes.Directory, 0, 0) };
            Assert.Equal(deep, path);
            cts.Cancel();
            return new[] { new DirEntry("file", FileAttributes.Normal, 1, 0) };
        });

        var result = scanner.Scan(root, ct: cts.Token);
        var error = Assert.Single(result.Errors);
        Assert.Equal(deep, error.Path);
        Assert.Equal("not finished: scan stopped", error.Reason);
        Assert.DoesNotContain(result.Errors, e => e.Path == b);
        Assert.False(result.Complete);
    }

    [Fact]
    public void Test_StoppedScanInEmptyFolderNamesIt()
    {
        string a = PathIn("a");
        string b = PathIn("b");
        bool readB = false;
        using var cts = new CancellationTokenSource();
        var scanner = new Scanner(path =>
        {
            if (path == root) return new[] { new DirEntry("a", FileAttributes.Directory, 0, 0), new DirEntry("b", FileAttributes.Directory, 0, 0) };
            if (path == a)
            {
                cts.Cancel();
                return Array.Empty<DirEntry>();
            }
            if (path == b) readB = true;
            return Array.Empty<DirEntry>();
        });

        var result = scanner.Scan(root, ct: cts.Token);
        var error = Assert.Single(result.Errors);
        Assert.Equal(a, error.Path);
        Assert.Equal("not finished: scan stopped", error.Reason);
        Assert.False(result.Complete);
        Assert.False(readB);
    }

    [Fact]
    public void Test_StoppedScanInLastFolderNamesIt()
    {
        string a = PathIn("a");
        using var cts = new CancellationTokenSource();
        var scanner = new Scanner(path =>
        {
            if (path == root) return new[] { new DirEntry("a", FileAttributes.Directory, 0, 0) };
            Assert.Equal(a, path);
            cts.Cancel();
            return new[] { new DirEntry("file", FileAttributes.Normal, 1, 0) };
        });

        var result = scanner.Scan(root, ct: cts.Token);
        var error = Assert.Single(result.Errors);
        Assert.Equal(a, error.Path);
        Assert.Equal("not finished: scan stopped", error.Reason);
        Assert.False(result.Complete);
    }

    [Fact]
    public void Test_CancelledBeforeStartNamesNothingExtra()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scanner = new Scanner(_ => throw new InvalidOperationException("Reader must not run."));

        var result = scanner.Scan(root, ct: cts.Token);
        Assert.DoesNotContain(result.Errors, e => e.Reason == "not finished: scan stopped");
        Assert.False(result.Complete);
    }

    [Fact]
    public void Test_FallbackWhenListingRejected()
    {
        string tree = PathIn("fallback_tree");
        string sub = Path.Combine(tree, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "f.txt"), new byte[512]);

        var scannerFallback = new Scanner(path =>
        {
            if (path.EndsWith("sub", StringComparison.OrdinalIgnoreCase))
                throw new Win32Exception(87);
            return DirectoryReader.Read(path);
        });

        var res = scannerFallback.Scan(tree, deep: 2);
        Assert.True(res.Complete);
        Assert.Equal(1, scannerFallback.FallbackDirectories);
        Assert.Contains(res.Files, f => f.Path.EndsWith("f.txt") && f.LogicalBytes == 512);

        var scannerDenied = new Scanner(path =>
        {
            if (path.EndsWith("sub", StringComparison.OrdinalIgnoreCase))
                throw new Win32Exception(5);
            return DirectoryReader.Read(path);
        });

        var resDenied = scannerDenied.Scan(tree, deep: 2);
        Assert.False(resDenied.Complete);
        Assert.Equal(0, scannerDenied.FallbackDirectories);
        Assert.Single(resDenied.Errors);
    }

    [Fact]
    public void Test_SystemVolumeInformationDeniedIsNotCounted()
    {
        AssertSystemVolumeInformationDenied(new UnauthorizedAccessException("denied"));
    }

    [Fact]
    public void Test_SystemVolumeInformationWin32DeniedIsNotCounted()
    {
        AssertSystemVolumeInformationDenied(new Win32Exception(5));
    }

    private void AssertSystemVolumeInformationDenied(Exception denied)
    {
        string volumeRoot = Path.GetPathRoot(root)!;
        string systemPath = Path.Combine(volumeRoot, "System Volume Information");
        string dataPath = Path.Combine(volumeRoot, "data");
        var scanner = new Scanner(path =>
        {
            if (path == volumeRoot) return new[] { new DirEntry("System Volume Information", FileAttributes.Directory, 0, 0), new DirEntry("data", FileAttributes.Directory, 0, 0) };
            if (path == systemPath) throw denied;
            if (path == dataPath) return Array.Empty<DirEntry>();
            throw new InvalidOperationException($"Unexpected path: {path}");
        });

        var result = scanner.Scan(volumeRoot);
        Assert.True(result.Complete);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { systemPath }, result.SystemOnly);
        Assert.Contains(result.Directories, entry => entry.Path == dataPath);
        Assert.DoesNotContain(result.Directories, entry => entry.Path == systemPath);
    }

    [Fact]
    public void Test_SystemVolumeInformationAsTargetIsStillIncomplete()
    {
        string volumeRoot = Path.GetPathRoot(root)!;
        string systemPath = Path.Combine(volumeRoot, "System Volume Information");
        var scanner = new Scanner(path => path == systemPath
            ? throw new UnauthorizedAccessException("denied")
            : throw new InvalidOperationException($"Unexpected path: {path}"));

        var result = scanner.Scan(systemPath);

        Assert.False(result.Complete);
        Assert.Equal(systemPath, Assert.Single(result.Errors).Path);
        Assert.Empty(result.SystemOnly);
    }

    [Fact]
    public void Test_SystemVolumeInformationExtendedRootIsNotCounted()
    {
        string volumeRoot = Path.GetPathRoot(root)!;
        string systemPath = Path.Combine(volumeRoot, "System Volume Information");
        string extendedRoot = @"\\?\" + volumeRoot;
        var scanner = new Scanner(path =>
        {
            if (path == volumeRoot || path == extendedRoot)
                return new[] { new DirEntry("System Volume Information", FileAttributes.Directory, 0, 0) };
            if (path == systemPath) throw new UnauthorizedAccessException("denied");
            throw new InvalidOperationException($"Unexpected path: {path}");
        });

        var result = scanner.Scan(extendedRoot);

        Assert.True(result.Complete);
        Assert.Equal(new[] { systemPath }, result.SystemOnly);
        Assert.DoesNotContain(result.Directories, entry => entry.Path == systemPath);
    }

    [Fact]
    public void Test_SystemVolumeInformationIoErrorIsStillIncomplete()
    {
        string volumeRoot = Path.GetPathRoot(root)!;
        string systemPath = Path.Combine(volumeRoot, "System Volume Information");
        var scanner = new Scanner(path => path == volumeRoot
            ? new[] { new DirEntry("System Volume Information", FileAttributes.Directory, 0, 0) }
            : path == systemPath
                ? throw new IOException("disk")
                : throw new InvalidOperationException($"Unexpected path: {path}"));

        var result = scanner.Scan(volumeRoot);

        Assert.False(result.Complete);
        Assert.Equal(systemPath, Assert.Single(result.Errors).Path);
        Assert.Empty(result.SystemOnly);
    }

    [Fact]
    public void Test_OtherDeniedFolderIsStillIncomplete()
    {
        string volumeRoot = Path.GetPathRoot(root)!;
        string secretPath = Path.Combine(volumeRoot, "secret");
        var scanner = new Scanner(path => path == volumeRoot
            ? new[] { new DirEntry("secret", FileAttributes.Directory, 0, 0) }
            : throw new UnauthorizedAccessException("denied"));

        var result = scanner.Scan(volumeRoot);
        Assert.False(result.Complete);
        Assert.Equal(secretPath, Assert.Single(result.Errors).Path);
        Assert.Empty(result.SystemOnly);
    }

    [Fact]
    public void Test_NestedSystemVolumeInformationIsStillIncomplete()
    {
        string volumeRoot = Path.GetPathRoot(root)!;
        string nestedPath = Path.Combine(volumeRoot, "a", "System Volume Information");
        string aPath = Path.Combine(volumeRoot, "a");
        var scanner = new Scanner(path => path == volumeRoot
            ? new[] { new DirEntry("a", FileAttributes.Directory, 0, 0) }
            : path == aPath
                ? new[] { new DirEntry("System Volume Information", FileAttributes.Directory, 0, 0) }
                : throw new UnauthorizedAccessException("denied"));

        var result = scanner.Scan(volumeRoot);
        Assert.False(result.Complete);
        Assert.Equal(nestedPath, Assert.Single(result.Errors).Path);
        Assert.Empty(result.SystemOnly);
    }

    [Fact]
    public void Test5_JsonDefaultExactFalseAndDepth1AndCsvHeader()
    {
        string tree = PathIn("t5_tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "x"));
        File.WriteAllBytes(Path.Combine(tree, "a", "x", "nested.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(tree, "root.txt"), new byte[200]);

        var (codeJson, outJson) = Run(tree, "/json");
        Assert.Equal(0, codeJson);
        using var doc = JsonDocument.Parse(outJson);
        var targetElem = doc.RootElement.GetProperty("targets")[0];
        Assert.False(targetElem.GetProperty("exact").GetBoolean());

        var dirs = targetElem.GetProperty("directories").EnumerateArray().ToList();
        var files = targetElem.GetProperty("files").EnumerateArray().ToList();
        string expectedDir = Path.Combine(tree, "a");
        string expectedFile = Path.Combine(tree, "root.txt");
        Assert.Single(dirs);
        Assert.Equal(expectedDir, dirs[0].GetProperty("path").GetString());
        Assert.Single(files);
        Assert.Equal(expectedFile, files[0].GetProperty("path").GetString());

        var (codeCsv, outCsv) = Run(tree, "/csv");
        Assert.Equal(0, codeCsv);
        Assert.StartsWith("kind,target,path,logical_bytes,on_disk_bytes", outCsv);
    }

    [Fact]
    public void JunctionAndHardLinkDoNotDoubleCount()
    {
        string a = PathIn("a"); Directory.CreateDirectory(a); string file = Path.Combine(a, "one"); File.WriteAllBytes(file, new byte[1024 * 1024]);
        Assert.True(CreateHardLink(PathIn("link"), file, IntPtr.Zero));
        Command("cmd.exe", $"/c mklink /J \"{PathIn("junction")}\" \"{a}\"");
        var result = new Scanner().Scan(root);
        Assert.True(result.Complete); Assert.Equal(1024 * 1024, result.Directories.Single(x => x.Path == a).OnDiskBytes);
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
        var result = new Scanner().Scan(root, deep: 8, exact: true);
        Assert.True(result.Complete); var entry = result.Files.Single(x => x.Path == sparse); Assert.True(entry.OnDiskBytes < entry.LogicalBytes);
        Assert.Contains(result.Files, x => x.Path == longFile && x.LogicalBytes == 5);
    }

    [Fact]
    public void OutputFormatsAndReadOnlySwitches()
    {
        File.WriteAllBytes(PathIn("large"), new byte[8192]); File.WriteAllBytes(PathIn("small"), new byte[1]);
        var before = Directory.GetFiles(root).ToDictionary(x => x, x => (new FileInfo(x).Length, File.GetLastWriteTimeUtc(x)));
        var text = Run(root, "/top", "1"); Assert.Equal(0, text.code); Assert.Contains("large", text.output); Assert.DoesNotContain("small", text.output);
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
