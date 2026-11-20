using System.ComponentModel;
using Dirused.Common;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class PurgeServiceTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void M1_RecentAndOpen32HeldTomcatLogs_StopBothDeletedStartVerified_Exit0AndPurgeLogsInOrder()
    {
        string recentPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        string oldPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stdout.2026-11-04.log";

        // Recent file has LastWriteUtc >= cutoff
        var fRecent = new LargestFile(recentPath, 100_000_000, Now) { VolumeSerial = 0x1111, FileId = 0x2222 };
        // Old file has LastWriteUtc < cutoff
        var fOld = new LargestFile(oldPath, 50_000_000, Old) { VolumeSerial = 0x3333, FileId = 0x4444 };

        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 2, fRecent.LogicalBytes + fOld.LogicalBytes, new List<LargestFile> { fRecent, fOld }, true)
        };

        var fs = new FakeFs();
        fs.HeldProbes[recentPath] = true;
        fs.HeldProbes[oldPath] = true;
        // On recent path, stat is needed for ClassifyHeldFile
        fs.Stats[recentPath] = new PurgeFileInfo(recentPath, 100_000_000, Now, false, false, 1) { VolumeSerial = 0x1111, FileIndex = 0x2222 };

        // For old path, initial Open returns 32
        fs.OpenCallbacks[oldPath] = new Queue<Func<PurgeOpen>>(new Func<PurgeOpen>[]
        {
            () => new PurgeOpen("sharing violation", notFound: false, errorCode: 32)
        });
        fs.Stats[oldPath] = new PurgeFileInfo(oldPath, 50_000_000, Old, false, false, 1) { VolumeSerial = 0x3333, FileIndex = 0x4444 };

        // After Stop, both files are larger than recorded and succeed deletion
        long largerRecent = 120_000_000;
        long largerOld = 60_000_000;

        fs.OpenAfterStop[recentPath] = () => new PurgeFileInfo(recentPath, largerRecent, Now, false, false, 1) { VolumeSerial = 0x1111, FileIndex = 0x2222 };
        fs.OpenAfterStop[oldPath] = () => new PurgeFileInfo(oldPath, largerOld, Old, false, false, 1) { VolumeSerial = 0x3333, FileIndex = 0x4444 };

        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var handle = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running
        };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        var logs = new List<string>();
        using var err = new StringWriter();
        DateTime fakeTime = Now;
        var run = Purge.Apply(
            new[] { r },
            days: 1,
            minBytes: 1024,
            confirm: true,
            fs: fs,
            utcNow: () => fakeTime,
            log: line => logs.Add(line),
            ct: CancellationToken.None,
            services: services,
            control: control,
            localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
            sleep: ts => fakeTime += ts,
            input: new StringReader("y\n"),
            inputRedirected: false,
            yes: false,
            error: err);

        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("deleted", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal(2, run.Deleted);
        Assert.Equal(largerRecent + largerOld, run.DeletedBytes);
        Assert.Equal(0, run.Failed);
        Assert.False(run.ServiceStartFailed);

        Assert.Equal(1, handle.StopCalls);
        Assert.Equal(1, handle.StartCalls);

        // Check purge logs
        var statuses = logs.Select(l => l.Split('\t')[^1]).ToList();
        Assert.Contains("service-stop", statuses);
        Assert.Contains("deleted", statuses);
        Assert.Contains("service-start", statuses);
        Assert.Contains("service-started", statuses);

        int stopIdx = statuses.IndexOf("service-stop");
        int del1Idx = statuses.IndexOf("deleted");
        int startIdx = statuses.IndexOf("service-start");
        int startedIdx = statuses.IndexOf("service-started");

        Assert.True(stopIdx < del1Idx);
        Assert.True(del1Idx < startIdx);
        Assert.True(startIdx < startedIdx);
    }

    [Fact]
    public void M2_NoYes_RedirectedOrOtherThanYes_NeverStops_WritesFileListBeforeQuestion_YAndYESProceed()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };

        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        // Test answers that reject: redirected input without /yes, "n", "no", "ye", "other", empty, and redirected input containing "y" or "yes"
        foreach (var (inputStr, redirected) in new[] { ("", true), ("y\n", true), ("yes\n", true), ("n\n", false), ("no\n", false), ("ye\n", false), ("foo\n", false), ("\n", false) })
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            using var err = new StringWriter();
            var run = Purge.Apply(
                new[] { r }, 1, 1024, true, fs, () => Now, null, CancellationToken.None,
                services: services, control: control, localNow: () => Now, sleep: _ => {},
                input: new StringReader(inputStr), inputRedirected: redirected, yes: false, error: err);

            Assert.Equal("skipped: in use, not confirmed", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
            Assert.Equal(1, run.Skipped);

            // Assert file list is written before the question
            string errOut = err.ToString();
            Assert.Contains(@"\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log", errOut);
            Assert.Contains("Stop service tomcat85 on h to delete 1 held file(s)", errOut);
            int fileIdx = errOut.IndexOf(@"tomcat85-stderr.2026-11-20.log", StringComparison.Ordinal);
            int promptIdx = errOut.IndexOf("Stop service tomcat85", StringComparison.Ordinal);
            Assert.True(fileIdx < promptIdx);
        }

        // Test answers that accept: "y" and "YES"
        foreach (var acceptStr in new[] { "y\n", "YES\n" })
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            fs.OpenAfterStop[p] = () => new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            using var err = new StringWriter();
            var run = Purge.Apply(
                new[] { r }, 1, 1024, true, fs, () => Now, null, CancellationToken.None,
                services: services, control: control, localNow: () => Now, sleep: _ => {},
                input: new StringReader(acceptStr), inputRedirected: false, yes: false, error: err);

            Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StopCalls);
        }
    }

    [Fact]
    public void M3_YesSwitchWindowRule_1900And0559ProceedWithoutInput_0600And1859Skip()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        // 19:00 (inclusive) and 05:59 (inside) -> proceed
        foreach (var time in new[] { new DateTime(2026, 11, 20, 19, 0, 0), new DateTime(2026, 11, 21, 5, 59, 59) })
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            fs.OpenAfterStop[p] = () => new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            // Notice: StringReader("bad") ensures it does NOT read input!
            DateTime fakeTime = Now;
            var run = Purge.Apply(
                new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
                services: services, control: control, localNow: () => time, sleep: ts => fakeTime += ts,
                input: new StringReader("bad\n"), inputRedirected: false, yes: true);

            Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StopCalls);
        }

        // 06:00 (exclusive) and 18:59 (outside) -> skipped: in use, outside 19:00-06:00 for /yes
        foreach (var time in new[] { new DateTime(2026, 11, 20, 6, 0, 0), new DateTime(2026, 11, 20, 18, 59, 59) })
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            var run = Purge.Apply(
                new[] { r }, 1, 1024, true, fs, () => Now, null, CancellationToken.None,
                services: services, control: control, localNow: () => time, sleep: _ => {},
                input: new StringReader("bad\n"), inputRedirected: false, yes: true);

            Assert.Equal("skipped: in use, outside 19:00-06:00 for /yes", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
        }
    }

    [Fact]
    public void M4_StartRunsOnceOwned_InAllFailureAndExceptionScenarios()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        // 1. After delete throws
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            fs.OpenAfterStop[p] = () => throw new InvalidOperationException("disk exploded");
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("failed: disk exploded", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StartCalls);
        }

        // 2. After cancellation following stop
        {
            var cts = new CancellationTokenSource();
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle
            {
                InitialState = ServiceState.Running,
                StateAfterStop = ServiceState.Stopped,
                StateAfterStart = ServiceState.Running,
                OnStop = () => cts.Cancel() // cancel after stop!
            };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, cts.Token, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("not attempted: cancelled", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StartCalls);
        }

        // 3. After Stop timeout
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle
            {
                InitialState = ServiceState.Running,
                StateAfterStop = ServiceState.StopPending, // stays StopPending -> times out!
                StateAfterStart = ServiceState.Running
            };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("failed: could not stop tomcat85: timed out", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StartCalls);
        }

        // 4. After ControlService fails with non-1051/1062 error and state is NOT Running -> owned
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle
            {
                InitialState = ServiceState.Running,
                StopResult = (false, 5), // Access Denied (5 != 1051, 1062)
                QueryStateResult = (ServiceState.StopPending, null), // not Running!
                StateAfterStart = ServiceState.Running
            };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.StartsWith("failed: could not stop tomcat85:", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StartCalls);
        }

        // 5. When service-start log write throws -> caught, does not skip verified
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            fs.OpenAfterStop[p] = () => new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle
            {
                InitialState = ServiceState.Running,
                StateAfterStop = ServiceState.Stopped,
                StateAfterStart = ServiceState.Running
            };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, line =>
            {
                if (line.Contains("service-start\t") || line.EndsWith("service-start"))
                    throw new IOException("disk full on audit log");
            }, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

            Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.False(run.ServiceStartFailed);
            Assert.Equal(1, handle.StartCalls);
        }
    }

    [Fact]
    public void M4_QueryStateThrowsDuringStopPoll_StartStillCalled_ExceptionSurfaces()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p] = true;
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        int queryCallsAfterStop = 0;
        FakeServiceHandle handle = null!;
        handle = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running,
            CustomQuery = () =>
            {
                if (handle.StopCalls > 0)
                {
                    queryCallsAfterStop++;
                    if (queryCallsAfterStop == 1)
                    {
                        // Stop poll is the first query call after Stop
                        throw new InvalidOperationException("QueryState exploded during stop poll");
                    }
                }
                return (ServiceState.Running, null);
            }
        };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        Assert.Throws<InvalidOperationException>(() =>
            Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
                services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
                sleep: ts => fakeTime += ts, yes: true));

        Assert.Equal(1, handle.StopCalls);
        Assert.True(handle.StartCalls >= 1);
    }

    [Fact]
    public void M4_SleepThrowsDuringStopPoll_StartStillCalled_ExceptionSurfaces()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p] = true;
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        FakeServiceHandle handle = null!;
        handle = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.StopPending, // keep StopPending so poll tries to sleep
            StateAfterStart = ServiceState.Running
        };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        int sleepCallsAfterStop = 0;
        DateTime fakeTime = Now;
        Assert.Throws<InvalidOperationException>(() =>
            Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
                services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
                sleep: ts =>
                {
                    fakeTime += ts;
                    if (handle.StopCalls > 0)
                    {
                        sleepCallsAfterStop++;
                        if (sleepCallsAfterStop == 1)
                        {
                            throw new InvalidOperationException("sleep exploded during stop poll");
                        }
                    }
                },
                yes: true));

        Assert.Equal(1, handle.StopCalls);
        Assert.True(handle.StartCalls >= 1);
    }

    [Fact]
    public void StopThrows_QueryStateStopped_OwnedTrue_StartCalledOnce_ExceptionSurfaces()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p] = true;
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var handle = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running,
            OnStop = () => throw new InvalidOperationException("Stop crashed")
        };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
                services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
                sleep: ts => fakeTime += ts, yes: true));

        Assert.Equal("Stop crashed", ex.Message);
        Assert.Equal(1, handle.StopCalls);
        Assert.Equal(1, handle.StartCalls);
    }

    [Fact]
    public void StopThrows_QueryStateRunning_OwnedFalse_StartNeverCalled()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p] = true;
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var handle = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Running,
            StateAfterStart = ServiceState.Running,
            OnStop = () => throw new InvalidOperationException("Stop crashed")
        };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
                services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
                sleep: ts => fakeTime += ts, yes: true));

        Assert.Equal("Stop crashed", ex.Message);
        Assert.Equal(1, handle.StopCalls);
        Assert.Equal(0, handle.StartCalls);
    }

    [Fact]
    public void M5_AfterStopTimeout_FakeStaysStopPending_StartReturns1056_ThenFakeGoesStopped_DirusedWaitsAndCallsStartAgain()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p] = true;
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        int startCount = 0;
        int stopPendingSleep2Count = 0;
        ServiceState currentState = ServiceState.Running;
        var operations = new List<string>();
        var sleepDurations = new List<int>();

        var handle = new FakeServiceHandle();
        handle.OnStop = () => { currentState = ServiceState.StopPending; }; // stays StopPending during Stop poll
        handle.CustomStart = () =>
        {
            startCount++;
            operations.Add($"Start-{startCount}");
            if (startCount == 1)
            {
                // First Start returns 1056 and state is StopPending
                currentState = ServiceState.StopPending;
                return (false, 1056);
            }
            if (currentState == ServiceState.StopPending)
            {
                // If Start is called while still StopPending (e.g. from else branch), it fails with 1056
                return (false, 1056);
            }
            // Start when Stopped reaches Running
            currentState = ServiceState.Running;
            return (true, 0);
        };
        handle.CustomQuery = () =>
        {
            operations.Add($"QueryState-{currentState}");
            return (currentState, null);
        };

        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        var logLines = new List<string>();
        DateTime fakeTime = Now;
        var run = Purge.Apply(
            new[] { r }, 1, 1024, true, fs, () => fakeTime, l => logLines.Add(l), CancellationToken.None,
            services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
            sleep: ts =>
            {
                int sec = (int)ts.TotalSeconds;
                sleepDurations.Add(sec);
                operations.Add($"Sleep-{sec}");
                fakeTime += ts;

                // Stop poll times out after 120s: transition to Stopped so line 828 does not loop
                if (startCount == 0 && (fakeTime - Now).TotalSeconds >= 120)
                {
                    currentState = ServiceState.Stopped;
                }

                // In 1056 wait: only 2-second sleep(s) inside the 1056 loop transition it to Stopped!
                // If a 5-second sleep runs (from the non-1056 else branch), state remains StopPending!
                if (startCount == 1 && currentState == ServiceState.StopPending && sec == 2)
                {
                    stopPendingSleep2Count++;
                    if (stopPendingSleep2Count >= 1)
                    {
                        currentState = ServiceState.Stopped;
                    }
                }
            },
            yes: true);

        Assert.Equal("failed: could not stop tomcat85: timed out", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.False(run.ServiceStartFailed);

        // Assert 1056 path behavior exactly:
        // 1. Exactly 2 Start calls (no outer retry 3rd start)
        Assert.Equal(2, handle.StartCalls);
        Assert.Equal(2, startCount);

        // 2. No 5-second sleep from the non-1056 else branch
        Assert.DoesNotContain(5, sleepDurations);

        // 3. Exactly one 60-second settle sleep
        Assert.Single(sleepDurations, d => d == 60);

        // 4. Verify exact sequence: Start-1 -> Sleep-2 -> Start-2 -> Sleep-60
        int idxStart1 = operations.IndexOf("Start-1");
        int idxSleep2 = operations.FindIndex(idxStart1, op => op == "Sleep-2");
        int idxStart2 = operations.IndexOf("Start-2");
        int idxSleep60 = operations.IndexOf("Sleep-60");

        Assert.True(idxStart1 >= 0);
        Assert.True(idxSleep2 > idxStart1, "Sleep-2 must follow Start-1");
        Assert.True(idxStart2 > idxSleep2, "Start-2 must happen after StopPending 2s wait");
        Assert.True(idxSleep60 > idxStart2, "Sleep-60 must happen after Start-2 succeeds");

        // 5. Audit logs show service-start and service-started, and NO service-start-failed
        Assert.Contains(logLines, l => l.Contains("service-start"));
        Assert.Contains(logLines, l => l.Contains("service-started"));
        Assert.DoesNotContain(logLines, l => l.Contains("service-start-failed"));
    }

    [Fact]
    public void M6_NotRunningAfterStartOrSettle_RetriesOnce_FinalFailureExits3AndWritesCommand()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p] = true;
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.OpenAfterStop[p] = () => new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var handle = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Stopped
        };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        var logLines = new List<string>();
        using var err = new StringWriter();
        DateTime fakeTime = Now;
        var run = Purge.Apply(
            new[] { r }, 1, 1024, true, fs, () => fakeTime, l => logLines.Add(l), CancellationToken.None,
            services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
            sleep: ts => fakeTime += ts, yes: true, error: err);

        // Retried once -> 2 start attempts total, 0 extra stop calls (StopCalls == 1)
        Assert.Equal(1, handle.StopCalls);
        Assert.Equal(2, handle.StartCalls);
        Assert.True(run.ServiceStartFailed);

        string errStr = err.ToString();
        Assert.Contains(@"error: tomcat85 on h did not start (timed out waiting for RUNNING, state Stopped). Start it with: sc \\h start tomcat85", errStr);
        Assert.Contains(logLines, l => l.Contains("service-start-failed: timed out waiting for RUNNING"));
    }

    [Fact]
    public void M7_Open32Retry_TwiceThenSucceeds_Deletes_LongerThan30sFails()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        // Sub-test 1: 32 twice, then succeeds within 30s -> deleted
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            int openAttempts = 0;
            fs.CustomOpen = path =>
            {
                if (openAttempts < 2)
                {
                    openAttempts++;
                    return new PurgeOpen("sharing violation", notFound: false, errorCode: 32);
                }
                return new PurgeOpen(new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 }, new MemoryStream(), () => fs.DeletedPaths.Add(path), () => fs.DeletedPaths.Remove(path));
            };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, run.Deleted);
        }

        // Sub-test 2: 32 for longer than 30s -> failed: in use after stopping tomcat85
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            fs.CustomOpen = path => new PurgeOpen("sharing violation", notFound: false, errorCode: 32);
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("failed: in use after stopping tomcat85", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, run.Deleted);
        }
    }

    [Fact]
    public void M8_RecentHeldRowNoLongerHeldAtRevalidation_SkippedAgeGate_NothingStopped()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        int probeCount = 0;
        fs.CustomProbeHeld = path =>
        {
            probeCount++;
            // Initial scan probe returns true (held)
            if (probeCount == 1) return true;
            // Revalidation probe returns false (released!)
            return false;
        };
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var handle = new FakeServiceHandle { InitialState = ServiceState.Running };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

        // Released recent row is sent past the age gate -> skipped: modified within 1 day(s)
        Assert.Equal("skipped: modified within 1 day(s)", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal(0, handle.StopCalls);
        Assert.Equal(0, run.Deleted);
    }

    [Fact]
    public void M9_FromListWithPurgeConfirmYes_AppliesSameFlowWithListRowStatuses()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        var f = new LargestFile(p, 100_000_000, Old) { VolumeSerial = 1, FileId = 1, FromList = true, ListTarget = @"\\h\d$\Tomcat" };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p] = true;
        fs.OpenCallbacks[p] = new Queue<Func<PurgeOpen>>(new Func<PurgeOpen>[]
        {
            () => new PurgeOpen("sharing violation", notFound: false, errorCode: 32)
        });
        fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Old, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.OpenAfterStop[p] = () => new PurgeFileInfo(p, 110_000_000, Old, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal(1, handle.StopCalls);
        Assert.Equal(1, handle.StartCalls);
        Assert.Equal(1, run.Deleted);
    }

    [Fact]
    public void M10_TargetNextRunWarning_PrintsIfServiceNotRunning_DoesNotPrintIfRunning()
    {
        var r = new ScanResult(@"\\us03p02ffm01\d$\tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\us03p02ffm01\d$\tomcat",
            Largest = new LargestFiles(Array.Empty<string>(), 0, 0, new List<LargestFile>(), true)
        };

        // 1. Service NOT running -> prints warning
        {
            var services = new FakeServices();
            services.Hosts["us03p02ffm01"] = new[]
            {
                new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", false) // Running = false
            };
            var control = new FakeServiceControl();
            using var err = new StringWriter();
            Purge.Apply(new[] { r }, 1, 1024, false, new FakeFs(), () => Now, null, CancellationToken.None, services: services, control: control, error: err);
            Assert.Contains(@"warning: tomcat85 on us03p02ffm01 is not running; start it with: sc \\us03p02ffm01 start tomcat85", err.ToString());
        }

        // 2. Service IS running -> prints nothing
        {
            var services = new FakeServices();
            services.Hosts["us03p02ffm01"] = new[]
            {
                new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) // Running = true
            };
            var control = new FakeServiceControl();
            using var err = new StringWriter();
            Purge.Apply(new[] { r }, 1, 1024, false, new FakeFs(), () => Now, null, CancellationToken.None, services: services, control: control, error: err);
            Assert.Empty(err.ToString());
        }
    }

    [Fact]
    public void N1_HeldNonTomcatRowNeverCausesStop()
    {
        string pNonTomcat = @"\\h\d$\Tomcat\dump.hprof";
        string pTomcat = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";

        var f1 = new LargestFile(pNonTomcat, 50_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var f2 = new LargestFile(pTomcat, 50_000_000, Now) { VolumeSerial = 2, FileId = 2 };

        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log", "*.hprof" }, 2, 100_000_000, new List<LargestFile> { f1, f2 }, true)
        };

        var fs = new FakeFs();
        fs.HeldProbes[pNonTomcat] = true;
        fs.HeldProbes[pTomcat] = true;
        fs.Stats[pTomcat] = new PurgeFileInfo(pTomcat, 50_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.OpenAfterStop[pTomcat] = () => new PurgeFileInfo(pTomcat, 50_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };

        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("skipped: modified within 1 day(s)", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("deleted", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal(1, handle.StopCalls);
        Assert.Equal(1, run.Deleted);
    }

    [Fact]
    public void N2_AfterStop_IdChangedSmallerOrLeftLogsIsSkipped_SameIdLargerIsDeleted()
    {
        string pGood = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        string pBadId = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.badid.log";
        string pSmaller = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.smaller.log";
        string pLeftLogs = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.left.log";

        var fGood = new LargestFile(pGood, 100_000, Now) { VolumeSerial = 1, FileId = 1 };
        var fBadId = new LargestFile(pBadId, 100_000, Now) { VolumeSerial = 2, FileId = 2 };
        var fSmaller = new LargestFile(pSmaller, 100_000, Now) { VolumeSerial = 3, FileId = 3 };
        var fLeftLogs = new LargestFile(pLeftLogs, 100_000, Now) { VolumeSerial = 4, FileId = 4 };

        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 4, 400_000, new List<LargestFile> { fGood, fBadId, fSmaller, fLeftLogs }, true)
        };

        var fs = new FakeFs();
        foreach (var p in new[] { pGood, pBadId, pSmaller, pLeftLogs })
            fs.HeldProbes[p] = true;

        fs.Stats[pGood] = new PurgeFileInfo(pGood, 100_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.Stats[pBadId] = new PurgeFileInfo(pBadId, 100_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.Stats[pSmaller] = new PurgeFileInfo(pSmaller, 100_000, Now, false, false, 1) { VolumeSerial = 3, FileIndex = 3 };
        fs.Stats[pLeftLogs] = new PurgeFileInfo(pLeftLogs, 100_000, Now, false, false, 1) { VolumeSerial = 4, FileIndex = 4 };

        fs.OpenAfterStop[pGood] = () => new PurgeFileInfo(pGood, 120_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.OpenAfterStop[pBadId] = () => new PurgeFileInfo(pBadId, 120_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 999 }; // ID changed!
        fs.OpenAfterStop[pSmaller] = () => new PurgeFileInfo(pSmaller, 80_000, Now, false, false, 1) { VolumeSerial = 3, FileIndex = 3 }; // smaller!
        fs.OpenAfterStop[pLeftLogs] = () => new PurgeFileInfo(@"\\h\d$\Tomcat\Tomcat\other\tomcat85-stderr.left.log", 120_000, Now, false, false, 1) { VolumeSerial = 4, FileIndex = 4 }; // left logs!

        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("skipped: changed since scan (id)", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal("skipped: changed since scan (smaller)", run.Results[0].Largest!.Files[2].PurgeStatus);
        Assert.Equal("skipped: changed since scan (name)", run.Results[0].Largest!.Files[3].PurgeStatus);
        Assert.Equal(1, run.Deleted);
        Assert.Equal(3, run.Skipped);
    }

    [Fact]
    public void N3_ServiceWithRunningDependentsOr1051OrRunningAfterStop_NeverStartsOrStops()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
        };
        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        // 1. Service with running dependents
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, Dependents = new[] { "dep1", "dep2" } };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("failed: in use (service has running dependents: dep1, dep2)", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
            Assert.Equal(0, handle.StartCalls);
        }

        // 2. Stop fails with 1051
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StopResult = (false, 1051) };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.StartsWith("failed: could not stop tomcat85:", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StopCalls);
            Assert.Equal(0, handle.StartCalls); // NOT owned -> no Start!
        }

        // 3. Stop fails with 1062
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StopResult = (false, 1062) };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.StartsWith("failed: could not stop tomcat85:", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StopCalls);
            Assert.Equal(0, handle.StartCalls); // NOT owned -> no Start!
        }

        // 4. Stays RUNNING after failed Stop with other error (e.g. 5)
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle
            {
                InitialState = ServiceState.Running,
                StopResult = (false, 5),
                QueryStateResult = (ServiceState.Running, null) // stays Running
            };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.StartsWith("failed: could not stop tomcat85:", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(1, handle.StopCalls);
            Assert.Equal(0, handle.StartCalls); // NOT owned -> no Start!
        }

        // 5. RunningDependents returns null with an error (x06)
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle
            {
                InitialState = ServiceState.Running,
                DependentsError = "access denied querying dependents"
            };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("failed: could not stop tomcat85: access denied querying dependents", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
            Assert.Equal(0, handle.StartCalls);
        }

        // 6. Initial state is not Running (x07)
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle
            {
                InitialState = ServiceState.Stopped
            };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("failed: in use (tomcat85 not running)", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
            Assert.Equal(0, handle.StartCalls);
        }

        // 7. Handle open failure (N3 Open failure)
        {
            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
            var handle = new FakeServiceHandle { InitialState = ServiceState.Running };
            var control = new FakeServiceControl { OpenError = "failed to open service handle" };
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);
            Assert.Equal("failed: could not stop tomcat85: failed to open service handle", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
            Assert.Equal(0, handle.StartCalls);
        }
    }

    [Fact]
    public void N5_AfterEarlierStartFails_LaterGroupServiceNotAttemptedAndNeverStopped()
    {
        string p1 = @"\\h1\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        string p2 = @"\\h2\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";

        var f1 = new LargestFile(p1, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var f2 = new LargestFile(p2, 100_000_000, Now) { VolumeSerial = 2, FileId = 2 };

        var r1 = new ScanResult(@"\\h1\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h1\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f1.LogicalBytes, new List<LargestFile> { f1 }, true)
        };
        var r2 = new ScanResult(@"\\h2\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h2\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f2.LogicalBytes, new List<LargestFile> { f2 }, true)
        };

        var services = new FakeServices();
        services.Hosts["h1"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };
        services.Hosts["h2"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p1] = true;
        fs.HeldProbes[p2] = true;
        fs.Stats[p1] = new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.Stats[p2] = new PurgeFileInfo(p2, 100_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.OpenAfterStop[p1] = () => new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        // Group 1's Start fails (QueryState never reaches Running)
        var handle1 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Stopped
        };
        // Group 2's handle
        var handle2 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running
        };

        var control = new FakeServiceControl();
        control.Services[("h1", "tomcat85")] = handle1;
        control.Services[("h2", "tomcat85")] = handle2;

        DateTime fakeTime = Now;
        var run = Purge.Apply(
            new[] { r1, r2 }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
            services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
            sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("not attempted: earlier service start failed", run.Results[1].Largest!.Files[0].PurgeStatus);
        Assert.True(run.ServiceStartFailed);

        Assert.Equal(1, handle1.StopCalls);
        Assert.Equal(0, handle2.StopCalls); // Later group was NEVER stopped!
        Assert.Equal(0, handle2.StartCalls);
    }

    [Fact]
    public void N5_TwoGroupsBothSucceed_TwoStopsTwoStarts_AllRowsDeleted()
    {
        string p1 = @"\\h1\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        string p2 = @"\\h2\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";

        var f1 = new LargestFile(p1, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var f2 = new LargestFile(p2, 100_000_000, Now) { VolumeSerial = 2, FileId = 2 };

        var r1 = new ScanResult(@"\\h1\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h1\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f1.LogicalBytes, new List<LargestFile> { f1 }, true)
        };
        var r2 = new ScanResult(@"\\h2\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h2\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f2.LogicalBytes, new List<LargestFile> { f2 }, true)
        };

        var services = new FakeServices();
        services.Hosts["h1"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };
        services.Hosts["h2"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p1] = true;
        fs.HeldProbes[p2] = true;
        fs.Stats[p1] = new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.Stats[p2] = new PurgeFileInfo(p2, 100_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.OpenAfterStop[p1] = () => new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.OpenAfterStop[p2] = () => new PurgeFileInfo(p2, 100_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };

        var handle1 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running
        };
        var handle2 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running
        };

        var control = new FakeServiceControl();
        control.Services[("h1", "tomcat85")] = handle1;
        control.Services[("h2", "tomcat85")] = handle2;

        DateTime fakeTime = Now;
        var run = Purge.Apply(
            new[] { r1, r2 }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
            services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0),
            sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("deleted", run.Results[1].Largest!.Files[0].PurgeStatus);
        Assert.Equal(2, run.Deleted);
        Assert.False(run.ServiceStartFailed);

        Assert.Equal(1, handle1.StopCalls);
        Assert.Equal(1, handle1.StartCalls);
        Assert.Equal(1, handle2.StopCalls);
        Assert.Equal(1, handle2.StartCalls);
    }

    [Fact]
    public void M7_RevalidationRechecksTomcatRedirectLog_DifferentServiceOrNonRedirect_FailedInUse_ZeroStop()
    {
        // Case 1: Different service key at revalidation
        {
            string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
            var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\h\d$\Tomcat",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };

            var services = new FakeServices();
            services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

            fs.OnProbeHeld = _ =>
            {
                services.Hosts["h"] = new[] { new ServiceEntry("tomcat85_new", "Tomcat85", @"D:\Tomcat\Tomcat", true) };
            };

            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;
            control.Services[("h", "tomcat90")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

            Assert.Equal("failed: in use (held by a different service)", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
            Assert.Equal(0, run.Deleted);
            Assert.Equal(1, run.Failed);
        }

        // Case 2: Non-redirect name at revalidation
        {
            string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
            var f = new LargestFile(p, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\h\d$\Tomcat",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };

            var services = new FakeServices();
            services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

            var fs = new FakeFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

            fs.OnProbeHeld = _ =>
            {
                services.Hosts["h"] = Array.Empty<ServiceEntry>();
            };

            var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
            var control = new FakeServiceControl();
            control.Services[("h", "tomcat85")] = handle;

            DateTime fakeTime = Now;
            var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

            Assert.Equal("failed: in use (no service found)", run.Results[0].Largest!.Files[0].PurgeStatus);
            Assert.Equal(0, handle.StopCalls);
            Assert.Equal(0, run.Deleted);
            Assert.Equal(1, run.Failed);
        }
    }

    [Fact]
    public void M14_TwoGroupsClockMovesPast0600_Group1Stops_Group2SkippedOutsideWindow()
    {
        string p1 = @"\\h1\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        string p2 = @"\\h2\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";

        var f1 = new LargestFile(p1, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var f2 = new LargestFile(p2, 100_000_000, Now) { VolumeSerial = 2, FileId = 2 };

        var r1 = new ScanResult(@"\\h1\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h1\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f1.LogicalBytes, new List<LargestFile> { f1 }, true)
        };
        var r2 = new ScanResult(@"\\h2\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h2\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f2.LogicalBytes, new List<LargestFile> { f2 }, true)
        };

        var services = new FakeServices();
        services.Hosts["h1"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };
        services.Hosts["h2"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p1] = true;
        fs.HeldProbes[p2] = true;
        fs.Stats[p1] = new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.Stats[p2] = new PurgeFileInfo(p2, 100_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.OpenAfterStop[p1] = () => new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        DateTime localClock = new DateTime(2026, 11, 20, 5, 58, 0);

        var handle1 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running,
            OnStop = () =>
            {
                // Move clock from 05:58 to 06:01 before group 2's Stop
                localClock = new DateTime(2026, 11, 20, 6, 1, 0);
            }
        };
        var handle2 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running
        };

        var control = new FakeServiceControl();
        control.Services[("h1", "tomcat85")] = handle1;
        control.Services[("h2", "tomcat85")] = handle2;

        DateTime fakeTime = Now;
        var run = Purge.Apply(
            new[] { r1, r2 }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None,
            services: services, control: control, localNow: () => localClock,
            sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("skipped: in use, outside 19:00-06:00 for /yes", run.Results[1].Largest!.Files[0].PurgeStatus);
        Assert.Equal(1, handle1.StopCalls);
        Assert.Equal(1, handle1.StartCalls);
        Assert.Equal(0, handle2.StopCalls);
        Assert.Equal(0, handle2.StartCalls);
    }

    [Fact]
    public void M16b_NextRunWarning_WarnsWhenTargetEqualsOrUnderInstallRoot()
    {
        // 1. Target equals install root
        {
            var r = new ScanResult(@"\\us03p02ffm01\d$\Tomcat\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\us03p02ffm01\d$\Tomcat\Tomcat",
                Largest = new LargestFiles(Array.Empty<string>(), 0, 0, new List<LargestFile>(), true)
            };
            var services = new FakeServices();
            services.Hosts["us03p02ffm01"] = new[]
            {
                new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", false)
            };
            var control = new FakeServiceControl();
            using var err = new StringWriter();
            Purge.Apply(new[] { r }, 1, 1024, false, new FakeFs(), () => Now, null, CancellationToken.None, services: services, control: control, error: err);
            Assert.Contains(@"warning: tomcat85 on us03p02ffm01 is not running; start it with: sc \\us03p02ffm01 start tomcat85", err.ToString());
        }

        // 2. Target is under install root
        {
            var r = new ScanResult(@"\\us03p02ffm01\d$\Tomcat\Tomcat\logs", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\us03p02ffm01\d$\Tomcat\Tomcat\logs",
                Largest = new LargestFiles(Array.Empty<string>(), 0, 0, new List<LargestFile>(), true)
            };
            var services = new FakeServices();
            services.Hosts["us03p02ffm01"] = new[]
            {
                new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", false)
            };
            var control = new FakeServiceControl();
            using var err = new StringWriter();
            Purge.Apply(new[] { r }, 1, 1024, false, new FakeFs(), () => Now, null, CancellationToken.None, services: services, control: control, error: err);
            Assert.Contains(@"warning: tomcat85 on us03p02ffm01 is not running; start it with: sc \\us03p02ffm01 start tomcat85", err.ToString());
        }
    }

    [Fact]
    public void X01_N1_FileHeldByNonTomcatProcessWithRedirectLogName_NotHeldBackForStop_ZeroStopForIt()
    {
        string pNonTomcat = @"\\h\d$\Tomcat\Tomcat\logs\otherapp-stderr.2026-11-04.log";
        string pTomcat = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";

        // Old file (LastWriteUtc < cutoff) held by non-tomcat process with redirect-log naming pattern
        var f1 = new LargestFile(pNonTomcat, 50_000_000, Old) { VolumeSerial = 1, FileId = 1 };
        var f2 = new LargestFile(pTomcat, 50_000_000, Now) { VolumeSerial = 2, FileId = 2 };

        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 2, 100_000_000, new List<LargestFile> { f1, f2 }, true)
        };

        var fs = new FakeFs();
        fs.HeldProbes[pNonTomcat] = true;
        fs.HeldProbes[pTomcat] = true;

        fs.OpenCallbacks[pNonTomcat] = new Queue<Func<PurgeOpen>>(new Func<PurgeOpen>[]
        {
            () => new PurgeOpen("sharing violation", notFound: false, errorCode: 32)
        });
        fs.Stats[pNonTomcat] = new PurgeFileInfo(pNonTomcat, 50_000_000, Old, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        fs.Stats[pTomcat] = new PurgeFileInfo(pTomcat, 50_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.OpenAfterStop[pTomcat] = () => new PurgeFileInfo(pTomcat, 50_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };

        var services = new FakeServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var handle = new FakeServiceHandle { InitialState = ServiceState.Running, StateAfterStop = ServiceState.Stopped, StateAfterStart = ServiceState.Running };
        var control = new FakeServiceControl();
        control.Services[("h", "tomcat85")] = handle;

        DateTime fakeTime = Now;
        var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => fakeTime, null, CancellationToken.None, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("failed: in use (not a Tomcat log)", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("deleted", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal(1, handle.StopCalls);
        Assert.Equal(1, run.Deleted);
        Assert.Equal(1, run.Failed);
    }

    [Fact]
    public void X12_CancellationRequestedAtGroupHead_NotAttemptedCancelled_ZeroStopZeroStart()
    {
        string p1 = @"\\h1\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";
        string p2 = @"\\h2\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-20.log";

        var f1 = new LargestFile(p1, 100_000_000, Now) { VolumeSerial = 1, FileId = 1 };
        var f2 = new LargestFile(p2, 100_000_000, Now) { VolumeSerial = 2, FileId = 2 };

        var r1 = new ScanResult(@"\\h1\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h1\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f1.LogicalBytes, new List<LargestFile> { f1 }, true)
        };
        var r2 = new ScanResult(@"\\h2\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h2\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, f2.LogicalBytes, new List<LargestFile> { f2 }, true)
        };

        var services = new FakeServices();
        services.Hosts["h1"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };
        services.Hosts["h2"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var fs = new FakeFs();
        fs.HeldProbes[p1] = true;
        fs.HeldProbes[p2] = true;
        fs.Stats[p1] = new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.Stats[p2] = new PurgeFileInfo(p2, 100_000_000, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.OpenAfterStop[p1] = () => new PurgeFileInfo(p1, 100_000_000, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var cts = new CancellationTokenSource();
        var handle1 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running,
            CustomStart = () =>
            {
                cts.Cancel(); // cancel after group 1 start, before group 2 arrives at group head
                return (true, 0);
            }
        };
        var handle2 = new FakeServiceHandle
        {
            InitialState = ServiceState.Running,
            StateAfterStop = ServiceState.Stopped,
            StateAfterStart = ServiceState.Running
        };

        var control = new FakeServiceControl();
        control.Services[("h1", "tomcat85")] = handle1;
        control.Services[("h2", "tomcat85")] = handle2;

        DateTime fakeTime = Now;
        var run = Purge.Apply(new[] { r1, r2 }, 1, 1024, true, fs, () => fakeTime, null, cts.Token, services: services, control: control, localNow: () => new DateTime(2026, 11, 20, 20, 0, 0), sleep: ts => fakeTime += ts, yes: true);

        Assert.Equal("deleted", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("not attempted: cancelled", run.Results[1].Largest!.Files[0].PurgeStatus);
        Assert.Equal(1, handle1.StopCalls);
        Assert.Equal(0, handle2.StopCalls);
        Assert.Equal(0, handle2.StartCalls);
    }

    private sealed class FakeServiceHandle : IServiceHandle
    {
        public int StopCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public ServiceState InitialState { get; set; } = ServiceState.Running;
        public ServiceState StateAfterStop { get; set; } = ServiceState.Stopped;
        public ServiceState StateAfterStart { get; set; } = ServiceState.Running;

        public (bool Ok, int Win32Error)? StopResult { get; set; }
        public (bool Ok, int Win32Error)? StartResult { get; set; }
        public (ServiceState State, string? Error)? QueryStateResult { get; set; }
        public IReadOnlyList<string>? Dependents { get; set; }
        public string? DependentsError { get; set; }

        public Action? OnStop { get; set; }
        public Func<(bool Ok, int Win32Error)>? CustomStart { get; set; }
        public Func<(ServiceState State, string? Error)>? CustomQuery { get; set; }

        private bool stopAttempted = false;
        private bool stopped = false;
        private bool started = false;

        public IReadOnlyList<string>? RunningDependents(out string? error)
        {
            if (DependentsError != null)
            {
                error = DependentsError;
                return null;
            }
            error = null;
            return Dependents ?? Array.Empty<string>();
        }

        public (bool Ok, int Win32Error) Stop()
        {
            StopCalls++;
            stopAttempted = true;
            OnStop?.Invoke();
            if (StopResult.HasValue) return StopResult.Value;
            stopped = true;
            return (true, 0);
        }

        public (bool Ok, int Win32Error) Start()
        {
            StartCalls++;
            started = true;
            if (CustomStart != null) return CustomStart();
            if (StartResult.HasValue) return StartResult.Value;
            return (true, 0);
        }

        public (ServiceState State, string? Error) QueryState()
        {
            QueryCalls++;
            if (CustomQuery != null) return CustomQuery();
            if (started)
            {
                return (StateAfterStart, null);
            }
            if (stopAttempted || stopped)
            {
                if (QueryStateResult.HasValue) return QueryStateResult.Value;
                return (StateAfterStop, null);
            }
            return (InitialState, null);
        }

        public void Dispose() {}
    }

    private sealed class FakeServiceControl : IServiceControl
    {
        public Dictionary<(string Host, string Key), FakeServiceHandle> Services { get; } = new();
        public int OpenCalls { get; private set; }
        public string? OpenError { get; set; }

        public IServiceHandle? Open(string host, string key, out string? error)
        {
            OpenCalls++;
            if (OpenError != null)
            {
                error = OpenError;
                return null;
            }
            error = null;
            if (Services.TryGetValue((host, key), out var handle))
            {
                return handle;
            }
            error = "Service not found";
            return null;
        }
    }

    private sealed class FakeServices : IServiceQuery
    {
        public Dictionary<string, IReadOnlyList<ServiceEntry>> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error)
        {
            error = null;
            if (Hosts.TryGetValue(host, out var entries)) return entries;
            error = "host unreachable";
            return null;
        }
    }

    private sealed class FakeFs : IPurgeFileSystem
    {
        public HashSet<string> DeletedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool?> HeldProbes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeFileInfo> Stats { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Queue<Func<PurgeOpen>>> OpenCallbacks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Func<PurgeFileInfo>> OpenAfterStop { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, PurgeOpen>? CustomOpen { get; set; }
        public Func<string, bool?>? CustomProbeHeld { get; set; }
        public Action<string>? OnProbeHeld { get; set; }

        public string? ResolveRoot(string path, out string? error)
        {
            error = null;
            return path;
        }

        public PurgeOpen Open(string path)
        {
            if (DeletedPaths.Contains(path))
            {
                return new PurgeOpen("File not found", notFound: true);
            }

            if (CustomOpen != null) return CustomOpen(path);

            if (OpenCallbacks.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue()();
            }

            if (OpenAfterStop.TryGetValue(path, out var infoFactory))
            {
                var info = infoFactory();
                return new PurgeOpen(info, new MemoryStream(), () => DeletedPaths.Add(path), () => DeletedPaths.Remove(path));
            }

            if (Stats.TryGetValue(path, out var stat))
            {
                return new PurgeOpen(stat, new MemoryStream(), () => DeletedPaths.Add(path), () => DeletedPaths.Remove(path));
            }

            return new PurgeOpen("File not found", notFound: true);
        }

        public PurgeFileInfo? Stat(string path, out string? error, out bool notFound)
        {
            error = null;
            notFound = false;
            if (Stats.TryGetValue(path, out var stat)) return stat;
            notFound = true;
            return null;
        }

        public bool? ProbeHeld(string path, out string? error)
        {
            error = null;
            OnProbeHeld?.Invoke(path);
            if (CustomProbeHeld != null) return CustomProbeHeld(path);
            if (HeldProbes.TryGetValue(path, out var held)) return held;
            return false;
        }
    }
}
