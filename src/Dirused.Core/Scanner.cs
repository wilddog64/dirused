using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dirused.Core;

public record Entry(string Path, long LogicalBytes, long OnDiskBytes);
public record ScanError(string Path, string Reason);
public record Volume(string Drive, long UsedBytes, long FreeBytes, long TotalBytes, double PercentUsed);
public record ScanResult(string Target, Volume Volume, List<Entry> Directories, List<Entry> Files, List<ScanError> Errors, bool Complete);

public sealed class Scanner
{
    private readonly HashSet<(uint, ulong)> seen = new();
    private readonly List<Entry> directories = new();
    private readonly List<Entry> files = new();
    private readonly List<ScanError> errors = new();
    private long clusterSize;

    public ScanResult Scan(string target)
    {
        seen.Clear(); directories.Clear(); files.Clear(); errors.Clear();
        string full = Path.GetFullPath(target);
        string root = Path.GetPathRoot(full) ?? throw new IOException("Target has no volume root.");
        var drive = new DriveInfo(root);
        long total = drive.TotalSize, free = drive.TotalFreeSpace;
        var volume = new Volume(root, total - free, free, total, total == 0 ? 0 : 100.0 * (total - free) / total);
        if (!GetDiskFreeSpace(root, out uint sectors, out uint bytes, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        clusterSize = (long)sectors * bytes;
        if (clusterSize <= 0) throw new IOException("Invalid volume cluster size.");
        Walk(full);
        return new ScanResult(full, volume, directories.OrderByDescending(x => x.OnDiskBytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList(), files.OrderByDescending(x => x.OnDiskBytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList(), errors, errors.Count == 0);
    }

    private (long logical, long disk) Walk(string path)
    {
        long logical = 0, disk = 0;
        try
        {
            var info = new DirectoryInfo(Extended(path));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return (0, 0);
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                string display = Unextended(entry.FullName);
                try
                {
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var child = Walk(display); logical += child.logical; disk += child.disk;
                    }
                    else
                    {
                        long length = ((FileInfo)entry).Length;
                        bool placeholder = ((uint)attributes & (0x00400000u | 0x00040000u | 0x1000u)) != 0;
                        bool duplicate = !placeholder && !Remember(entry.FullName);
                        long allocated = duplicate ? 0 : Allocated(entry.FullName);
                        long countedLength = duplicate ? 0 : length;
                        files.Add(new Entry(display, countedLength, allocated));
                        logical += countedLength; disk += allocated;
                    }
                }
                catch (Exception ex) when (IsScanError(ex)) { errors.Add(new ScanError(display, ex.Message)); }
            }
        }
        catch (Exception ex) when (IsScanError(ex)) { errors.Add(new ScanError(path, ex.Message)); }
        directories.Add(new Entry(path, logical, disk));
        return (logical, disk);
    }

    private static bool IsScanError(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException;
    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    private static string Unextended(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    private long Allocated(string path)
    {
        Marshal.SetLastPInvokeError(0);
        uint low = GetCompressedFileSize(path, out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0) throw new Win32Exception(error);
        long size = checked((long)(((ulong)high << 32) | low));
        return checked((size / clusterSize + (size % clusterSize == 0 ? 0 : 1)) * clusterSize);
    }
    private bool Remember(string path)
    {
        using SafeFileHandle handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return seen.Add((info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectors, out uint bytes, out uint free, out uint total);
    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string path, out uint high);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
