using System.Runtime.InteropServices;
namespace Dirused.Core.Native;
internal static class NetApi32
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShareInfo1 { public IntPtr Name; public uint Type; public IntPtr Remark; }
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetShareEnum(string server, int level, out IntPtr buffer, uint preferredLength, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);
    [DllImport("Netapi32.dll")]
    internal static extern int NetApiBufferFree(IntPtr buffer);
}
