namespace Dirused.Common;

public enum OutputFormat { Text, Json, Csv }
public record Options(IReadOnlyList<string> Targets, int Top = 50, OutputFormat Format = OutputFormat.Text,
    string? File = null, bool Summary = false, TimeSpan? Timeout = null, int? Parallel = null);
public record ParseResult(Options? Options = null, string? Error = null, bool Usage = false);
