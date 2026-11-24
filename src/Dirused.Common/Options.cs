namespace Dirused.Common;

public enum OutputFormat { Text, Json, Csv }
public record Options(IReadOnlyList<string> Targets, int Top = 50, OutputFormat Format = OutputFormat.Text,
    string? File = null, bool Summary = false, TimeSpan? Timeout = null, int? Parallel = null,
    int Deep = 1, bool Exact = false, bool DeepGiven = false, int Warn = 85, int Crit = 90,
    bool Html = false, string? Output = null, IReadOnlyList<string>? Largest = null, int LargestCount = 20,
    bool Purge = false, int PurgeDays = 1, bool Confirm = false, long PurgeMinBytes = 1L << 30,
    bool PurgeNeedsPatterns = false, string? SaveList = null, string? FromList = null, bool Yes = false,
    string? ScheduleTime = null, string? ScheduleZone = null,
    bool ScheduleList = false, string? ScheduleCancelId = null, string? RunPlan = null,
    bool ScheduleTrust = false, string? ScheduleTrustAccount = null, string? ScheduleUntrust = null);
public record ParseResult(Options? Options = null, string? Error = null, bool Usage = false);
