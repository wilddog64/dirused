using System.Globalization;
using Dirused.Core;
namespace Dirused.Common.Renderers;
public static class Format
{
    public static string Size(long n) { string[] units = { "B", "KB", "MB", "GB", "TB", "PB" }; double value = n; int i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; } return $"{value:F1} {units[i]}"; }
    public static string Short(long n)
    {
        if (n < 1024) return $"{n}B";
        string[] units = { "B", "K", "M", "G", "T", "P" };
        double v = n;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        if (v < 9.95)
        {
            double rounded = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            if (rounded < 10)
                return $"{rounded.ToString("0.0", CultureInfo.InvariantCulture)}{units[i]}";
        }
        long intVal = (long)Math.Round(v, MidpointRounding.AwayFromZero);
        return $"{intVal}{units[i]}";
    }
    public static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    public static void Row(TextWriter w, string kind, string target, Entry e) => w.WriteLine($"{kind},{Quote(target)},{Quote(e.Path)},{e.LogicalBytes.ToString(CultureInfo.InvariantCulture)},{e.OnDiskBytes.ToString(CultureInfo.InvariantCulture)}");
}
