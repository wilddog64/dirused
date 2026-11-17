namespace Dirused.Common.Renderers;

public enum FullnessLevel { Ok, Warn, Crit }

public sealed class FullnessLevelJsonConverter : System.Text.Json.Serialization.JsonStringEnumConverter<FullnessLevel>
{
    public FullnessLevelJsonConverter() : base(System.Text.Json.JsonNamingPolicy.CamelCase) { }
}

public static class Fullness
{
    public static bool ShouldColor(TextWriter output, bool color, bool redirected, string? noColor) =>
        color && ReferenceEquals(output, Console.Out) && !redirected && string.IsNullOrEmpty(noColor);

    public static FullnessLevel Of(double percentUsed, int warn, int crit) =>
        percentUsed >= crit ? FullnessLevel.Crit : percentUsed >= warn ? FullnessLevel.Warn : FullnessLevel.Ok;

    public static FullnessLevel? For(Dirused.Core.ScanResult result, int warn, int crit) =>
        result.Complete && result.Volume.TotalBytes > 0 ? Of(result.Volume.PercentUsed, warn, crit) : null;

    public static string Tag(FullnessLevel? level) => level switch
    {
        FullnessLevel.Warn => " [WARN]",
        FullnessLevel.Crit => " [CRIT]",
        _ => ""
    };

    public static void WriteLine(TextWriter output, string line, FullnessLevel? level, bool color)
    {
        bool useColor = ShouldColor(output, color, Console.IsOutputRedirected, Environment.GetEnvironmentVariable("NO_COLOR"));
        if (!useColor || level is not (FullnessLevel.Warn or FullnessLevel.Crit))
        {
            output.WriteLine(line);
            return;
        }
        try
        {
            Console.ForegroundColor = level == FullnessLevel.Warn ? ConsoleColor.Yellow : ConsoleColor.Red;
            output.WriteLine(line);
        }
        finally
        {
            Console.ResetColor();
        }
    }
}
