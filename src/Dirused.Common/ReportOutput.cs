using System.Security;
using System.Text;

namespace Dirused.Common;

public static class ReportOutput
{
    public static bool TryOpen(string path, out StreamWriter? writer, out string fullPath, out string? error)
    {
        writer = null;
        fullPath = "";
        error = null;
        try
        {
            fullPath = Path.GetFullPath(path);
            string? parent = Path.GetDirectoryName(fullPath);
            if (parent != null) Directory.CreateDirectory(parent);
            writer = new StreamWriter(fullPath, append: false, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            error = $"Cannot write report '{path}': {ex.Message}";
            return false;
        }
    }
}
