using System.Text.Json;

namespace Dirused.Common.Update;

public sealed record UpdateState(DateTimeOffset? AttemptedUtc, string? Latest, DateTimeOffset? NoticedUtc, string? NoticedVersion = null)
{
    public static UpdateState Read(string path, DateTimeOffset now)
    {
        try { var state = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path)); if (state == null || state.AttemptedUtc > now || state.NoticedUtc > now) return new(null, null, null); return state; }
        catch { return new(null, null, null); }
    }
    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, path, true); }
        finally { try { File.Delete(temp); } catch { } }
    }
}
