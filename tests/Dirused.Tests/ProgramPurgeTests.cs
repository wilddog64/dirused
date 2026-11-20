using System.Runtime.Versioning;
using Dirused.Common;
using Dirused.Cli;
using Xunit;

namespace Dirused.Tests;

[SupportedOSPlatform("windows")]
public class ProgramPurgeTests
{
    [Fact]
    public void InvalidDeleteListAndOutputPathsAreReportedAsUsageErrors()
    {
        foreach (var args in new[]
        {
            new[] { @"C:\", "/largest", "*.log", "/purge", "/savelist", "bad\0path" },
            new[] { "/fromlist", "bad\0path", "/purge" },
            new[] { "/fromlist", "valid.txt", "/purge", "/output", "bad\0path" }
        })
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            int code = Program.RunWithPurge(args, output, error, Path.Combine(Path.GetTempPath(), "purge.log"));
            Assert.Equal(1, code);
            Assert.Contains("invalid path: bad\0path", error.ToString());
        }
    }

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
                string dryText = output.ToString();
                var dryRows = dryText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(line => !line.StartsWith("Largest candidate files", StringComparison.Ordinal)).Skip(1)
                    .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)).ToArray();
                string Row(string name)
                {
                    var matches = dryRows.Where(line => line.Contains(Path.DirectorySeparatorChar + name, StringComparison.Ordinal)).ToArray();
                    Assert.True(matches.Length == 1, $"expected one largest row for {name}, got {matches.Length}; report:\n{dryText}");
                    return matches[0];
                }
                string bRow = Row("b.log"); string bigRow = Row("big.log"); string aRow = Row("a.log");
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
                Assert.True(File.Exists(file)); Assert.Contains("1 smaller than 1.0 GB not shown", output.ToString()); Assert.Contains("no files of at least 1.0 GB", output.ToString());
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
            Assert.Contains("[skipped: changed since scan (id)]", output.ToString()); Assert.True(File.Exists(outsideFile)); Assert.True(File.Exists(Path.Combine(moved, "big.log")));

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

    [Fact]
    public void PurgeDryRunWorksWithoutPatternsAndConfirmRefusesMatchAll()
    {
        var dir = Scratch();
        try
        {
            var file = Path.Combine(dir, "big.log");
            using (var fs = File.Create(file)) { fs.SetLength(2L << 30); }
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
            var log = Path.Combine(dir, "purge.log");
            string beforeHash = TreeHash(dir);

            using (var output = new StringWriter()) using (var error = new StringWriter())
            {
                int dryCode = Program.RunWithPurge(new[] { dir, "/largest", "/purge" }, output, error, log);
                Assert.Equal(0, dryCode);
                Assert.Contains("[would delete]", output.ToString());
                Assert.Contains("To delete, rerun with /largest <patterns> /purge /confirm, e.g. /largest *.log", error.ToString());
                Assert.False(File.Exists(log));
                Assert.Equal(beforeHash, TreeHash(dir));
            }

            using (var output = new StringWriter()) using (var error = new StringWriter())
            {
                int confirmCode = Program.RunWithPurge(new[] { dir, "/largest", "*", "/purge", "/confirm" }, output, error, log);
                Assert.Equal(1, confirmCode);
                Assert.Contains("/purge /confirm cannot be used with a match-all pattern; name the file types to delete, e.g. /largest *.log", error.ToString());
                Assert.False(File.Exists(log));
                Assert.Equal(beforeHash, TreeHash(dir));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string TreeHash(string dir)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        foreach (var file in files)
        {
            writer.Write(Path.GetRelativePath(dir, file));
            var fi = new FileInfo(file);
            writer.Write(fi.Length);
            writer.Write(fi.LastWriteTimeUtc.Ticks);
            using var fs = File.OpenRead(file);
            byte[] buf = new byte[8192];
            int read = fs.Read(buf, 0, buf.Length);
            writer.Write(buf, 0, read);
        }
        return Convert.ToHexString(sha.ComputeHash(ms.ToArray()));
    }

    private static bool TryCreateJunction(string junction, string target, out string reason)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)!; string stdout = process.StandardOutput.ReadToEnd(); string stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        reason = process.ExitCode == 0 ? "" : $"Could not create directory junction: {stderr} {stdout}";
        return process.ExitCode == 0;
    }

    [Fact]
    public void SaveListThenFromListConfirmDeletesOnlyUnchangedListedFiles()
    {
        var dir=Scratch();
        try {
            var replaced=Path.Combine(dir,"replace.log");var keep=Path.Combine(dir,"keep.log");var appended=Path.Combine(dir,"later.log");
            File.WriteAllText(replaced,"old replacement content"+new string('x',2048));File.WriteAllText(keep,"keep content"+new string('x',2048));File.SetLastWriteTimeUtc(replaced,DateTime.UtcNow.AddDays(-5));File.SetLastWriteTimeUtc(keep,DateTime.UtcNow.AddDays(-5));
            var list=Path.Combine(dir,"delete.txt");var log=Path.Combine(dir,"purge.log");using(var o=new StringWriter())using(var e=new StringWriter()){int c=Program.RunWithPurge(new[]{dir,"/largest","*.log","/purge","/minsize","1KB","/savelist",list},o,e,log);Assert.Equal(0,c);Assert.Contains("delete list saved: 2 file(s)",e.ToString());}
            var savedRows=File.ReadAllLines(list).Skip(6).ToArray();Assert.Equal(2,savedRows.Length);foreach(var row in savedRows){var fields=row.Split('\t');Assert.Equal(7,fields.Length);Assert.Equal(8,fields[1].Length);Assert.Equal(16,fields[2].Length);Assert.True(uint.Parse(fields[1],System.Globalization.NumberStyles.HexNumber)>0);Assert.True(ulong.Parse(fields[2],System.Globalization.NumberStyles.HexNumber)>0);}
            File.Delete(replaced);File.WriteAllText(replaced,"new replacement content"+new string('x',2048));File.SetLastWriteTimeUtc(replaced,DateTime.UtcNow.AddDays(-5));File.WriteAllText(appended,"new outside list"+new string('x',2048));File.SetLastWriteTimeUtc(appended,DateTime.UtcNow.AddDays(-5));
            using(var o=new StringWriter())using(var e=new StringWriter()){int c=Program.RunWithPurge(new[]{"/fromlist",list,"/purge","/confirm"},o,e,log);Assert.Equal(0,c);Assert.Contains("skipped: file changed since list (id)",o.ToString());Assert.False(File.Exists(keep));Assert.True(File.Exists(replaced));Assert.True(File.Exists(appended));}
        } finally {Directory.Delete(dir,true);}
    }

    [Fact]
    public void RemovingListLineLeavesThatFileAndDeletesRemainingEntry()
    {
        var dir=Scratch();try{var removed=Path.Combine(dir,"removed.log");var kept=Path.Combine(dir,"kept.log");File.WriteAllText(removed,"remove"+new string('x',2048));File.WriteAllText(kept,"keep"+new string('x',2048));File.SetLastWriteTimeUtc(removed,DateTime.UtcNow.AddDays(-4));File.SetLastWriteTimeUtc(kept,DateTime.UtcNow.AddDays(-4));var list=Path.Combine(dir,"delete.txt");var log=Path.Combine(dir,"purge.log");using(var o=new StringWriter())using(var e=new StringWriter())Program.RunWithPurge(new[]{dir,"/largest","*.log","/purge","/minsize","1KB","/savelist",list},o,e,log);var lines=File.ReadAllLines(list);File.WriteAllLines(list,lines.Take(6).Concat(lines.Skip(6).Where(x=>!x.EndsWith("removed.log",StringComparison.OrdinalIgnoreCase))));using var output=new StringWriter();using var error=new StringWriter();int code=Program.RunWithPurge(new[]{"/fromlist",list,"/purge","/confirm"},output,error,log);Assert.Equal(0,code);Assert.True(File.Exists(removed));Assert.False(File.Exists(kept));}finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public void BadFromListRowsReportAndSetExitThreeWhileValidPeerRuns()
    {
        var dir=Scratch();try{var file=Path.Combine(dir,"valid.log");File.WriteAllText(file,"valid"+new string('x',2048));File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-4));var list=Path.Combine(dir,"delete.txt");var log=Path.Combine(dir,"purge.log");using(var o=new StringWriter())using(var e=new StringWriter())Program.RunWithPurge(new[]{dir,"/largest","*.log","/purge","/minsize","1KB","/savelist",list},o,e,log);File.AppendAllText(list,"bad line\r\n");using var output=new StringWriter();using var error=new StringWriter();int code=Program.RunWithPurge(new[]{"/fromlist",list,"/purge"},output,error,log);Assert.Equal(3,code);Assert.Contains("Bad list lines:",error.ToString());Assert.Contains("line 8:",error.ToString());Assert.Contains("would delete",output.ToString());Assert.True(File.Exists(file));}finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public void FromListDryRunStatsAnOpenFileWithoutShareDelete()
    {
        var dir=Scratch();try{var file=Path.Combine(dir,"held.log");File.WriteAllText(file,"held content"+new string('x',2048));File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-4));var list=Path.Combine(dir,"delete.txt");var log=Path.Combine(dir,"purge.log");using(var o=new StringWriter())using(var e=new StringWriter())Program.RunWithPurge(new[]{dir,"/largest","*.log","/purge","/minsize","1KB","/savelist",list},o,e,log);
            using(var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var o=new StringWriter())using(var e=new StringWriter()){int c=Program.RunWithPurge(new[]{"/fromlist",list,"/purge"},o,e,log);Assert.Equal(0,c);Assert.Contains("[skipped: in use (no service found)]",o.ToString());Assert.True(File.Exists(file));}
        }finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public void SaveAndFromListPathCollisionsAreRejectedBeforeWriting()
    {
        var dir=Scratch();try{var list=Path.Combine(dir,"delete.txt");File.WriteAllText(list,"sentinel");using(var o=new StringWriter())using(var e=new StringWriter()){Assert.Equal(1,Program.RunWithPurge(new[]{dir,"/largest","*.log","/purge","/output",list,"/savelist",list},o,e,Path.Combine(dir,"purge.log")));Assert.Equal("sentinel",File.ReadAllText(list));}var purgeLog=Path.Combine(dir,"purge.log");File.WriteAllText(purgeLog,"log sentinel");using(var o=new StringWriter())using(var e=new StringWriter()){Assert.Equal(1,Program.RunWithPurge(new[]{dir,"/largest","*.log","/purge","/savelist",purgeLog},o,e,purgeLog));Assert.Equal("log sentinel",File.ReadAllText(purgeLog));}using(var o=new StringWriter())using(var e=new StringWriter()){Assert.Equal(1,Program.RunWithPurge(new[]{"/fromlist",list,"/purge","/output",list},o,e,purgeLog));Assert.Equal("sentinel",File.ReadAllText(list));}using(var o=new StringWriter())using(var e=new StringWriter()){Assert.Equal(1,Program.RunWithPurge(new[]{"/fromlist",list,"/purge","/confirm"},o,e,list));Assert.Equal("sentinel",File.ReadAllText(list));}}finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public void InvalidFromListDoesNotCreateOrTruncatePurgeLogOrOutput()
    {
        var dir=Scratch();try{var list=Path.Combine(dir,"bad.txt");var log=Path.Combine(dir,"purge.log");var output=Path.Combine(dir,"report.txt");File.WriteAllText(list,"not a list"+new string('x',2048));File.WriteAllText(log,"log sentinel");File.WriteAllText(output,"output sentinel");using var o=new StringWriter();using var e=new StringWriter();int code=Program.RunWithPurge(new[]{"/fromlist",list,"/purge","/confirm","/output",output},o,e,log);Assert.Equal(1,code);Assert.Contains($"not a dirused delete list: {list}",e.ToString());Assert.Equal("log sentinel",File.ReadAllText(log));Assert.Equal("output sentinel",File.ReadAllText(output));}finally{Directory.Delete(dir,true);}
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
        public PurgeFileInfo? Stat(string path, out string? error, out bool notFound) => inner.Stat(path, out error, out notFound);
    }

    private sealed class SwapSubfolderOnOpenFileSystem(IPurgeFileSystem inner, string source, string moved, string junction, string outside) : IPurgeFileSystem
    {
        private bool swapped;
        public string? ResolveRoot(string path, out string? error) => inner.ResolveRoot(path, out error);
        public PurgeFileInfo? Stat(string path, out string? error, out bool notFound) => inner.Stat(path, out error, out notFound);
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
