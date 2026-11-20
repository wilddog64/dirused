using System.Runtime.Versioning;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests;

[SupportedOSPlatform("windows")]
public sealed class HeldProbeWindowsTests
{
    [Fact]
    public void W1_ProbeHeldAndRealOpenAgreeWhenHeldAndFreeAfterRelease()
    {
        string path = Path.GetTempFileName();
        try
        {
            var fs = new RealPurgeFileSystem();

            // Test 1: FileShare.ReadWrite
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                bool? probeHeld = fs.ProbeHeld(path, out string? probeErr);
                Assert.True(probeHeld);
                Assert.Null(probeErr);

                using var opened = fs.Open(path);
                Assert.Null(opened.Info);
                Assert.Equal(32, opened.ErrorCode);
            }

            // Both free after release
            {
                bool? probeHeld = fs.ProbeHeld(path, out string? probeErr);
                Assert.False(probeHeld);
                Assert.Null(probeErr);

                using var opened = fs.Open(path);
                Assert.NotNull(opened.Info);
                Assert.Equal(0, opened.ErrorCode);
            }

            // Test 2: FileShare.ReadWrite | FileShare.Delete
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                bool? probeHeld = fs.ProbeHeld(path, out string? probeErr);
                Assert.True(probeHeld);
                Assert.Null(probeErr);

                using var opened = fs.Open(path);
                Assert.Null(opened.Info);
                Assert.Equal(32, opened.ErrorCode);
            }

            // Both free after release
            {
                bool? probeHeld = fs.ProbeHeld(path, out string? probeErr);
                Assert.False(probeHeld);
                Assert.Null(probeErr);

                using var opened = fs.Open(path);
                Assert.NotNull(opened.Info);
                Assert.Equal(0, opened.ErrorCode);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void W2_RealServiceQueryListProcrunLocalSucceedsAndBadHostFails()
    {
        var query = new RealServiceQuery();

        var localList = query.ListProcrun(".", out string? localError);
        Assert.NotNull(localList);
        Assert.Null(localError);

        var badList = query.ListProcrun("invalid-machine-987654321", out string? badError);
        Assert.Null(badList);
        Assert.NotNull(badError);
    }

    [Fact]
    public void W3_GetDriveTypeFixedForSystemDrive()
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        if (!systemRoot.EndsWith('\\')) systemRoot += "\\";
        Assert.True(DriveHelper.DefaultIsFixedDrive(systemRoot));
    }
}
