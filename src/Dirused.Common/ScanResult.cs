namespace Dirused.Core;

using Dirused.Common;

public record Entry(string Path, long LogicalBytes, long OnDiskBytes);
public record ScanError(string Path, string Reason);
public record Volume(string Drive, long UsedBytes, long FreeBytes, long TotalBytes, double PercentUsed)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long ClusterSize { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public uint SerialNumber { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string FileSystem { get; init; } = "";
}
public record ScanResult(string Target, Volume Volume, List<Entry> Directories, List<Entry> Files, List<ScanError> Errors, bool Complete, bool Exact = false)
{
    public List<string> SystemOnly { get; init; } = new();
    [System.Text.Json.Serialization.JsonConverter(typeof(Dirused.Common.Renderers.FullnessLevelJsonConverter))]
    public Dirused.Common.Renderers.FullnessLevel? Level { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TopFoldersNote { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LargestFiles? Largest { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ScanRoot { get; init; }
}
