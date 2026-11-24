using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dirused.Common.Renderers;

namespace Dirused.Common;

public sealed record RunPlanSeams(IPurgeFileSystem Fs, IServiceQuery? Services, Func<string, bool>? IsFixedDrive,
    Func<DateTime> UtcNow, int Pid, string PurgeLogPath, Action<TimeSpan> Sleep);

public static class PlanRunner
{
    private const string StampFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    private static readonly Regex IdPattern = new(@"\A[0-9]{8}-[0-9]{4}-[0-9a-f]{4}\z", RegexOptions.Compiled);

    public static int RunPlan(string id, ScheduleSeams s, RunPlanSeams r, TextWriter stdout, TextWriter stderr)
    {
        string planDir = Join(s.PlanRoot, id);
        string planPath = Join(planDir, "plan.txt"), listPath = Join(planDir, "list.txt"), exePath = Join(planDir, "dirused.exe");
        int Refuse(string line)
        {
            stderr.WriteLine(line);
            s.Store.AppendLog($"runplan {id}: {line}");
            return 1;
        }
        if (!IdPattern.IsMatch(id) || !s.Store.Exists(planPath)) return Refuse($"refused: not a plan id: {id}");
        if (!string.Equals(s.ExePath, exePath, StringComparison.OrdinalIgnoreCase)) return Refuse("refused: not started from the plan's program copy");
        string parent = Parent(s.PlanRoot);
        if (new[] { parent, s.PlanRoot, planDir, planPath, listPath, exePath }.Any(s.Store.IsReparsePoint)) return Refuse("refused: plan folder contains a link");
        if (!SchedulePlans.IsIntact(planDir, s)) return Refuse("refused: plan folder permissions changed");

        byte[]? planBytes = s.Store.ReadAll(planPath);
        var fields = ParsePlan(planBytes, id);
        DateTimeOffset now = Pacific(s.Now());
        if (fields == null) return ReportOutcome("refused: not a dirused plan", 1, null, now, s, r, stderr, id, planDir);

        DateTimeOffset expires = fields.Expires;
        if (s.Now() >= expires) return ReportOutcome("expired", 0, null, now, s, r, stderr, id, planDir);
        DateTimeOffset scheduled = fields.Run;
        if (s.Now() < scheduled.AddMinutes(-1) || s.Now() > scheduled.AddMinutes(15))
        {
            DateTimeOffset shown = Pacific(scheduled);
            return Refuse($"refused: not the scheduled time (scheduled {shown.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {Abbreviation(shown)})");
        }
        string reportPath = Join(planDir, "report.txt");
        if (s.Store.Exists(reportPath)) return Refuse("refused: plan already has a report");

        DateTimeOffset claimNow = Pacific(s.Now());
        string claimText = "run " + claimNow.ToString(StampFormat, CultureInfo.InvariantCulture) + " pid " + r.Pid.ToString(CultureInfo.InvariantCulture) + "\n";
        var claimWrite = s.Store.WriteNew(Join(planDir, "claim"), new UTF8Encoding(false).GetBytes(claimText));
        string started = claimNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        if (!claimWrite.Ok)
        {
            Claim? claim = null;
            for (int i = 0; i < 3; i++)
            {
                claim = ParseClaim(s.Store.ReadAll(Join(planDir, "claim")));
                if (claim != null) break;
                if (i < 2) r.Sleep(TimeSpan.FromMilliseconds(100));
            }
            if (claim?.Kind == "cancel") return RefuseClaim("cancelled", 0);
            if (claim?.Kind == "run") return RefuseClaim("refused: plan already started", 1);
            return RefuseClaim("error: could not claim plan: " + claimWrite.Error, 3);
        }
        int RefuseClaim(string line, int code) { stderr.WriteLine(line); s.Store.AppendLog($"runplan {id}: {line}"); return code; }

        int Report(string outcome, int exit, PurgeRun? run, IEnumerable<string>? purgeLines = null, IEnumerable<string>? errors = null)
        {
            string temp = Join(planDir, "report.tmp");
            if (s.Store.Exists(temp)) s.Store.DeleteTree(temp);
            string report = BuildReport(id, started, Pacific(s.Now()), outcome, exit, run, purgeLines, errors);
            var written = s.Store.WriteNew(temp, new UTF8Encoding(false).GetBytes(report));
            if (!written.Ok) { ReportFailure(written.Error); return exit; }
            var moved = s.Store.MoveNew(temp, reportPath);
            if (!moved.Ok) { ReportFailure(moved.Error); return exit; }
            return exit;
        }
        void ReportFailure(string? error)
        {
            string message = error ?? "unknown error";
            stderr.WriteLine("error: could not write report: " + message);
            s.Store.AppendLog($"runplan {id}: report failed: {message}");
        }

        var purgeLines = new List<string>();
        var errors = new List<string>();
        using var errorWriter = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        try
        {
            var trusted = fields.Trusted == "none" ? Array.Empty<TrustedAccount>() : fields.Trusted.Split(',').Select(x => new TrustedAccount(x, x)).ToArray();
            var trust = PlanTrust.Check(s.ExePath, s.PlanRoot, s.UserSid, trusted, s.Store, s.Security);
            if (!trust.Ok)
            {
                string line = trust.Lines.FirstOrDefault() ?? "/schedule: trust check failed";
                if (line.StartsWith("/schedule: ", StringComparison.Ordinal)) line = line[11..];
                if (line.EndsWith(".", StringComparison.Ordinal)) line = line[..^1];
                return Report("refused: " + line, 1, null);
            }
            if (!HashMatches(s.Store.ReadAll(exePath), fields.ExeHash)) return Report("refused: program copy changed since it was scheduled", 1, null);
            byte[]? listBytes = s.Store.ReadAll(listPath);
            if (!HashMatches(listBytes, fields.ListHash)) return Report("refused: list changed since it was scheduled", 1, null);
            if (listBytes == null || !PurgeList.Parse(listBytes, out var list) || list == null || list.Errors.Count > 0 || list.Files.Count == 0)
                return Report("refused: not a dirused plan", 1, null);

            var run = Purge.Apply(PurgeList.ToResults(list), list.Days, list.MinBytes, true, r.Fs, r.UtcNow,
                line => { purgeLines.Add(line); var result = s.Store.AppendLog(line); if (!result.Ok) throw new IOException(result.Error); },
                CancellationToken.None, r.PurgeLogPath, r.Services, r.IsFixedDrive, control: null, localNow: null, sleep: null,
                input: TextReader.Null, inputRedirected: true, yes: true, error: errorWriter, scheduled: true);
            errors.AddRange(Lines(errorWriter.ToString()));
            foreach (string line in errors) stderr.WriteLine(line);
            int exit = run.ServiceStartFailed || run.Failed > 0 || run.NotAttempted > 0 ? 3 : 0;
            return Report("done", exit, run, purgeLines, errors);
        }
        catch (Exception ex)
        {
            errors.AddRange(Lines(errorWriter.ToString()));
            foreach (string line in errors) stderr.WriteLine(line);
            return Report("refused: error during run: " + ex.Message, 3, null, purgeLines, errors);
        }
    }

    private static int ReportOutcome(string outcome, int exit, PurgeRun? run, DateTimeOffset now, ScheduleSeams s, RunPlanSeams r, TextWriter stderr, string id, string planDir)
    {
        string claimText = "run " + now.ToString(StampFormat, CultureInfo.InvariantCulture) + " pid " + r.Pid.ToString(CultureInfo.InvariantCulture) + "\n";
        var result = s.Store.WriteNew(Join(planDir, "claim"), new UTF8Encoding(false).GetBytes(claimText));
        string started = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        if (!result.Ok)
        {
            Claim? claim = null;
            for (int i = 0; i < 3; i++) { claim = ParseClaim(s.Store.ReadAll(Join(planDir, "claim"))); if (claim != null) break; if (i < 2) r.Sleep(TimeSpan.FromMilliseconds(100)); }
            string line = claim?.Kind == "cancel" ? "cancelled" : claim?.Kind == "run" ? "refused: plan already started" : "error: could not claim plan: " + result.Error;
            stderr.WriteLine(line); s.Store.AppendLog($"runplan {id}: {line}"); return claim?.Kind == "cancel" ? 0 : claim?.Kind == "run" ? 1 : 3;
        }
        string temp = Join(planDir, "report.tmp"), path = Join(planDir, "report.txt");
        if (s.Store.Exists(temp)) s.Store.DeleteTree(temp);
        string report = BuildReport(id, started, Pacific(s.Now()), outcome, exit, run, null, null);
        var write = s.Store.WriteNew(temp, new UTF8Encoding(false).GetBytes(report));
        var moved = write.Ok ? s.Store.MoveNew(temp, path) : write;
        if (!moved.Ok) { string message = moved.Error ?? "unknown error"; stderr.WriteLine("error: could not write report: " + message); s.Store.AppendLog($"runplan {id}: report failed: {message}"); }
        return exit;
    }

    private sealed record PlanFields(string Trusted, DateTimeOffset Run, DateTimeOffset Expires, string ExeHash, string ListHash);
    private static PlanFields? ParsePlan(byte[]? bytes, string id)
    {
        if (bytes == null) return null;
        string[] lines;
        try { lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n').Select(x => x.EndsWith('\r') ? x[..^1] : x).ToArray(); }
        catch (DecoderFallbackException) { return null; }
        if (lines.Length == 0 || lines[0] != "# dirused plan v1") return null;
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1).Where(x => x.StartsWith("# ", StringComparison.Ordinal)))
        {
            int colon = line.IndexOf(':', 2); if (colon < 0) return null;
            string key = line[2..colon], value = line[(colon + 1)..].TrimStart();
            if (!f.TryAdd(key, value)) return null;
        }
        string[] required = { "id", "created", "run", "expires", "exe-sha256", "list-sha256", "files", "bytes", "host", "source", "trusted" };
        if (required.Any(k => !f.TryGetValue(k, out string? v) || string.IsNullOrEmpty(v)) || f["id"] != id ||
            !DateTimeOffset.TryParseExact(f["created"], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created) ||
            !DateTimeOffset.TryParseExact(f["run"], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var run) ||
            !DateTimeOffset.TryParseExact(f["expires"], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) ||
            !long.TryParse(f["files"], NumberStyles.None, CultureInfo.InvariantCulture, out long count) || count < 0 ||
            !long.TryParse(f["bytes"], NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size < 0 ||
            !Regex.IsMatch(f["exe-sha256"], @"\A[0-9a-f]{64}\z") || !Regex.IsMatch(f["list-sha256"], @"\A[0-9a-f]{64}\z") ||
            created >= run || expires.UtcDateTime != created.AddHours(24).UtcDateTime || run > created.AddHours(23).AddMinutes(45)) return null;
        return new PlanFields(f["trusted"], run, expires, f["exe-sha256"], f["list-sha256"]);
    }

    private sealed record Claim(string Kind, DateTimeOffset Time, int? Pid);
    private static Claim? ParseClaim(byte[]? bytes)
    {
        if (bytes == null) return null;
        string[] p; try { p = Encoding.UTF8.GetString(bytes).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries); } catch { return null; }
        if (p.Length >= 2 && p[0] == "cancel" && DateTimeOffset.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ct)) return new("cancel", ct, null);
        if (p.Length >= 4 && p[0] == "run" && p[2] == "pid" && DateTimeOffset.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var rt) && int.TryParse(p[3], out int pid)) return new("run", rt, pid);
        return null;
    }
    private static bool HashMatches(byte[]? bytes, string hash) => bytes != null && string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), hash, StringComparison.Ordinal);
    private static List<string> Lines(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
    private static string BuildReport(string id, string started, DateTimeOffset finished, string outcome, int exit, PurgeRun? run, IEnumerable<string>? purgeLines, IEnumerable<string>? errors)
    {
        using var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        w.WriteLine("# dirused run report v1"); w.WriteLine("# id: " + id); w.WriteLine("# started: " + started); w.WriteLine("# finished: " + finished.ToString(StampFormat, CultureInfo.InvariantCulture)); w.WriteLine("# outcome: " + outcome); w.WriteLine("# exit: " + exit.ToString(CultureInfo.InvariantCulture));
        if (run != null)
        {
            w.WriteLine($"# result: deleted {run.Deleted} file(s), {Format.Size(run.DeletedBytes)} freed; {run.Skipped} skipped; {run.Failed} failed");
            new TextRenderer().Render(w, run.Results);
            if (run.HeldSummary is { Total: > 0 } h)
                w.WriteLine(h.TomcatCount > 0 && h.Service != null ? $"held files: {h.Total} (Tomcat: {h.Service} on {h.Host}, root {h.InstallRoot}: {h.TomcatCount} file(s), {Format.Size(h.TomcatBytes)}; other: {h.OtherCount})" : $"held files: {h.Total} (other: {h.OtherCount})");
        }
        w.WriteLine("--- purge log"); foreach (string line in purgeLines ?? Array.Empty<string>()) w.WriteLine(line);
        w.WriteLine("--- stderr"); foreach (string line in errors ?? Array.Empty<string>()) w.WriteLine(line);
        return w.ToString();
    }
    private static DateTimeOffset Pacific(DateTimeOffset time) { try { return TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")); } }
    private static string Abbreviation(DateTimeOffset time) => PacificZone().IsDaylightSavingTime(time) ? "PDT" : "PST";
    private static TimeZoneInfo PacificZone() { try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } }
    private static string Join(string path, string name) => path.TrimEnd('\\') + "\\" + name;
    private static string Parent(string path) { int i = path.TrimEnd('\\').LastIndexOf('\\'); return i <= 2 ? path[..3] : path[..i]; }
}
