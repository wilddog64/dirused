using System.Globalization;
using System.Text;
using Dirused.Core;

namespace Dirused.Common;

public sealed record BadPurgeListLine(int Line, string Reason);
public sealed record ParsedPurgeList(int Days, long MinBytes, IReadOnlyList<LargestFile> Files, IReadOnlyList<BadPurgeListLine> Errors);
public sealed record PurgeListSaveSummary(int SavedFiles, long SavedBytes, int NoIdFiles);
public static class PurgeList
{
    public static PurgeListSaveSummary Summarize(IEnumerable<ScanResult> results)
    {
        var rows = results.SelectMany(r => r.Largest?.Files ?? new List<LargestFile>()).Where(f => f.PurgeStatus == "would delete").ToList();
        bool HasIdentity(LargestFile f) => f.FileId.HasValue && f.FileId.Value != 0 && f.FileId.Value != ulong.MaxValue && f.VolumeSerial.HasValue && f.VolumeSerial.Value != 0 && f.LastWriteUtc != null;
        return new(rows.Count(HasIdentity), rows.Where(HasIdentity).Sum(f => f.LogicalBytes), rows.Count(f => !f.FileId.HasValue || f.FileId.Value == 0 || f.FileId.Value == ulong.MaxValue || !f.VolumeSerial.HasValue || f.VolumeSerial.Value == 0));
    }
    public const string Header = "# dirused delete list v1";
    public static bool Read(string path, out ParsedPurgeList? parsed)
    {
        parsed = null;
        string[] lines;
        try { lines = File.ReadAllLines(path, Encoding.UTF8); } catch { return false; }
        if (lines.Length < 2) return false;
        if (lines[0].StartsWith('\uFEFF')) lines[0] = lines[0][1..];
        if (lines[0] != Header) return false;
        var m = System.Text.RegularExpressions.Regex.Match(lines[1], "^# purge days=([0-9]+) minsize=([0-9]+)$");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 3650 || !long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long min) || min < 1 || min > (1L << 62)) return false;
        var files = new List<LargestFile>(); var errors = new List<BadPurgeListLine>(); var paths = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for (int n=2;n<lines.Length;n++) { string line=lines[n]; if (string.IsNullOrWhiteSpace(line)||line.StartsWith('#')) continue; var f=line.Split('\t',6); string? why=null;
            if(f.Length<6) why="expected 6 fields";
            else if(f[1].Length!=8||!uint.TryParse(f[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint serial)||serial==0) why="bad volume serial";
            else if(f[2].Length!=16||!ulong.TryParse(f[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out ulong id)||id==0||id==ulong.MaxValue) why="bad file ID";
            else if(!long.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out long bytes)||bytes<0) why="bad size";
            else if(!DateTime.TryParseExact(f[4],"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var time)) why="bad modification time";
            else if(paths.TryGetValue(f[5],out int prior)) why=$"duplicate of line {prior}";
            if(why!=null){errors.Add(new(n+1,why));continue;} paths[f[5]]=n+1;
            DateTime.TryParseExact(f[4],"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var dt);
            files.Add(new LargestFile(f[5],long.Parse(f[3],CultureInfo.InvariantCulture),dt){VolumeSerial=uint.Parse(f[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture),FileId=ulong.Parse(f[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture),FromList=true,ListTarget=f[0]});
        }
        parsed=new(days,min,files,errors);return true;
    }
    public static void Write(string path, IEnumerable<ScanResult> results, int days, long minBytes, string version, string[] args)
    {
        string full=Path.GetFullPath(path), dir=Path.GetDirectoryName(full)!; Directory.CreateDirectory(dir); string temp=Path.Combine(dir,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try { using(var w=new StreamWriter(temp,false,new UTF8Encoding(true))){w.NewLine="\r\n";w.WriteLine(Header);w.WriteLine("# purge days="+days.ToString(CultureInfo.InvariantCulture)+" minsize="+minBytes.ToString(CultureInfo.InvariantCulture));w.WriteLine("# tool version="+version);w.WriteLine("# utc="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture));w.WriteLine("# args="+string.Join(" ",args));w.WriteLine("# target\tvolumeSerial\tfileId\tbytes\tlastWriteUtc\tpath"); foreach(var result in results) foreach(var f in result.Largest?.Files??new()){if(f.PurgeStatus!="would delete"||f.FileId==null||f.FileId==0||f.FileId==ulong.MaxValue||f.VolumeSerial==null||f.VolumeSerial==0||f.LastWriteUtc==null)continue; w.WriteLine($"{result.ScanRoot??result.Target}\t{f.VolumeSerial:X8}\t{f.FileId:X16}\t{f.LogicalBytes.ToString(CultureInfo.InvariantCulture)}\t{f.LastWriteUtc.Value.ToString("O",CultureInfo.InvariantCulture)}\t{f.Path}");}} File.Move(temp,full,true); } finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
