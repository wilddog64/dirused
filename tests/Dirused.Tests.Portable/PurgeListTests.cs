using Dirused.Common;
using Dirused.Common.Renderers;
using Dirused.Core;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class PurgeListTests
{
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static LargestFile Row(string name, uint serial=0x12345678, ulong id=0x123456789ABCDEF0, long bytes=500_000_000, DateTime? time=null) => new($@"C:\logs\{name}", bytes, time??Old){VolumeSerial=serial,FileId=id};
    private static ScanResult Result(params LargestFile[] files) => new(@"C:\logs",new Volume("C:",0,0,0,0),new(),new(),new(),true){ScanRoot=@"C:\logs",Largest=new LargestFiles(Array.Empty<string>(),files.Length,files.Sum(f=>f.LogicalBytes),files.ToList(),true)};
    private static PurgeFileInfo Info(LargestFile f, uint? serial=null, ulong? id=null, long? bytes=null, DateTime? time=null) => new(f.Path,bytes??f.LogicalBytes,time??f.LastWriteUtc!.Value,false,false,1){VolumeSerial=serial??f.VolumeSerial,FileIndex=id??f.FileId};

    [Fact] public void M1_WritesOnlyWouldDeleteRowsWithNtfsIdentityAndHeaders()
    {
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var path=Path.Combine(dir,"delete.txt");
        try { var good=Row("good");var absent=Row("absent",serial:0);var skipped=Row("skip") with {PurgeStatus="skipped: changed"};var r=Result(good with {PurgeStatus="would delete"},absent with {PurgeStatus="would delete"},skipped);var summary=PurgeList.Summarize(new[]{r});Assert.Equal(1,summary.SavedFiles);Assert.Equal(1,summary.NoIdFiles);
            PurgeList.Write(path,new[]{r},7,1024,"test",new[]{"/largest","*.log","/purge","7"});var lines=File.ReadAllLines(path);Assert.StartsWith("# dirused delete list v2",lines[0]);Assert.Equal("# purge days=7 minsize=1024",lines[1]);Assert.Single(lines.Skip(6));Assert.Contains("good",lines[6]);
            PurgeList.Write(path,new[]{Result()},7,1024,"test",Array.Empty<string>());var empty=File.ReadAllLines(path);Assert.Equal("# purge days=7 minsize=1024",empty[1]);Assert.All(empty,l=>Assert.StartsWith("#",l.TrimStart('\uFEFF')));
        } finally {Directory.Delete(dir,true);}
    }

    [Theory]
    [InlineData("# dirused delete list v1\n# purge days=0 minsize=1\n")]
    [InlineData("# dirused delete list v1\n# purge days=3651 minsize=1\n")]
    [InlineData("# dirused delete list v1\n# purge days=-1 minsize=1\n")]
    [InlineData("# dirused delete list v1\n# purge days=1 minsize=4611686018427387905\n")]
    [InlineData("# dirused delete list v1\n# purge days=1 minsize=0\n")]
    [InlineData("wrong\n# purge days=1 minsize=1\n")]
    public void M5_N5_RejectsInvalidHeadersAndBounds(string value)
    { var p=Path.GetTempFileName();try{File.WriteAllText(p,value);Assert.False(PurgeList.Read(p,out _));}finally{File.Delete(p);} }

    [Fact] public void M6_N3_ReportsMalformedDuplicateAndProcessesValidPeers()
    {
        var file=Row("valid");string line=$"C:\\logs\t{file.VolumeSerial:X8}\t{file.FileId:X16}\t{file.LogicalBytes}\t{file.LastWriteUtc:O}\t{file.Path}";
        var p=Path.GetTempFileName();try{File.WriteAllText(p,$"{PurgeList.HeaderV1}\r\n# purge days=1 minsize=1\r\n# ignored\r\n\r\nbad\r\nC:\\logs\t00000000\t0000000000000001\t1\t{Old:O}\tx\r\nC:\\logs\t12345678\tFFFFFFFFFFFFFFFF\t1\t{Old:O}\ty\r\nC:\\logs\t12345678\t0000000000000000\t1\t{Old:O}\tzero-id\r\nC:\\logs\tzzzzzzzz\t0000000000000001\t1\t{Old:O}\tbad-hex\r\nC:\\logs\t12345678\t0000000000000001\tnope\t{Old:O}\tbad-number\r\nC:\\logs\t12345678\t0000000000000001\t1\tbad-time\tbad-time\r\n{line}\r\n{line}\r\n");Assert.True(PurgeList.Read(p,out var parsed));Assert.Single(parsed!.Files);Assert.Equal(8,parsed.Errors.Count);Assert.Contains("duplicate of line 12",parsed.Errors[^1].Reason);
            var fs=new ListFs();fs.Stats[file.Path]=Info(file);var run=Purge.Apply(new[]{Result(parsed.Files.ToArray())},parsed.Days,parsed.MinBytes,false,fs,()=>DateTime.UtcNow,null,CancellationToken.None);Assert.Equal("would delete",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal(1,fs.StatCalls);Assert.Equal(new[]{file.Path},fs.StatPaths);Assert.Empty(fs.Opens);var confirmFs=new ListFs();confirmFs.OpenInfos[file.Path]=Info(file);var confirmed=Purge.Apply(new[]{Result(parsed.Files.ToArray())},parsed.Days,parsed.MinBytes,true,confirmFs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("deleted",confirmed.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal(1,confirmFs.Deletes);Assert.Equal(new[]{file.Path,file.Path},confirmFs.Opens);
        }finally{File.Delete(p);}
    }

    [Fact] public void FromListDryRunMatchesConfirmSafetyChecksWithoutOpening()
    {
        var rows = new[] { Row("readonly"), Row("linked", id: 0x123456789ABCDEF1), Row("reparse", id: 0x123456789ABCDEF2), Row("outside", id: 0x123456789ABCDEF3), Row("peer", id: 0x123456789ABCDEF4) }.Select(f => f with { FromList = true }).ToArray();
        var fs = new ListFs();
        fs.Stats[rows[0].Path] = Info(rows[0]) with { IsReadOnly = true };
        fs.Stats[rows[1].Path] = Info(rows[1]) with { LinkCount = 2 };
        fs.Stats[rows[2].Path] = Info(rows[2]) with { IsReparsePoint = true };
        fs.Stats[rows[3].Path] = Info(rows[3]) with { FinalPath = @"C:\outside\file.log" };
        fs.Stats[rows[4].Path] = Info(rows[4]);
        var run = Purge.Apply(new[] { Result(rows) }, 1, 1, false, fs, () => DateTime.UtcNow, null, CancellationToken.None);
        Assert.Equal(new[] { "skipped: read-only", "skipped: hard-linked", "skipped: not a regular file", "skipped: outside target", "would delete" }, run.Results[0].Largest!.Files.Select(f => f.PurgeStatus));
        Assert.Empty(fs.Opens);
    }

    [Fact] public void EmptyTargetAndNonUtcTimesAreReportedAsBadRows()
    {
        var valid = Row("peer");
        string Make(string target, string time, string path) => $"{target}\t{valid.VolumeSerial:X8}\t{valid.FileId:X16}\t{valid.LogicalBytes}\t{time}\t{path}";
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, $"{PurgeList.HeaderV1}\r\n# purge days=1 minsize=1\r\n{Make("", Old.ToString("O"), "empty-target")}\r\n{Make(@"C:\\logs", "2020-01-01T00:00:00.0000000+01:00", "offset-time")}\r\n{Make(@"C:\\logs", Old.ToString("O"), valid.Path)}\r\n");
            Assert.True(PurgeList.Read(p, out var parsed));
            Assert.Equal(new[] { "bad target", "bad modification time" }, parsed!.Errors.Select(e => e.Reason));
            Assert.Single(parsed.Files);
        }
        finally { File.Delete(p); }
    }

    [Fact] public void M4_M8_DryRunStatsOnlyListRowsAndLeavesScanRowsAlone()
    { var l=Row("listed");var s=Row("scan");var r=Result(l with{FromList=true},s);var fs=new ListFs();fs.Stats[l.Path]=Info(l);var run=Purge.Apply(new[]{r},1,1,false,fs,()=>DateTime.UtcNow,null,CancellationToken.None);Assert.Equal("would delete",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal("would delete",run.Results[0].Largest!.Files[1].PurgeStatus);Assert.Equal(new[]{l.Path},fs.StatPaths);Assert.Empty(fs.Opens); }

    [Theory]
    [InlineData("volume")][InlineData("id")][InlineData("size")][InlineData("time")]
    public void N1_N2_N4_MismatchFailsSafeAndMatchingPeerIsProcessed(string mismatch)
    { var bad=Row("bad");var peer=Row("peer",id:bad.FileId!.Value+1);var fs=new ListFs();fs.Stats[bad.Path]=mismatch switch{"volume"=>Info(bad,serial:bad.VolumeSerial!.Value+1),"id"=>Info(bad,id:bad.FileId!.Value+1),"size"=>Info(bad,bytes:bad.LogicalBytes+1),_=>Info(bad,time:Old.AddTicks(1))};fs.Stats[peer.Path]=Info(peer);var result=Result(bad with{FromList=true},peer with{FromList=true});var run=Purge.Apply(new[]{result},1,1,false,fs,()=>DateTime.UtcNow,null,CancellationToken.None);Assert.Contains(mismatch switch{"volume"=>"(volume)","id"=>"(id)",_=>"(size or time)"},run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal("would delete",run.Results[0].Largest!.Files[1].PurgeStatus); }

    [Theory][InlineData("size")][InlineData("time")]
    public void N2_ConfirmRejectsMetadataChangeAndDeletesMatchingPeer(string mismatch)
    { var bad=Row("changed");var peer=Row("peer",id:bad.FileId!.Value+1);var fs=new ListFs();fs.OpenInfos[bad.Path]=mismatch=="size"?Info(bad,bytes:bad.LogicalBytes+1):Info(bad,time:Old.AddTicks(1));fs.OpenInfos[peer.Path]=Info(peer);var result=Result(bad with{FromList=true},peer with{FromList=true});var run=Purge.Apply(new[]{result},1,1,true,fs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("skipped: file changed since list (size or time)",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal("deleted",run.Results[0].Largest!.Files[1].PurgeStatus);Assert.Equal(1,fs.Deletes);}

    [Fact] public void N4_ConfirmRejectsHandleVolumeMismatchAndDeletesMatchingPeer()
    { var changed=Row("changed");var peer=Row("peer",id:changed.FileId!.Value+1);var fs=new ListFs();fs.OpenInfos[changed.Path]=Info(changed,serial:changed.VolumeSerial!.Value+1);fs.OpenInfos[peer.Path]=Info(peer);var result=Result(changed with{FromList=true},peer with{FromList=true});var run=Purge.Apply(new[]{result},1,1,true,fs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("skipped: file changed since list (volume)",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal("deleted",run.Results[0].Largest!.Files[1].PurgeStatus);Assert.Equal(1,fs.Deletes);}

    [Fact] public void N4_NullHandleVolumeSerialFailsSafe()
    { var bad=Row("changed");var peer=Row("peer",id:bad.FileId!.Value+1);var fs=new ListFs();fs.OpenInfos[bad.Path]=new PurgeFileInfo(bad.Path,bad.LogicalBytes,Old,false,false,1){FileIndex=bad.FileId};fs.OpenInfos[peer.Path]=Info(peer);var run=Purge.Apply(new[]{Result(bad with{FromList=true},peer with{FromList=true})},1,1,true,fs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("skipped: file changed since list (volume)",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal("deleted",run.Results[0].Largest!.Files[1].PurgeStatus);}

    [Fact] public void N1_ConfirmRejectsHandleIdMismatchAndDeletesMatchingPeer()
    { var changed=Row("changed");var peer=Row("peer",id:changed.FileId!.Value+1);var fs=new ListFs();fs.OpenInfos[changed.Path]=Info(changed,id:changed.FileId!.Value+2);fs.OpenInfos[peer.Path]=Info(peer);var run=Purge.Apply(new[]{Result(changed with{FromList=true},peer with{FromList=true})},1,1,true,fs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("skipped: file changed since list (id)",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal("deleted",run.Results[0].Largest!.Files[1].PurgeStatus);Assert.Equal(1,fs.Deletes);}

    [Fact] public void N1_NullHandleFileIndexFailsSafe()
    { var bad=Row("changed");var peer=Row("peer",id:bad.FileId!.Value+1);var fs=new ListFs();fs.OpenInfos[bad.Path]=new PurgeFileInfo(bad.Path,bad.LogicalBytes,Old,false,false,1){VolumeSerial=bad.VolumeSerial};fs.OpenInfos[peer.Path]=Info(peer);var run=Purge.Apply(new[]{Result(bad with{FromList=true},peer with{FromList=true})},1,1,true,fs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("skipped: file changed since list (id)",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal("deleted",run.Results[0].Largest!.Files[1].PurgeStatus);Assert.Equal(1,fs.Deletes);}

    [Fact] public void M2_ConfirmUsesOpenedHandleIdentityAndListThresholds()
    { var f=Row("listed",bytes:500_000_000);var fs=new ListFs();fs.OpenInfos[f.Path]=Info(f);fs.OpenInfos[f.Path+"#verify"]=new PurgeFileInfo(f.Path,0,Old,false,false,1);var res=Result(f with{FromList=true});var run=Purge.Apply(new[]{res},1,100_000_000,true,fs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("deleted",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal(1,fs.Deletes); }

    [Fact] public void M11_ListReportsHaveNoVolumeLineOrHtmlVolumeTables()
    { var f=Row("listed") with{FromList=true,PurgeStatus="would delete"};var r=Result(f) with{Volume=new Volume("SECRET-VOLUME",1,2,3,33)};using var text=new StringWriter();new TextRenderer().Render(text,new[]{r});Assert.DoesNotContain("SECRET-VOLUME",text.ToString());using var html=new StringWriter();new HtmlRenderer(false,85,90,50).Render(html,new[]{r});Assert.DoesNotContain("SECRET-VOLUME",html.ToString());Assert.DoesNotContain("<h2>Nodes</h2>",html.ToString());Assert.DoesNotContain("<h2>Volumes</h2>",html.ToString());Assert.DoesNotContain("<section>",html.ToString());Assert.Contains("Largest candidate files",html.ToString()); }

    [Fact] public void M7_ParserEnforcesFromListSwitchContract()
    { const string message="/fromlist can only be combined with /purge, /confirm, /html, /output, /json and /csv";foreach(var arg in new[]{new[]{"/fromlist","f","/purge",@"C:\logs"},new[]{"/fromlist","f","/purge","/file","x"},new[]{"/fromlist","f","/purge","/largest","*.log"},new[]{"/fromlist","f","/purge","1"},new[]{"/fromlist","f","/purge","/minsize","1MB"},new[]{"/fromlist","f","/purge","/timeout","2"},new[]{"/fromlist","f","/purge","/savelist","x"}})Assert.Equal(message,ArgParser.Parse(arg).Error);Assert.Equal("/fromlist requires /purge",ArgParser.Parse(new[]{"/fromlist","f"}).Error);Assert.Null(ArgParser.Parse(new[]{"/fromlist","f","/purge"}).Error);Assert.Equal("/savelist requires /purge",ArgParser.Parse(new[]{@"C:\x","/largest","*.log","/savelist","f"}).Error);Assert.Equal("/savelist cannot be used with /confirm",ArgParser.Parse(new[]{@"C:\x","/largest","*.log","/purge","/confirm","/savelist","f"}).Error);Assert.Equal("/savelist cannot be used with /summary",ArgParser.Parse(new[]{@"C:\x","/largest","*.log","/purge","/summary","/savelist","f"}).Error); }

    [Fact]
    public void M6_SaveListWritesWouldDeleteAfterStoppingRowsAndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "delete.txt");
        try
        {
            var f1 = Row("stopping.log") with { PurgeStatus = "would delete after stopping tomcat85" };
            var f2 = new LargestFile(@"D:\data\held.log", 300_000_000, Old) { VolumeSerial = 0x87654321, FileId = 0xFEDCBA9876543210, PurgeStatus = "would delete after stopping tomcat9" };
            var r1 = Result(f1);
            var r2 = new ScanResult(@"D:\data", new Volume("D:", 0, 0, 0, 0), new(), new(), new(), true) { ScanRoot = @"D:\data", Largest = new LargestFiles(Array.Empty<string>(), 1, f2.LogicalBytes, new List<LargestFile> { f2 }, true) };

            var summary = PurgeList.Summarize(new[] { r1, r2 });
            Assert.Equal(2, summary.SavedFiles);
            Assert.Equal(0, summary.NoIdFiles);
            Assert.Equal(f1.LogicalBytes + f2.LogicalBytes, summary.SavedBytes);

            PurgeList.Write(path, new[] { r1, r2 }, 1, 1024, "v0.0.23", new[] { "/largest", "*.log", "/purge" });

            Assert.True(PurgeList.Read(path, out var parsed));
            Assert.NotNull(parsed);
            Assert.Equal(2, parsed.Files.Count);
            Assert.Empty(parsed.Errors);
            Assert.Equal(@"C:\logs", parsed.Files[0].ListTarget);
            Assert.Equal(f1.Path, parsed.Files[0].Path);
            Assert.Equal(@"D:\data", parsed.Files[1].ListTarget);
            Assert.Equal(f2.Path, parsed.Files[1].Path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void M1_WritesV2HeaderCreatedColumnAndLocalTimeWithOffset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC-08", TimeSpan.FromHours(-8), "UTC-08", "UTC-08");
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "delete.txt");
        try
        {
            var utcTime = new DateTime(2026, 11, 20, 16, 8, 16, DateTimeKind.Utc).AddTicks(126039);
            var file = Row("file.log", time: utcTime) with { PurgeStatus = "would delete" };
            var r = Result(file);
            PurgeList.Write(path, new[] { r }, 7, 1024, "v0.0.24", new[] { "/largest", "*.log", "/purge" }, zone);
            var lines = File.ReadAllLines(path);
            Assert.Equal("# dirused delete list v2", lines[0]);
            Assert.Equal("# purge days=7 minsize=1024", lines[1]);
            Assert.Equal("# tool version=v0.0.24", lines[2]);
            Assert.StartsWith("# created=", lines[3]);
            Assert.EndsWith("-08:00", lines[3]);
            Assert.Equal("# args=/largest *.log /purge", lines[4]);
            Assert.Equal("# target\tvolumeSerial\tfileId\tbytes\tlastWrite\tpath", lines[5]);
            Assert.Contains("2026-11-20 08:08:16.0126039 -08:00", lines[6]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void M2_N1_RoundTripPreservesTicksAndUtcKindAcrossDstAndPrecision()
    {
        var dstRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)
        );
        var writerZone = TimeZoneInfo.CreateCustomTimeZone("PacificTest", TimeSpan.FromHours(-8), "PacificTest", "Standard", "Daylight", new[] { dstRule });

        var summerUtc = new DateTime(2026, 7, 15, 18, 30, 45, DateTimeKind.Utc).AddTicks(1234567);
        var winterUtc = new DateTime(2026, 11, 20, 16, 8, 16, DateTimeKind.Utc);

        var f1 = Row("summer.log", id: 0x1111111111111111, time: summerUtc) with { PurgeStatus = "would delete" };
        var f2 = Row("winter.log", id: 0x2222222222222222, time: winterUtc) with { PurgeStatus = "would delete" };
        var r = Result(f1, f2);

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "delete.txt");
        try
        {
            PurgeList.Write(path, new[] { r }, 1, 1024, "v0.0.24", Array.Empty<string>(), writerZone);
            var lines = File.ReadAllLines(path);
            Assert.Contains(" -07:00\t", lines[6]);
            Assert.Contains(" -08:00\t", lines[7]);

            Assert.True(PurgeList.Read(path, out var parsed));
            Assert.NotNull(parsed);
            Assert.Empty(parsed.Errors);
            Assert.Equal(2, parsed.Files.Count);

            Assert.Equal(DateTimeKind.Utc, parsed.Files[0].LastWriteUtc!.Value.Kind);
            Assert.Equal(summerUtc.Ticks, parsed.Files[0].LastWriteUtc!.Value.Ticks);
            Assert.Equal(summerUtc, parsed.Files[0].LastWriteUtc!.Value);

            Assert.Equal(DateTimeKind.Utc, parsed.Files[1].LastWriteUtc!.Value.Kind);
            Assert.Equal(winterUtc.Ticks, parsed.Files[1].LastWriteUtc!.Value.Ticks);
            Assert.Equal(winterUtc, parsed.Files[1].LastWriteUtc!.Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void M3_ReadsV1ListWithUtcFormatAndSameErrors()
    {
        var valid = Row("valid");
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, $"{PurgeList.HeaderV1}\r\n# purge days=1 minsize=1\r\nC:\\logs\t{valid.VolumeSerial:X8}\t{valid.FileId:X16}\t{valid.LogicalBytes}\t{Old:O}\t{valid.Path}\r\nC:\\logs\t12345678\t0000000000000001\t1\t2026-11-20 08:08:16.0126039 -08:00\tbad-v2-time-in-v1\r\n");
            Assert.True(PurgeList.Read(p, out var parsed));
            Assert.NotNull(parsed);
            Assert.Single(parsed.Files);
            Assert.Equal(valid.Path, parsed.Files[0].Path);
            Assert.Equal(Old, parsed.Files[0].LastWriteUtc);
            Assert.Equal(DateTimeKind.Utc, parsed.Files[0].LastWriteUtc!.Value.Kind);
            Assert.Single(parsed.Errors);
            Assert.Equal("bad modification time", parsed.Errors[0].Reason);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void N2_RejectsMalformedV2TimesWhileReadingValidPeers()
    {
        var valid = Row("valid");
        string Make(string time, string path) => $"C:\\logs\t{valid.VolumeSerial:X8}\t{valid.FileId:X16}\t{valid.LogicalBytes}\t{time}\t{path}";
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, $"{PurgeList.Header}\r\n# purge days=1 minsize=1\r\n"
                + $"{Make("2026-11-20T16:08:16.0126039Z", "v1-in-v2.log")}\r\n"
                + $"{Make("2026-11-20 08:08:16.0126039", "no-offset.log")}\r\n"
                + $"{Make("2026-11-20 08:08:16 -08:00", "no-fraction.log")}\r\n"
                + $"{Make("2026-11-20 08:08:16.0126039 -08:00", valid.Path)}\r\n");
            Assert.True(PurgeList.Read(p, out var parsed));
            Assert.NotNull(parsed);
            Assert.Single(parsed.Files);
            Assert.Equal(valid.Path, parsed.Files[0].Path);
            Assert.Equal(3, parsed.Errors.Count);
            Assert.All(parsed.Errors, e => Assert.Equal("bad modification time", e.Reason));
        }
        finally { File.Delete(p); }
    }

    [Theory]
    [InlineData("# dirused delete list v3", false)]
    [InlineData("# dirused delete list v0", false)]
    [InlineData("# dirused delete list v1", true)]
    [InlineData("# dirused delete list v2", true)]
    public void N3_RejectsUnknownHeadersAndAcceptsV1AndV2(string header, bool expectedSuccess)
    {
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, $"{header}\r\n# purge days=1 minsize=1\r\n");
            Assert.Equal(expectedSuccess, PurgeList.Read(p, out _));
        }
        finally { File.Delete(p); }
    }

    private sealed class ListFs:IPurgeFileSystem
    { public Dictionary<string,PurgeFileInfo> Stats=new();public Dictionary<string,PurgeFileInfo> OpenInfos=new();public List<string> StatPaths=new(),Opens=new();public int StatCalls,Deletes;private readonly HashSet<string> opened=new();public string? ResolveRoot(string p,out string? e){e=null;return p;}public PurgeFileInfo? Stat(string p,out string? e,out bool missing){StatCalls++;StatPaths.Add(p);e=null;missing=false;return Stats.GetValueOrDefault(p);}public PurgeOpen Open(string p){Opens.Add(p);if(!opened.Add(p))return new PurgeOpen("gone",true);if(!OpenInfos.TryGetValue(p,out var info))info=Stats.GetValueOrDefault(p)!;return new PurgeOpen(info,new Handle(()=>{}),()=>Deletes++);}private sealed class Handle(Action close):IDisposable{public void Dispose()=>close();} }
}
