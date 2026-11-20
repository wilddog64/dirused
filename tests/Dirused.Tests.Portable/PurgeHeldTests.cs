using Dirused.Common;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class PurgeHeldTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void M1_DryRunHeldTomcatRedirectLogBecomesWouldDeleteAfterStoppingAndSummaryPopulated()
    {
        string filePath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        var file = new LargestFile(filePath, 117L * 1024 * 1024 * 1024, Old)
        {
            VolumeSerial = 0x11223344,
            FileId = 0x5566778899AABBCC
        };
        var result = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 1, file.LogicalBytes, new List<LargestFile> { file }, true)
        };

        var fs = new TestFs();
        fs.HeldProbes[filePath] = true;
        fs.Stats[filePath] = new PurgeFileInfo(filePath, file.LogicalBytes, Old, false, false, 1)
        {
            VolumeSerial = file.VolumeSerial,
            FileIndex = file.FileId
        };

        var services = new TestServices();
        services.Hosts["h"] = new[]
        {
            new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true)
        };

        var run = Purge.Apply(new[] { result }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services);

        Assert.Equal("would delete after stopping tomcat85", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal(1, run.WouldDelete);
        Assert.Equal(file.LogicalBytes, run.WouldDeleteBytes);
        Assert.NotNull(run.HeldSummary);
        Assert.Equal(1, run.HeldSummary.Total);
        Assert.Equal("tomcat85", run.HeldSummary.Service);
        Assert.Equal("h", run.HeldSummary.Host);
        Assert.Equal(@"D:\Tomcat\Tomcat", run.HeldSummary.InstallRoot);
        Assert.Equal(1, run.HeldSummary.TomcatCount);
        Assert.Equal(file.LogicalBytes, run.HeldSummary.TomcatBytes);
        Assert.Equal(0, run.HeldSummary.OtherCount);
    }

    [Fact]
    public void M2_D1AgeExemptionHeldLogIsWouldDeleteAfterStoppingAndNotHeldKeepsAgeStatus()
    {
        string heldPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        string unheldPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stdout.2026-11-04.log";

        var heldFile = new LargestFile(heldPath, 500_000_000, Now) { VolumeSerial = 0x11223344, FileId = 0x1111 };
        var unheldFile = new LargestFile(unheldPath, 500_000_000, Now) { VolumeSerial = 0x11223344, FileId = 0x2222 };

        var result = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 2, heldFile.LogicalBytes + unheldFile.LogicalBytes, new List<LargestFile> { heldFile, unheldFile }, true)
        };

        var fs = new TestFs();
        fs.HeldProbes[heldPath] = true;
        fs.HeldProbes[unheldPath] = false;
        fs.Stats[heldPath] = new PurgeFileInfo(heldPath, heldFile.LogicalBytes, Now, false, false, 1)
        {
            VolumeSerial = heldFile.VolumeSerial,
            FileIndex = heldFile.FileId
        };

        var services = new TestServices();
        services.Hosts["h"] = new[]
        {
            new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true)
        };

        var run = Purge.Apply(new[] { result }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services);

        Assert.Equal("would delete after stopping tomcat85", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("skipped: modified within 1 day(s)", run.Results[0].Largest!.Files[1].PurgeStatus);
    }

    [Fact]
    public void M3_ClassificationStatusesCoverAllReasons()
    {
        var services = new TestServices();
        services.Hosts["h"] = new[]
        {
            new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true)
        };

        // 1. Held .hprof anywhere -> skipped: in use (heap dump, holder unknown)
        {
            string p = @"\\h\d$\Tomcat\dump.hprof";
            var f = new LargestFile(p, 10_000, Old) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"\\h\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\h\d$",
                Largest = new LargestFiles(new[] { "*.hprof" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };
            var fs = new TestFs();
            fs.HeldProbes[p] = true;
            var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services);
            Assert.Equal("skipped: in use (heap dump, holder unknown)", run.Results[0].Largest!.Files[0].PurgeStatus);
        }

        // 2. Held catalina log under root -> skipped: in use (not a Tomcat log)
        {
            string p = @"\\h\d$\Tomcat\Tomcat\catalina.2026-11-19.log";
            var f = new LargestFile(p, 10_000, Old) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"\\h\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\h\d$",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };
            var fs = new TestFs();
            fs.HeldProbes[p] = true;
            var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services);
            Assert.Equal("skipped: in use (not a Tomcat log)", run.Results[0].Largest!.Files[0].PurgeStatus);
        }

        // 3. Held file on non-admin share -> skipped: in use (no service found)
        {
            string p = @"\\h\userfiles\logs\app.log";
            var f = new LargestFile(p, 10_000, Old) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"\\h\userfiles", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\h\userfiles",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };
            var fs = new TestFs();
            fs.HeldProbes[p] = true;
            var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services);
            Assert.Equal("skipped: in use (no service found)", run.Results[0].Largest!.Files[0].PurgeStatus);
        }

        // 3b. Held file on non-fixed drive letter -> skipped: in use (no service found)
        {
            string p = @"Z:\logs\app.log";
            var f = new LargestFile(p, 10_000, Old) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"Z:\logs", new Volume("Z:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"Z:\logs",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };
            var fs = new TestFs();
            fs.HeldProbes[p] = true;
            var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services, isFixedDrive: _ => false);
            Assert.Equal("skipped: in use (no service found)", run.Results[0].Largest!.Files[0].PurgeStatus);
        }

        // 4. ListProcrun null -> skipped: in use (could not list services on <h>: <error>)
        {
            string p = @"\\badhost\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
            var f = new LargestFile(p, 10_000, Old) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"\\badhost\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\badhost\d$",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };
            var fs = new TestFs();
            fs.HeldProbes[p] = true;
            var badServices = new TestServices();
            badServices.Errors["badhost"] = "network unreachable";
            var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: badServices);
            Assert.Equal("skipped: in use (could not list services on badhost: network unreachable)", run.Results[0].Largest!.Files[0].PurgeStatus);
        }

        // 5. Two services matching -> no service found
        {
            string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
            var f = new LargestFile(p, 10_000, Old) { VolumeSerial = 1, FileId = 1 };
            var r = new ScanResult(@"\\h\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\h\d$",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };
            var fs = new TestFs();
            fs.HeldProbes[p] = true;
            var twoServices = new TestServices();
            twoServices.Hosts["h"] = new[]
            {
                new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true),
                new ServiceEntry("tomcat85_2", "Tomcat85", @"D:\Tomcat\Tomcat", true)
            };
            var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: twoServices);
            Assert.Equal("skipped: in use (no service found)", run.Results[0].Largest!.Files[0].PurgeStatus);
        }

        // 6. Stat ID differs -> skipped: in use (id mismatch)
        {
            string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
            var f = new LargestFile(p, 10_000, Old) { VolumeSerial = 0x1111, FileId = 0x2222 };
            var r = new ScanResult(@"\\h\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
            {
                ScanRoot = @"\\h\d$",
                Largest = new LargestFiles(new[] { "*.log" }, 1, f.LogicalBytes, new List<LargestFile> { f }, true)
            };
            var fs = new TestFs();
            fs.HeldProbes[p] = true;
            fs.Stats[p] = new PurgeFileInfo(p, 10_000, Old, false, false, 1) { VolumeSerial = 0x1111, FileIndex = 0x9999 };
            var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services);
            Assert.Equal("skipped: in use (id mismatch)", run.Results[0].Largest!.Files[0].PurgeStatus);
        }
    }

    [Fact]
    public void M4_ConfirmHeldTomcatFileFailsWithStopNotAvailableAndNotDeleted()
    {
        string tomcatPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        string otherPath = @"\\h\d$\Tomcat\Tomcat\logs\other.log";

        var tomcatFile = new LargestFile(tomcatPath, 10_000, Old) { VolumeSerial = 0x1111, FileId = 0x2222 };
        var otherFile = new LargestFile(otherPath, 10_000, Old) { VolumeSerial = 0x3333, FileId = 0x4444 };

        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 2, tomcatFile.LogicalBytes + otherFile.LogicalBytes, new List<LargestFile> { tomcatFile, otherFile }, true)
        };

        var fs = new TestFs();
        fs.Opens[tomcatPath] = new PurgeOpen("sharing violation", notFound: false, errorCode: 32);
        fs.Opens[otherPath] = new PurgeOpen("sharing violation", notFound: false, errorCode: 32);
        fs.Stats[tomcatPath] = new PurgeFileInfo(tomcatPath, tomcatFile.LogicalBytes, Old, false, false, 1)
        {
            VolumeSerial = tomcatFile.VolumeSerial,
            FileIndex = tomcatFile.FileId
        };

        var services = new TestServices();
        services.Hosts["h"] = new[]
        {
            new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true)
        };

        using var log = new StringWriter();
        var run = Purge.Apply(new[] { r }, 1, 1024, true, fs, () => Now, s => log.WriteLine(s), CancellationToken.None, services: services);

        Assert.Equal("failed: in use by tomcat85 (stop not available yet)", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("failed: in use (not a Tomcat log)", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal(0, run.Deleted);
        Assert.Equal(2, run.Failed);
        Assert.Equal(0, fs.Deletes);
    }

    [Fact]
    public void M5_FromListDryRunHeldLogThatGrewIsWouldDeleteAfterStoppingAndUnheldStaysSkipped()
    {
        string heldPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        string unheldPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stdout.2026-11-04.log";

        var heldFile = new LargestFile(heldPath, 100_000_000, Old) { VolumeSerial = 0x1111, FileId = 0x2222, FromList = true, ListTarget = @"\\h\d$\Tomcat" };
        var unheldFile = new LargestFile(unheldPath, 100_000_000, Old) { VolumeSerial = 0x3333, FileId = 0x4444, FromList = true, ListTarget = @"\\h\d$\Tomcat" };

        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(new[] { "*.log" }, 2, heldFile.LogicalBytes + unheldFile.LogicalBytes, new List<LargestFile> { heldFile, unheldFile }, true)
        };

        var fs = new TestFs();
        fs.HeldProbes[heldPath] = true;
        fs.HeldProbes[unheldPath] = false;
        fs.Stats[heldPath] = new PurgeFileInfo(heldPath, 200_000_000, Now, false, false, 1)
        {
            VolumeSerial = heldFile.VolumeSerial,
            FileIndex = heldFile.FileId
        };
        fs.Stats[unheldPath] = new PurgeFileInfo(unheldPath, 200_000_000, Now, false, false, 1)
        {
            VolumeSerial = unheldFile.VolumeSerial,
            FileIndex = unheldFile.FileId
        };

        var services = new TestServices();
        services.Hosts["h"] = new[]
        {
            new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true)
        };

        var run = Purge.Apply(new[] { r }, 1, 1024, false, fs, () => Now, null, CancellationToken.None, services: services);

        Assert.Equal("would delete after stopping tomcat85", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("skipped: file changed since list (size or time)", run.Results[0].Largest!.Files[1].PurgeStatus);
    }

    [Fact]
    public void M7_ProcrunPathParseAndInstallRootHandleQuotesAndBin()
    {
        var p1 = ProcrunPath.Parse(@"D:\Tomcat\Tomcat\bin\Tomcat85.exe //RS//Tomcat85");
        Assert.NotNull(p1);
        Assert.Equal(@"D:\Tomcat\Tomcat\bin\Tomcat85.exe", p1.Value.Exe);
        Assert.Equal("Tomcat85", p1.Value.Name);

        var p2 = ProcrunPath.Parse("\"C:\\Program Files\\Apache\\bin\\Tomcat9.exe\" //RS//Tomcat9");
        Assert.NotNull(p2);
        Assert.Equal(@"C:\Program Files\Apache\bin\Tomcat9.exe", p2.Value.Exe);
        Assert.Equal("Tomcat9", p2.Value.Name);

        Assert.Null(ProcrunPath.Parse(@"C:\bin\service.exe"));
        Assert.Null(ProcrunPath.Parse(""));

        Assert.Equal(@"D:\Tomcat\Tomcat", ProcrunPath.InstallRoot(@"D:\Tomcat\Tomcat\bin\Tomcat85.exe"));
        Assert.Equal(@"C:\Program Files\Apache", ProcrunPath.InstallRoot(@"C:\Program Files\Apache\bin\Tomcat9.exe"));
        Assert.Equal(@"D:\Tomcat\Tomcat", ProcrunPath.InstallRoot(@"D:\Tomcat\Tomcat\Tomcat85.exe"));
        Assert.Equal(@"D:\", ProcrunPath.InstallRoot(@"D:\bin\Tomcat85.exe"));
    }

    [Fact]
    public void M8_ScanRowIdentityRejectsReplacementWithDifferentIdUnderConfirm()
    {
        string p = @"C:\t\replaced.log";
        var file = new LargestFile(p, 100, Old) { VolumeSerial = 0x1111, FileId = 0x2222 };
        var fileWithoutId = new LargestFile(@"C:\t\noid.log", 100, Old);

        var r = new ScanResult(@"C:\t", new Volume("C:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"C:\t",
            Largest = new LargestFiles(new[] { "*.log" }, 2, 200, new List<LargestFile> { file, fileWithoutId }, true)
        };

        var fs = new TestFs();
        fs.OpenInfos[p] = new PurgeFileInfo(p, 100, Old, false, false, 1) { VolumeSerial = 0x1111, FileIndex = 0x9999 };
        fs.OpenInfos[fileWithoutId.Path] = new PurgeFileInfo(fileWithoutId.Path, 100, Old, false, false, 1);

        var run = Purge.Apply(new[] { r }, 1, 1, true, fs, () => Now, _ => {}, CancellationToken.None);

        Assert.Equal("skipped: changed since scan (id)", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("deleted", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal(1, fs.Deletes);
    }

    [Fact]
    public void N1_DryRunNeverRequestsDeleteAndServiceListOnlyFetchedForHeldFiles()
    {
        string heldH1 = @"\\h1\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        string unheldH2 = @"\\h2\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";

        var f1 = new LargestFile(heldH1, 100, Old) { VolumeSerial = 1, FileId = 1 };
        var f2 = new LargestFile(unheldH2, 100, Old) { VolumeSerial = 2, FileId = 2 };

        var r1 = new ScanResult(@"\\h1\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true) { ScanRoot = @"\\h1\d$", Largest = new LargestFiles(Array.Empty<string>(), 1, 100, new List<LargestFile> { f1 }, true) };
        var r2 = new ScanResult(@"\\h2\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true) { ScanRoot = @"\\h2\d$", Largest = new LargestFiles(Array.Empty<string>(), 1, 100, new List<LargestFile> { f2 }, true) };

        var fs = new TestFs();
        fs.HeldProbes[heldH1] = true;
        fs.HeldProbes[unheldH2] = false;
        fs.Stats[heldH1] = new PurgeFileInfo(heldH1, 100, Old, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var services = new TestServices();
        services.Hosts["h1"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };
        services.Hosts["h2"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        Purge.Apply(new[] { r1, r2 }, 1, 1, false, fs, () => Now, null, CancellationToken.None, services: services);

        Assert.True(services.HostCalls.ContainsKey("h1"));
        Assert.False(services.HostCalls.ContainsKey("h2"));
    }

    [Fact]
    public void N2_PartialServiceListReturnsNullAndDoesNotUseIncompleteEntries()
    {
        string p = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        var f = new LargestFile(p, 100, Old) { VolumeSerial = 1, FileId = 1 };
        var r = new ScanResult(@"\\h\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true) { ScanRoot = @"\\h\d$", Largest = new LargestFiles(Array.Empty<string>(), 1, 100, new List<LargestFile> { f }, true) };

        var fs = new TestFs();
        fs.HeldProbes[p] = true;

        var services = new TestServices();
        services.Errors["h"] = "access denied querying config on service2";

        var run = Purge.Apply(new[] { r }, 1, 1, false, fs, () => Now, null, CancellationToken.None, services: services);

        Assert.Equal("skipped: in use (could not list services on h: access denied querying config on service2)", run.Results[0].Largest!.Files[0].PurgeStatus);
    }

    [Fact]
    public void N3_D1ExemptionDoesNotApplyToStaleCatalinaLog()
    {
        string redirectPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        string catalinaPath = @"\\h\d$\Tomcat\Tomcat\logs\catalina.2026-11-04.log";

        var redirect = new LargestFile(redirectPath, 100, Now) { VolumeSerial = 1, FileId = 1 };
        var catalina = new LargestFile(catalinaPath, 100, Now) { VolumeSerial = 2, FileId = 2 };

        var r = new ScanResult(@"\\h\d$", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true) { ScanRoot = @"\\h\d$", Largest = new LargestFiles(Array.Empty<string>(), 2, 200, new List<LargestFile> { redirect, catalina }, true) };

        var fs = new TestFs();
        fs.HeldProbes[redirectPath] = true;
        fs.HeldProbes[catalinaPath] = true;
        fs.Stats[redirectPath] = new PurgeFileInfo(redirectPath, 100, Now, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };
        fs.Stats[catalinaPath] = new PurgeFileInfo(catalinaPath, 100, Now, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };

        var services = new TestServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var run = Purge.Apply(new[] { r }, 1, 1, false, fs, () => Now, null, CancellationToken.None, services: services);

        Assert.Equal("would delete after stopping tomcat85", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("skipped: modified within 1 day(s)", run.Results[0].Largest!.Files[1].PurgeStatus);
    }

    [Fact]
    public void N4_ConfirmDoesNotDeleteHeldFileWhileDeletingEligiblePeer()
    {
        string heldPath = @"\\h\d$\Tomcat\Tomcat\logs\tomcat85-stderr.2026-11-04.log";
        string eligiblePath = @"\\h\d$\Tomcat\Tomcat\logs\old.log";

        var held = new LargestFile(heldPath, 100, Old) { VolumeSerial = 1, FileId = 1 };
        var eligible = new LargestFile(eligiblePath, 100, Old) { VolumeSerial = 2, FileId = 2 };

        var r = new ScanResult(@"\\h\d$\Tomcat", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true)
        {
            ScanRoot = @"\\h\d$\Tomcat",
            Largest = new LargestFiles(Array.Empty<string>(), 2, 200, new List<LargestFile> { held, eligible }, true)
        };

        var fs = new TestFs();
        fs.Opens[heldPath] = new PurgeOpen("sharing violation", notFound: false, errorCode: 32);
        fs.OpenInfos[eligiblePath] = new PurgeFileInfo(eligiblePath, 100, Old, false, false, 1) { VolumeSerial = 2, FileIndex = 2 };
        fs.Stats[heldPath] = new PurgeFileInfo(heldPath, 100, Old, false, false, 1) { VolumeSerial = 1, FileIndex = 1 };

        var services = new TestServices();
        services.Hosts["h"] = new[] { new ServiceEntry("tomcat85", "Tomcat85", @"D:\Tomcat\Tomcat", true) };

        var run = Purge.Apply(new[] { r }, 1, 1, true, fs, () => Now, _ => {}, CancellationToken.None, services: services);

        Assert.Equal("failed: in use by tomcat85 (stop not available yet)", run.Results[0].Largest!.Files[0].PurgeStatus);
        Assert.Equal("deleted", run.Results[0].Largest!.Files[1].PurgeStatus);
        Assert.Equal(1, run.Deleted);
        Assert.Equal(1, run.Failed);
        Assert.Equal(1, fs.Deletes);
        Assert.DoesNotContain(heldPath, fs.DeletedPaths);
        Assert.Contains(eligiblePath, fs.DeletedPaths);
    }

    private sealed class TestServices : IServiceQuery
    {
        public Dictionary<string, IReadOnlyList<ServiceEntry>> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> HostCalls { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error)
        {
            HostCalls[host] = HostCalls.GetValueOrDefault(host, 0) + 1;
            if (Errors.TryGetValue(host, out var err))
            {
                error = err;
                return null;
            }
            error = null;
            return Hosts.GetValueOrDefault(host, Array.Empty<ServiceEntry>());
        }
    }

    private sealed class TestFs : IPurgeFileSystem
    {
        public Dictionary<string, bool?> HeldProbes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeFileInfo> Stats { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeOpen> Opens { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PurgeFileInfo> OpenInfos { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeletedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Deletes { get; private set; }

        public string? ResolveRoot(string path, out string? error)
        {
            error = null;
            return path;
        }

        public bool? ProbeHeld(string path, out string? error)
        {
            error = null;
            if (HeldProbes.TryGetValue(path, out var held))
            {
                if (held == null) error = "probe error";
                return held;
            }
            return false;
        }

        public PurgeFileInfo? Stat(string path, out string? error, out bool notFound)
        {
            error = null;
            notFound = false;
            if (Stats.TryGetValue(path, out var info)) return info;
            error = "not found";
            notFound = true;
            return null;
        }

        public PurgeOpen Open(string path)
        {
            if (DeletedPaths.Contains(path)) return new PurgeOpen("gone", notFound: true);
            if (Opens.TryGetValue(path, out var errorOpen)) return errorOpen;
            if (OpenInfos.TryGetValue(path, out var info))
            {
                return new PurgeOpen(info, new TestHandle(), () =>
                {
                    Deletes++;
                    DeletedPaths.Add(path);
                });
            }
            var defaultInfo = new PurgeFileInfo(path, 100, Old, false, false, 1);
            return new PurgeOpen(defaultInfo, new TestHandle(), () =>
            {
                Deletes++;
                DeletedPaths.Add(path);
            });
        }

        private sealed class TestHandle : IDisposable
        {
            public void Dispose() {}
        }
    }
}
