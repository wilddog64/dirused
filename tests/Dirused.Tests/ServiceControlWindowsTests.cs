using System.Diagnostics;
using System.Runtime.Versioning;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests;

[SupportedOSPlatform("windows")]
public sealed class ServiceControlWindowsTests
{
    [Fact]
    public void RealServiceControl_ThrowawayServiceLifecycle()
    {
        string svcName = "DirusedTest_" + Guid.NewGuid().ToString("N")[..8];
        RunSc($"create {svcName} binPath= \"cmd.exe /c exit 0\"");
        try
        {
            var control = new RealServiceControl();
            using var handle = control.Open(".", svcName, out string? openErr);
            Assert.NotNull(handle);
            Assert.Null(openErr);

            var deps = handle.RunningDependents(out string? depsErr);
            Assert.Null(depsErr);
            Assert.NotNull(deps);
            Assert.Empty(deps);

            var (startOk, startErr) = handle.Start();
            Assert.False(startOk);
            Assert.Equal(1053, startErr);

            var (state, stateErr) = handle.QueryState();
            Assert.Null(stateErr);
            Assert.Equal(ServiceState.Stopped, state);

            var (stopOk, stopErr) = handle.Stop();
            Assert.False(stopOk);
            Assert.Equal(1062, stopErr);
        }
        finally
        {
            RunSc($"delete {svcName}");
        }
    }

    private static void RunSc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
    }
}
