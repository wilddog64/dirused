using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dirused.Common.Renderers;

namespace Dirused.Common;

public static class SchedulePlans
{
    private static readonly Regex IdPattern = new(@"\A[0-9]{8}-[0-9]{4}-[0-9a-f]{4}\z", RegexOptions.Compiled);
    private static readonly string[] PlanFiles = { "plan.txt", "list.txt", "dirused.exe" };

    public static bool IsIntact(string planDir, ScheduleSeams s)
    {
        string root = TrimRoot(s.PlanRoot);
        string parent = Parent(root);
        if (s.Store.IsReparsePoint(parent) || s.Store.IsReparsePoint(root) || s.Store.IsReparsePoint(planDir)) return false;
        foreach (var file in PlanFiles)
            if (s.Store.IsReparsePoint(Join(planDir, file))) return false;
        var paths = new List<string> { root, planDir, Join(planDir, "plan.txt"), Join(planDir, "list.txt"), Join(planDir, "dirused.exe") };
        foreach (var path in paths)
        {
            var acl = s.Security.ReadAcl(path);
            if (!acl.Ok || !string.Equals(acl.OwnerSid, s.UserSid, StringComparison.OrdinalIgnoreCase)) return false;
            if (acl.Entries.Any(e => e.Allow && !string.Equals(e.Sid, s.UserSid, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    public static int List(ScheduleSeams s, TextWriter stdout, TextWriter stderr)
    {
        var rows = new List<(string Id, DateTimeOffset? Run, string Text)>();
        foreach (string id in s.Store.ListDirectoryNames(s.PlanRoot).Where(n => IdPattern.IsMatch(n)))
        {
            string dir = Join(s.PlanRoot, id);
            if (!IsIntact(dir, s)) { rows.Add((id, null, $"{id}  -  -  permissions changed — not run")); continue; }
            var fields = Parse(s.Store.ReadAll(Join(dir, "plan.txt")));
            if (fields == null || !ValidPlan(fields, id, out var run, out var count, out var bytes, out var host, out var source))
            { rows.Add((id, null, $"{id}  -  -  unreadable plan")); continue; }
            string task = TaskName(s, id), claimText = Text(s.Store.ReadAll(Join(dir, "claim")));
            var claim = ParseClaim(claimText);
            string state;
            if (claim?.Kind == "cancel") state = "cancelled";
            else if (claim?.Kind == "run")
            {
                var report = ParseFields(s.Store.ReadAll(Join(dir, "report.txt")));
                if (report != null && report.TryGetValue("outcome", out var outcome))
                {
                    state = outcome;
                    if (outcome == "done")
                    {
                        report.TryGetValue("result", out var result); report.TryGetValue("exit", out var exit);
                        state = $"done ({result ?? ""}, exit {exit ?? ""})";
                    }
                }
                else if (claim.Pid is int pid && string.Equals(s.ProcessImagePath(pid), Join(dir, "dirused.exe"), StringComparison.OrdinalIgnoreCase)) state = "running";
                else
                {
                    var started = TimeZoneInfo.ConvertTime(claim.Time!.Value, Pacific());
                    state = $"interrupted (started {started.ToString("ddd HH:mm", CultureInfo.InvariantCulture)} {PacificAbbreviation(started)}, no report) — check the list and the report folder";
                }
            }
            else if (s.Now() > run.AddMinutes(15)) state = "missed (did not run)";
            else if (!s.Scheduler.Exists(task)) state = "task missing";
            else state = "scheduled";
            var pacificRun = TimeZoneInfo.ConvertTime(run, Pacific());
            string stamp = $"{pacificRun.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {PacificAbbreviation(pacificRun)}";
            rows.Add((id, run, $"{id}  {stamp}  {host}  {state}  {count} file(s) {Format.Size(bytes)}  {SourceName(source)}"));
        }
        if (rows.Count == 0) stdout.WriteLine("No scheduled purges.");
        else foreach (var row in rows.OrderByDescending(x => x.Run.HasValue).ThenByDescending(x => x.Run).ThenByDescending(x => x.Id, StringComparer.Ordinal)) stdout.WriteLine(row.Text);
        return 0;
    }

    public static int Cancel(string id, ScheduleSeams s, TextWriter stdout, TextWriter stderr)
    {
        string dir = Join(s.PlanRoot, id);
        if (!IdPattern.IsMatch(id) || !s.Store.Exists(dir)) { stderr.WriteLine($"unknown plan id: {id}"); return 1; }
        if (!IsIntact(dir, s)) { stderr.WriteLine("refused: plan folder permissions changed"); return 1; }
        string claimPath = Join(dir, "claim");
        var cancelTime = TimeZoneInfo.ConvertTime(s.Now(), Pacific());
        var write = s.Store.WriteNew(claimPath, Encoding.UTF8.GetBytes("cancel " + cancelTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + "\n"));
        if (!write.Ok)
        {
            var old = ParseClaim(Text(s.Store.ReadAll(claimPath)));
            if (old?.Kind == "run") { stderr.WriteLine($"{id} already started; it cannot be cancelled"); return 1; }
            if (old?.Kind == "cancel") { stdout.WriteLine($"{id} is already cancelled"); return 0; }
            stderr.WriteLine($"error: could not cancel {id}: {write.Error}"); return 3;
        }
        var deleted = s.Scheduler.Delete(TaskName(s, id));
        if (!deleted.Ok) { stderr.WriteLine($"error: could not delete task for {id}: {deleted.Error}; the plan is marked cancelled and will not run"); return 3; }
        var log = s.Store.AppendLog("schedule-cancel " + id);
        if (!log.Ok) stderr.WriteLine($"warning: could not write the purge log: {log.Error}");
        stdout.WriteLine($"Cancelled {id}."); return 0;
    }

    private static Dictionary<string, string>? Parse(byte[]? bytes)
    {
        if (bytes == null) return null;
        try
        {
            var lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
            if (lines.Length == 0 || lines[0] != "# dirused plan v1") return null;
            return ParseLines(lines);
        }
        catch (DecoderFallbackException) { return null; }
    }
    private static Dictionary<string, string>? ParseFields(byte[]? bytes)
    {
        if (bytes == null) return null;
        try { return ParseLines(new UTF8Encoding(false, true).GetString(bytes).Split('\n').Select(x => x.TrimEnd('\r')).ToArray()); }
        catch (DecoderFallbackException) { return null; }
    }
    private static Dictionary<string, string> ParseLines(string[] lines)
    {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            result["_header"] = "# dirused plan v1";
            foreach (var line in lines)
            {
                if (!line.StartsWith("# ", StringComparison.Ordinal)) continue;
                int colon = line.IndexOf(':', 2); if (colon > 2) result[line[2..colon]] = line[(colon + 1)..].TrimStart();
            }
            return result;
    }
    private static bool ValidPlan(Dictionary<string, string> f, string id, out DateTimeOffset run, out long count, out long bytes, out string host, out string source)
    {
        run = default; count = bytes = 0; host = source = "";
        if (!f.TryGetValue("id", out var fid) || fid != id || !f.TryGetValue("run", out var rt) || !DateTimeOffset.TryParse(rt, CultureInfo.InvariantCulture, DateTimeStyles.None, out run) || !f.TryGetValue("files", out var c) || !long.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 0 || !f.TryGetValue("bytes", out var b) || !long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out bytes) || bytes < 0 || !f.TryGetValue("host", out var h) || string.IsNullOrWhiteSpace(h) || !f.TryGetValue("source", out var src) || string.IsNullOrWhiteSpace(src)) return false;
        host = h; source = src; return true;
    }
    private sealed record Claim(string Kind, DateTimeOffset? Time, int? Pid);
    private static Claim? ParseClaim(string text)
    {
        var p = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 2 && p[0] == "cancel" && DateTimeOffset.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ct)) return new("cancel", ct, null);
        if (p.Length >= 4 && p[0] == "run" && p[2] == "pid" && DateTimeOffset.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var rt) && int.TryParse(p[3], out var pid)) return new("run", rt, pid);
        return null;
    }
    private static string Text(byte[]? data) => data == null ? "" : Encoding.UTF8.GetString(data);
    private static string TaskName(ScheduleSeams s, string id) => $"\\dirused\\{s.UserSid}\\purge-{id}";
    private static string Join(string a, string b) => a.TrimEnd('\\') + "\\" + b;
    private static string TrimRoot(string s) => s.Length == 3 && s[1] == ':' ? s : s.TrimEnd('\\');
    private static string Parent(string path) { int i = path.TrimEnd('\\').LastIndexOf('\\'); return i <= 2 ? path[..3] : path[..i]; }
    private static string SourceName(string s) => s.Split('\\').Last();
    private static TimeZoneInfo Pacific() { try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } }
    private static string PacificAbbreviation(DateTimeOffset t) => Pacific().IsDaylightSavingTime(t) ? "PDT" : "PST";
}
