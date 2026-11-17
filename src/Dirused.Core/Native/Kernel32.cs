using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Dirused.Core.Native;
internal static class Kernel32
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetDiskFreeSpace(string root, out uint sectors, out uint bytes, out uint free, out uint total);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetDiskFreeSpaceEx(string root, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetVolumeInformation(string root, System.Text.StringBuilder? name, uint nameSize, out uint serial, out uint maxComponent, out uint flags, System.Text.StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetCompressedFileSize(string path, out uint high);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr info, uint bufferSize);
}
