using System.IO.Enumeration;

namespace Dirused.Common;

public static class Largest
{
    public static readonly IReadOnlyList<string> DefaultPatterns = new[] { "*.log", "*.log.*", "*.out", "*.hprof", "*.phd", "javacore*.txt", "*.dmp", "*.mdmp", "*.trc", "*.etl", "*.bak", "*.bkp", "*.old", "*.orig", "*.backup", "*.tmp", "*.temp", "~$*", "*.~*", "*.swp", "*.$$$", "*.part", "*.partial", "*.crdownload", "*.filepart" };

    public static bool ParsePatterns(string? arg, out IReadOnlyList<string> patterns, out string? error)
    {
        error = null;
        if (arg == null) { patterns = DefaultPatterns; return true; }
        var items = arg.Split(new[] { ';', ',' }, StringSplitOptions.None).Select(x => x.Trim()).Select(x => x.StartsWith('+') ? (Add: true, Value: x[1..].Trim()) : (Add: false, Value: x)).Where(x => x.Value.Length > 0).ToList();
        bool anyAdd = items.Any(x => x.Add), anyPlain = items.Any(x => !x.Add);
        if (anyAdd && anyPlain) { patterns = Array.Empty<string>(); error = "/largest: use + items to add to the defaults, or a plain list to replace them"; return false; }
        if (items.Any(x => x.Value.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) || items.Count == 0)
        { patterns = Array.Empty<string>(); error = "/largest patterns match file names only, e.g. \"*.log;*.bak\""; return false; }
        var list = anyAdd ? DefaultPatterns.Concat(items.Select(x => x.Value)) : items.Select(x => x.Value);
        patterns = list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    public static bool Matches(string fileName, IReadOnlyList<string> patterns) => patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));
}

public sealed record LargestFile(string Path, long LogicalBytes, DateTime? LastWriteUtc);
public sealed record LargestFiles(IReadOnlyList<string> Patterns, long Matches, long MatchedBytes, List<LargestFile> Files, bool Complete);

public sealed class LargestCollector
{
    private readonly int count;
    private readonly IReadOnlyList<string> patterns;
    private readonly PriorityQueue<LargestFile, LargestFile> heap = new(new WorstFirst());
    public long Matches { get; private set; }
    public long MatchedBytes { get; private set; }
    public LargestCollector(int count, IReadOnlyList<string> patterns) { this.count = count; this.patterns = patterns; }
    public void Offer(string path, string name, long bytes, DateTime? lastWriteUtc)
    {
        if (bytes <= 0 || !Largest.Matches(name, patterns)) return;
        Matches++; MatchedBytes = checked(MatchedBytes + bytes);
        var file = new LargestFile(path, bytes, lastWriteUtc);
        if (heap.Count < count) heap.Enqueue(file, file);
        else if (Better(file, heap.Peek())) { heap.Dequeue(); heap.Enqueue(file, file); }
    }
    public LargestFiles Snapshot(bool complete) => new(patterns, Matches, MatchedBytes, heap.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.LogicalBytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList(), complete);
    private static bool Better(LargestFile a, LargestFile b) => a.LogicalBytes > b.LogicalBytes || a.LogicalBytes == b.LogicalBytes && StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path) < 0;
    private sealed class WorstFirst : IComparer<LargestFile>
    {
        public int Compare(LargestFile? a, LargestFile? b) { if (ReferenceEquals(a,b)) return 0; if (a is null) return -1; if (b is null) return 1; int n = a.LogicalBytes.CompareTo(b.LogicalBytes); return n != 0 ? n : StringComparer.OrdinalIgnoreCase.Compare(b.Path, a.Path); }
    }
}
