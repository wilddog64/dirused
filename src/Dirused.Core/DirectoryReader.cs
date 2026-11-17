using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("Dirused.Tests")]

namespace Dirused.Core;

public readonly record struct DirEntry(string Name, FileAttributes Attributes, long EndOfFile, ulong FileId);

public static class DirectoryReader
{
    private const int BufferSize = 64 * 1024;
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int ERROR_NO_MORE_FILES = 18;

    internal static string Extended(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] :
        @"\\?\" + path;

    public static IReadOnlyList<DirEntry> Read(string path)
    {
        using SafeFileHandle handle = Native.Kernel32.CreateFile(
            Extended(path),
            0x1,        // FILE_LIST_DIRECTORY
            7,          // share all: FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            IntPtr.Zero,
            3,          // OPEN_EXISTING
            0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            var entries = new List<DirEntry>();
            int infoClass = FileIdBothDirectoryRestartInfo;

            while (true)
            {
                if (!Native.Kernel32.GetFileInformationByHandleEx(handle, infoClass, buffer, BufferSize))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ERROR_NO_MORE_FILES)
                    {
                        break;
                    }
                    throw new Win32Exception(error);
                }

                infoClass = FileIdBothDirectoryInfo;

                IntPtr current = buffer;
                while (true)
                {
                    uint nextEntryOffset = (uint)Marshal.ReadInt32(current, 0);
                    long endOfFile = Marshal.ReadInt64(current, 40);
                    FileAttributes attributes = (FileAttributes)Marshal.ReadInt32(current, 56);
                    uint fileNameLength = (uint)Marshal.ReadInt32(current, 60);
                    ulong fileId = (ulong)Marshal.ReadInt64(current, 96);

                    string fileName = Marshal.PtrToStringUni(IntPtr.Add(current, 104), (int)(fileNameLength / 2))!;

                    if (fileName != "." && fileName != "..")
                    {
                        entries.Add(new DirEntry(fileName, attributes, endOfFile, fileId));
                    }

                    if (nextEntryOffset == 0)
                    {
                        break;
                    }

                    current = IntPtr.Add(current, (int)nextEntryOffset);
                }
            }

            return entries;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
