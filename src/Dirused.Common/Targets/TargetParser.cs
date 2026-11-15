namespace Dirused.Common.Targets;
public static class TargetParser
{
    public static Target? Parse(string value)
    {
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = value[2..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return new Target(value, parts.Length <= 1 ? TargetKind.Node : TargetKind.Unc,
                parts.Length <= 1 ? value : @"\\" + parts[0] + @"\" + parts[1] + @"\", value);
        }
        if (value == ".") return new Target(value, TargetKind.LocalPath, "", value);
        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
            return new Target(value, value.Length == 3 ? TargetKind.LocalDrive : TargetKind.LocalPath, value[..3], value);
        return null;
    }
}
