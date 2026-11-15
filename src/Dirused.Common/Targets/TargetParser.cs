namespace Dirused.Common.Targets;
public static class TargetParser
{
    public static Target? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
        {
            var parts = value[2..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            string path = @"\\" + string.Join("\\", parts);
            return new Target(value, parts.Length == 1 ? TargetKind.Node : TargetKind.Unc,
                parts.Length == 1 ? path : @"\\" + parts[0] + @"\" + parts[1] + @"\", path);
        }
        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            if (value.Length > 2 && value[2] != '\\' && value[2] != '/') return null;
            string root = char.ToUpperInvariant(value[0]) + @":\";
            string path = value.Length == 2 ? root : root + value[3..].Replace('/', '\\');
            return new Target(value, path.Length == 3 ? TargetKind.LocalDrive : TargetKind.LocalPath, root, path);
        }
        return new Target(value, TargetKind.LocalPath, "", value.Replace('/', '\\'));
    }
}
