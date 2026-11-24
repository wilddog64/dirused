using System.Globalization;
using System.Text;
using Dirused.Common.Renderers;
using Dirused.Core;

namespace Dirused.Common;

public sealed record BadPurgeListLine(int Line, string Reason);
public sealed record ParsedPurgeList(int Days, long MinBytes, IReadOnlyList<LargestFile> Files, IReadOnlyList<BadPurgeListLine> Errors);
public sealed record PurgeListSaveSummary(int SavedFiles, long SavedBytes, int NoIdFiles);
public static class PurgeList
{
    public static PurgeListSaveSummary Summarize(IEnumerable<ScanResult> results)
    {
        var rows = results.SelectMany(r => r.Largest?.Files ?? new List<LargestFile>()).Where(f => Purge.IsWouldDelete(f.PurgeStatus)).ToList();
        bool HasIdentity(LargestFile f) => f.FileId.HasValue && f.FileId.Value != 0 && f.FileId.Value != ulong.MaxValue && f.VolumeSerial.HasValue && f.VolumeSerial.Value != 0 && f.LastWriteUtc != null;
        return new(rows.Count(HasIdentity), rows.Where(HasIdentity).Sum(f => f.LogicalBytes), rows.Count(f => !f.FileId.HasValue || f.FileId.Value == 0 || f.FileId.Value == ulong.MaxValue || !f.VolumeSerial.HasValue || f.VolumeSerial.Value == 0));
    }
    public const string Header = "# dirused delete list v3";
    public const string HeaderV2 = "# dirused delete list v2";
    public const string HeaderV1 = "# dirused delete list v1";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff zzz";

    public static string FormatMinSize(long bytes)
    {
        if (bytes > 0 && bytes % (1L << 40) == 0) return (bytes / (1L << 40)).ToString(CultureInfo.InvariantCulture) + "TB";
        if (bytes > 0 && bytes % (1L << 30) == 0) return (bytes / (1L << 30)).ToString(CultureInfo.InvariantCulture) + "GB";
        if (bytes > 0 && bytes % (1L << 20) == 0) return (bytes / (1L << 20)).ToString(CultureInfo.InvariantCulture) + "MB";
        if (bytes > 0 && bytes % (1L << 10) == 0) return (bytes / (1L << 10)).ToString(CultureInfo.InvariantCulture) + "KB";
        return bytes.ToString(CultureInfo.InvariantCulture) + "B";
    }

    public static bool Read(string path, out ParsedPurgeList? parsed)
    {
        try { return Parse(File.ReadAllBytes(path), out parsed); }
        catch { parsed = null; return false; }
    }

    public static bool Parse(byte[] bytes, out ParsedPurgeList? parsed)
    {
        parsed = null;
        string[] lines;
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var parsedLines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null) parsedLines.Add(line);
            lines = parsedLines.ToArray();
        }
        if (lines.Length < 2) return false;
        if (lines[0].StartsWith('\uFEFF')) lines[0] = lines[0][1..];
        bool isV3 = lines[0] == Header;
        bool isV2 = lines[0] == HeaderV2;
        bool isV1 = lines[0] == HeaderV1;
        if (!isV3 && !isV2 && !isV1) return false;
        int days;
        long min;
        if (isV3)
        {
            var m = System.Text.RegularExpressions.Regex.Match(lines[1], @"^# purge days=([0-9]+) minsize=([0-9]+)(B|KB|MB|GB|TB)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out days) || days is < 1 or > 3650) return false;
            if (!long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long num) || num < 1) return false;
            long mult = m.Groups[3].Value.ToUpperInvariant() switch
            {
                "B" => 1L,
                "KB" => 1L << 10,
                "MB" => 1L << 20,
                "GB" => 1L << 30,
                "TB" => 1L << 40,
                _ => 0L
            };
            if (mult == 0L) return false;
            try { min = checked(num * mult); }
            catch (OverflowException) { return false; }
            if (min < 1 || min > (1L << 62)) return false;
        }
        else
        {
            var m = System.Text.RegularExpressions.Regex.Match(lines[1], "^# purge days=([0-9]+) minsize=([0-9]+)$");
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out days) || days is < 1 or > 3650 || !long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out min) || min < 1 || min > (1L << 62)) return false;
        }
        var files = new List<LargestFile>(); var errors = new List<BadPurgeListLine>(); var paths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int n = 2; n < lines.Length; n++)
        {
            string line = lines[n];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var f = line.Split('\t', isV3 ? 7 : 6);
            string? why = null;
            if (isV3 && f.Length < 7) why = "expected 7 fields";
            else if (!isV3 && f.Length < 6) why = "expected 6 fields";
            else if (string.IsNullOrWhiteSpace(f[0])) why = "bad target";
            else if (f[1].Length != 8 || !uint.TryParse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint serial) || serial == 0) why = "bad volume serial";
            else if (f[2].Length != 16 || !ulong.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id) || id == 0 || id == ulong.MaxValue) why = "bad file ID";
            else if (!long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out long fileBytes) || fileBytes < 0) why = "bad size";
            else if (isV3 && string.IsNullOrWhiteSpace(f[4])) why = "bad size display";
            else if (isV1 && (!DateTime.TryParseExact(f[4], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) || time.Kind != DateTimeKind.Utc)) why = "bad modification time";
            else if ((isV2 || isV3) && !DateTimeOffset.TryParseExact(isV3 ? f[5] : f[4], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) why = "bad modification time";
            else if (paths.TryGetValue(isV3 ? f[6] : f[5], out int prior)) why = $"duplicate of line {prior}";
            if (why != null) { errors.Add(new(n + 1, why)); continue; }
            string filePath = isV3 ? f[6] : f[5];
            paths[filePath] = n + 1;
            string timeStr = isV3 ? f[5] : f[4];
            DateTime dt = (isV2 || isV3)
                ? DateTimeOffset.ParseExact(timeStr, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None).UtcDateTime
                : DateTime.ParseExact(timeStr, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            files.Add(new LargestFile(filePath, long.Parse(f[3], CultureInfo.InvariantCulture), dt)
            {
                VolumeSerial = uint.Parse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                FileId = ulong.Parse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                FromList = true,
                ListTarget = f[0]
            });
        }
        parsed = new(days, min, files, errors);
        return true;
    }

    public static void Write(string path, IEnumerable<ScanResult> results, int days, long minBytes, string version, string[] args, TimeZoneInfo? zone = null)
    {
        var targetZone = zone ?? TimeZoneInfo.Local;
        string full = Path.GetFullPath(path), dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(true)))
            {
                w.NewLine = "\r\n";
                w.WriteLine(Header);
                w.WriteLine("# purge days=" + days.ToString(CultureInfo.InvariantCulture) + " minsize=" + FormatMinSize(minBytes));
                w.WriteLine("# tool version=" + version);
                DateTimeOffset created = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, targetZone);
                w.WriteLine("# created=" + created.ToString(TimeFormat, CultureInfo.InvariantCulture));
                w.WriteLine("# args=" + string.Join(" ", args));
                w.WriteLine("# target\tvolumeSerial\tfileId\tbytes\tsize\tlastWrite\tpath");
                foreach (var result in results)
                {
                    foreach (var f in result.Largest?.Files ?? new())
                    {
                        if (!Purge.IsWouldDelete(f.PurgeStatus) || f.FileId == null || f.FileId == 0 || f.FileId == ulong.MaxValue || f.VolumeSerial == null || f.VolumeSerial == 0 || f.LastWriteUtc == null) continue;
                        DateTimeOffset dto = TimeZoneInfo.ConvertTime(new DateTimeOffset(f.LastWriteUtc.Value.Ticks, TimeSpan.Zero), targetZone);
                        w.WriteLine($"{result.ScanRoot ?? result.Target}\t{f.VolumeSerial:X8}\t{f.FileId:X16}\t{f.LogicalBytes.ToString(CultureInfo.InvariantCulture)}\t{Format.Short(f.LogicalBytes)}\t{dto.ToString(TimeFormat, CultureInfo.InvariantCulture)}\t{f.Path}");
                    }
                }
            }
            File.Move(temp, full, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
