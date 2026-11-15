using System.Globalization;
using Dirused.Core;
namespace Dirused.Common.Renderers;
public static class Format
{
    public static string Size(long n) { string[] units = { "B", "KB", "MB", "GB", "TB", "PB" }; double value = n; int i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; } return $"{value:F1} {units[i]}"; }
    public static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    public static void Row(TextWriter w, string kind, string target, Entry e) => w.WriteLine($"{kind},{Quote(target)},{Quote(e.Path)},{e.LogicalBytes.ToString(CultureInfo.InvariantCulture)},{e.OnDiskBytes.ToString(CultureInfo.InvariantCulture)}");
}
