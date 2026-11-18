using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dirused.Common.Update;

public static class Sha256File
{
    public static string Parse(string text, string expectedName)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[0-9a-fA-F]{64}$") || parts[1] != expectedName) throw new InvalidDataException("Invalid SHA-256 file.");
        return parts[0].ToLowerInvariant();
    }
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
