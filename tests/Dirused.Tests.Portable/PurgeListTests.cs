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
            PurgeList.Write(path,new[]{r},7,1024,"test",new[]{"/largest","*.log","/purge","7"});var lines=File.ReadAllLines(path);Assert.StartsWith("# dirused delete list v1",lines[0]);Assert.Equal("# purge days=7 minsize=1024",lines[1]);Assert.Single(lines.Skip(6));Assert.Contains("good",lines[6]);
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
        var p=Path.GetTempFileName();try{File.WriteAllText(p,$"{PurgeList.Header}\r\n# purge days=1 minsize=1\r\n# ignored\r\n\r\nbad\r\nC:\\logs\t00000000\t0000000000000001\t1\t{Old:O}\tx\r\nC:\\logs\t12345678\tFFFFFFFFFFFFFFFF\t1\t{Old:O}\ty\r\nC:\\logs\t12345678\t0000000000000000\t1\t{Old:O}\tzero-id\r\nC:\\logs\tzzzzzzzz\t0000000000000001\t1\t{Old:O}\tbad-hex\r\nC:\\logs\t12345678\t0000000000000001\tnope\t{Old:O}\tbad-number\r\nC:\\logs\t12345678\t0000000000000001\t1\tbad-time\tbad-time\r\n{line}\r\n{line}\r\n");Assert.True(PurgeList.Read(p,out var parsed));Assert.Single(parsed!.Files);Assert.Equal(8,parsed.Errors.Count);Assert.Contains("duplicate of line 12",parsed.Errors[^1].Reason);
            var fs=new ListFs();fs.Stats[file.Path]=Info(file);var run=Purge.Apply(new[]{Result(parsed.Files.ToArray())},parsed.Days,parsed.MinBytes,false,fs,()=>DateTime.UtcNow,null,CancellationToken.None);Assert.Equal("would delete",run.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal(1,fs.StatCalls);Assert.Equal(new[]{file.Path},fs.StatPaths);Assert.Empty(fs.Opens);var confirmFs=new ListFs();confirmFs.OpenInfos[file.Path]=Info(file);var confirmed=Purge.Apply(new[]{Result(parsed.Files.ToArray())},parsed.Days,parsed.MinBytes,true,confirmFs,()=>DateTime.UtcNow,_=>{},CancellationToken.None);Assert.Equal("deleted",confirmed.Results[0].Largest!.Files[0].PurgeStatus);Assert.Equal(1,confirmFs.Deletes);Assert.Equal(new[]{file.Path,file.Path},confirmFs.Opens);
        }finally{File.Delete(p);}
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
    { var f=Row("listed") with{FromList=true,PurgeStatus="would delete"};var r=Result(f) with{Volume=new Volume("SECRET-VOLUME",1,2,3,33)};using var text=new StringWriter();new TextRenderer().Render(text,new[]{r});Assert.DoesNotContain("SECRET-VOLUME",text.ToString());using var html=new StringWriter();new HtmlRenderer(false,85,90,50).Render(html,new[]{r});Assert.DoesNotContain("SECRET-VOLUME",html.ToString());Assert.Contains("Largest candidate files",html.ToString()); }

    [Fact] public void M7_ParserEnforcesFromListSwitchContract()
    { const string message="/fromlist can only be combined with /purge, /confirm, /html, /output, /json and /csv";foreach(var arg in new[]{new[]{"/fromlist","f","/purge",@"C:\logs"},new[]{"/fromlist","f","/purge","/file","x"},new[]{"/fromlist","f","/purge","/largest","*.log"},new[]{"/fromlist","f","/purge","1"},new[]{"/fromlist","f","/purge","/minsize","1MB"},new[]{"/fromlist","f","/purge","/timeout","2"},new[]{"/fromlist","f","/purge","/savelist","x"}})Assert.Equal(message,ArgParser.Parse(arg).Error);Assert.Equal("/fromlist requires /purge",ArgParser.Parse(new[]{"/fromlist","f"}).Error);Assert.Null(ArgParser.Parse(new[]{"/fromlist","f","/purge"}).Error);Assert.Equal("/savelist requires /purge",ArgParser.Parse(new[]{@"C:\x","/largest","*.log","/savelist","f"}).Error);Assert.Equal("/savelist cannot be used with /confirm",ArgParser.Parse(new[]{@"C:\x","/largest","*.log","/purge","/confirm","/savelist","f"}).Error);Assert.Equal("/savelist cannot be used with /summary",ArgParser.Parse(new[]{@"C:\x","/largest","*.log","/purge","/summary","/savelist","f"}).Error); }

    private sealed class ListFs:IPurgeFileSystem
    { public Dictionary<string,PurgeFileInfo> Stats=new();public Dictionary<string,PurgeFileInfo> OpenInfos=new();public List<string> StatPaths=new(),Opens=new();public int StatCalls,Deletes;private readonly HashSet<string> opened=new();public string? ResolveRoot(string p,out string? e){e=null;return p;}public PurgeFileInfo? Stat(string p,out string? e,out bool missing){StatCalls++;StatPaths.Add(p);e=null;missing=false;return Stats.GetValueOrDefault(p);}public PurgeOpen Open(string p){Opens.Add(p);if(!opened.Add(p))return new PurgeOpen("gone",true);if(!OpenInfos.TryGetValue(p,out var info))info=Stats.GetValueOrDefault(p)!;return new PurgeOpen(info,new Handle(()=>{}),()=>Deletes++);}private sealed class Handle(Action close):IDisposable{public void Dispose()=>close();} }
}
