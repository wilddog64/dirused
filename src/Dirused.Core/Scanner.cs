using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dirused.Core;

public sealed class Scanner
{
    private readonly HashSet<(uint, ulong)> seen = new();
    private readonly List<Entry> directories = new();
    private readonly List<Entry> files = new();
    private readonly List<ScanError> errors = new();
    private long clusterSize;

    public long PerFileCalls { get; private set; }

    public ScanResult Scan(string target, CancellationToken ct) => Scan(target, 1, false, ct);

    public ScanResult Scan(string target, int deep = 1, bool exact = false, CancellationToken ct = default)
    {
        seen.Clear(); directories.Clear(); files.Clear(); errors.Clear();
        PerFileCalls = 0;
        string full = Path.GetFullPath(target);
        string root = Path.GetPathRoot(full) ?? throw new IOException("Target has no volume root.");
        var volume = Volumes.VolumeProbe.Probe(root);
        clusterSize = volume.ClusterSize;
        if (clusterSize <= 0) throw new IOException("Invalid volume cluster size.");
        Walk(full, 0, deep, exact, ct);
        return new ScanResult(
            full,
            volume,
            directories.OrderByDescending(x => x.OnDiskBytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            files.OrderByDescending(x => x.OnDiskBytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            errors,
            errors.Count == 0 && !ct.IsCancellationRequested,
            exact);
    }

    private (long logical, long disk) Walk(string path, int depth, int maxDepth, bool exact, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return (0, 0);
        long logical = 0, disk = 0;
        try
        {
            var info = new DirectoryInfo(Extended(path));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return (0, 0);
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                if (ct.IsCancellationRequested) break;
                string display = Unextended(entry.FullName);
                try
                {
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var child = Walk(display, depth + 1, maxDepth, exact, ct);
                        logical += child.logical;
                        disk += child.disk;
                        if (depth + 1 <= maxDepth)
                        {
                            directories.Add(new Entry(display, child.logical, child.disk));
                        }
                    }
                    else
                    {
                        long length = ((FileInfo)entry).Length;
                        long countedLength;
                        long allocated;
                        if (exact)
                        {
                            bool placeholder = ((uint)attributes & (0x00400000u | 0x00040000u | 0x1000u)) != 0;
                            bool duplicate = !placeholder && !Remember(entry.FullName);
                            allocated = duplicate ? 0 : Allocated(entry.FullName);
                            countedLength = duplicate ? 0 : length;
                        }
                        else
                        {
                            countedLength = length;
                            allocated = RoundToCluster(length);
                        }
                        if (depth + 1 <= maxDepth)
                        {
                            files.Add(new Entry(display, countedLength, allocated));
                        }
                        logical += countedLength;
                        disk += allocated;
                    }
                }
                catch (Exception ex) when (IsScanError(ex)) { errors.Add(new ScanError(display, ex.Message)); }
            }
        }
        catch (Exception ex) when (IsScanError(ex)) { errors.Add(new ScanError(path, ex.Message)); }
        return (logical, disk);
    }

    private static bool IsScanError(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException;
    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    private static string Unextended(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    private long RoundToCluster(long size) => checked((size / clusterSize + (size % clusterSize == 0 ? 0 : 1)) * clusterSize);
    private long Allocated(string path)
    {
        PerFileCalls++;
        Marshal.SetLastPInvokeError(0);
        uint low = Native.Kernel32.GetCompressedFileSize(path, out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0) throw new Win32Exception(error);
        long size = checked((long)(((ulong)high << 32) | low));
        return RoundToCluster(size);
    }
    private bool Remember(string path)
    {
        PerFileCalls++;
        using SafeFileHandle handle = Native.Kernel32.CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Native.Kernel32.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return seen.Add((info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow));
    }
}
