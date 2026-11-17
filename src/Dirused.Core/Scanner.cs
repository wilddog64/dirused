using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dirused.Core;

public sealed class Scanner
{
    private readonly HashSet<ulong> seen = new();
    private readonly List<Entry> directories = new();
    private readonly List<Entry> files = new();
    private readonly List<ScanError> errors = new();
    private readonly Func<string, IReadOnlyList<DirEntry>> reader;
    private long clusterSize;

    public long PerFileCalls { get; private set; }
    public long FallbackDirectories { get; private set; }

    public Scanner() : this(DirectoryReader.Read)
    {
    }

    internal Scanner(Func<string, IReadOnlyList<DirEntry>> reader)
    {
        this.reader = reader;
    }

    public ScanResult Scan(string target, CancellationToken ct) => Scan(target, 1, false, ct);

    public ScanResult Scan(string target, int deep = 1, bool exact = false, CancellationToken ct = default)
    {
        seen.Clear(); directories.Clear(); files.Clear(); errors.Clear();
        PerFileCalls = 0;
        FallbackDirectories = 0;
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
            if (depth == 0)
            {
                var rootInfo = new DirectoryInfo(Extended(path));
                if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) return (0, 0);
            }

            IReadOnlyList<DirEntry>? entries = null;
            bool fallback = false;
            try
            {
                entries = reader(path);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 87 or 1 or 50)
            {
                fallback = true;
                FallbackDirectories++;
            }

            if (fallback)
            {
                var info = new DirectoryInfo(Extended(path));
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
                            long countedLength = length;
                            long allocated = exact ? Allocated(entry.FullName) : RoundToCluster(length);
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
            else
            {
                foreach (var entry in entries!)
                {
                    if (ct.IsCancellationRequested) break;
                    string childPath = Path.Combine(path, entry.Name);
                    try
                    {
                        var attributes = entry.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var child = Walk(childPath, depth + 1, maxDepth, exact, ct);
                            logical += child.logical;
                            disk += child.disk;
                            if (depth + 1 <= maxDepth)
                            {
                                directories.Add(new Entry(childPath, child.logical, child.disk));
                            }
                        }
                        else
                        {
                            long length = entry.EndOfFile;
                            bool placeholder = ((uint)attributes & (0x00400000u | 0x00040000u | 0x1000u)) != 0;
                            bool hasId = entry.FileId != 0 && entry.FileId != ulong.MaxValue;
                            bool duplicate = !placeholder && hasId && !seen.Add(entry.FileId);
                            long countedLength;
                            long allocated;
                            if (duplicate)
                            {
                                countedLength = 0;
                                allocated = 0;
                            }
                            else
                            {
                                countedLength = length;
                                allocated = exact ? Allocated(childPath) : RoundToCluster(length);
                            }

                            if (depth + 1 <= maxDepth)
                            {
                                files.Add(new Entry(childPath, countedLength, allocated));
                            }
                            logical += countedLength;
                            disk += allocated;
                        }
                    }
                    catch (Exception ex) when (IsScanError(ex)) { errors.Add(new ScanError(childPath, ex.Message)); }
                }
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
        uint low = Native.Kernel32.GetCompressedFileSize(Extended(path), out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0) throw new Win32Exception(error);
        long size = checked((long)(((ulong)high << 32) | low));
        return RoundToCluster(size);
    }
}
