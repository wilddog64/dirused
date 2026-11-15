using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Dirused.Core.Native;
namespace Dirused.Core.Volumes;
public class VolumeProbeException : Win32Exception
{
    public VolumeProbeException(int error) : base(error) { }
}
public static class VolumeProbe
{
    public static Volume Probe(string root)
    {
        if (!Kernel32.GetDiskFreeSpaceEx(root, out _, out var total, out var free)) throw new VolumeProbeException(Marshal.GetLastWin32Error());
        if (!Kernel32.GetDiskFreeSpace(root, out var sectors, out var bytes, out _, out _)) throw new VolumeProbeException(Marshal.GetLastWin32Error());
        var fs = new StringBuilder(256);
        if (!Kernel32.GetVolumeInformation(root, null, 0, out var serial, out _, out _, fs, (uint)fs.Capacity)) throw new VolumeProbeException(Marshal.GetLastWin32Error());
        if (total > long.MaxValue || free > total) throw new IOException($"Volume size out of range for {root}: total {total}, free {free}");
        long t = (long)total, f = (long)free;
        return new Volume(root, t - f, f, t, t == 0 ? 0 : 100.0 * (t - f) / t) { ClusterSize = (long)sectors * bytes, SerialNumber = serial, FileSystem = fs.ToString() };
    }
}
