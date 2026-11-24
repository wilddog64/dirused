using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dirused.Common.Renderers;

namespace Dirused.Common;

internal sealed record PreviewExtras(string Sha12, string TaskName, string PlanDir, string ExePath);

public static class ScheduleCommands
{
    public static int Schedule(ScheduleRequest req, ScheduleSeams s, TextWriter stdout, TextWriter stderr, TextReader input, bool inputRedirected)
    {
        if (!PurgeList.Parse(req.ListBytes, out var list) || list == null) { stderr.WriteLine($"not a dirused delete list: {req.ListPathAsTyped}"); return 1; }
        DateTimeOffset created = Truncate(s.Now());
        var pacific = Pacific();
        if (!Dirused.Common.Schedule.TryRunTime(req.Options, created, s.Local, stderr, out var resolvedRun)) return 1;
        var pacificRun = TimeZoneInfo.ConvertTime(resolvedRun, pacific);
        string id = MakeId(pacificRun, s.IdSuffix());
        string planDir = Join(s.PlanRoot, id);
        string taskName = $@"\dirused\{s.UserSid}\purge-{id}";
        string shaList = Hex(SHA256.HashData(req.ListBytes));
        var previewExtras = new PreviewExtras(shaList[..12], taskName, planDir, s.ExePath);
        int previewCode = Dirused.Common.Schedule.Preview(list, req.ListPathAsTyped, created, s.Local, req.Options, stdout, stderr, previewExtras, out var runUtc, out var host);
        if (previewCode == 1) return 1;
        var run = TimeZoneInfo.ConvertTime(runUtc, pacific);
        var made = TimeZoneInfo.ConvertTime(created, pacific);
        var expires = TimeZoneInfo.ConvertTime(created.AddHours(24), pacific);
        if (!req.Options.Confirm)
        {
            stdout.WriteLine("Preview only. Add /confirm to schedule it.");
            return list.Errors.Count > 0 ? 3 : 0;
        }
        if (list.Errors.Count > 0) { stderr.WriteLine("/schedule: fix the list first; nothing was scheduled"); return 3; }
        if (s.Token == TokenLevel.High) { stderr.WriteLine("/schedule: run from a normal (not elevated) window"); return 1; }
        if (!req.Options.Yes)
        {
            stderr.Write("Schedule this? [y/N] ");
            string? answer = inputRedirected ? null : input.ReadLine();
            if (answer == null || !(answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))) { stdout.WriteLine("Not scheduled."); return 0; }
        }
        if (runUtc - s.Now() < TimeSpan.FromMinutes(2)) { stderr.WriteLine($"/schedule: {req.Options.ScheduleTime} is less than 2 minutes away; pick a later time"); return 1; }
        var trusted = s.Trust.Load();
        var trust = PlanTrust.Check(s.ExePath, s.PlanRoot, s.UserSid, trusted, s.Store, s.Security);
        if (!trust.Ok) { foreach (var line in trust.Lines) stderr.WriteLine(line); return 1; }

        string plansParent = Parent(s.PlanRoot);
        foreach (var folder in new[] { plansParent, s.PlanRoot })
        {
            bool createdFolder = false;
            if (!s.Store.Exists(folder))
            {
                var create = s.Store.CreateDirectory(folder);
                if (!create.Ok) { stderr.WriteLine("error: could not secure plan folder: " + create.Error); return 3; }
                createdFolder = true;
            }
            var secured = s.Security.Secure(folder, s.UserSid);
            if (!secured.Ok) { if (createdFolder) s.Store.DeleteTree(folder); stderr.WriteLine("error: could not secure plan folder: " + secured.Error); return 3; }
        }

        int draws = 1;
        while (true)
        {
            planDir = Join(s.PlanRoot, id);
            taskName = $@"\dirused\{s.UserSid}\purge-{id}";
            var createdPlan = s.Store.CreateDirectory(planDir);
            if (createdPlan.Ok) break;
            bool collision = s.Store.Exists(planDir);
            if (collision && draws < 5) { id = MakeId(run, s.IdSuffix()); draws++; continue; }
            stderr.WriteLine(collision ? "error: could not write plan: no free plan id" : "error: could not write plan: " + createdPlan.Error); return 3;
        }
        if (s.Store.IsReparsePoint(planDir)) { s.Store.DeleteTree(planDir); stderr.WriteLine($"error: could not secure plan folder: {planDir} is a link"); return 3; }
        var securePlan = s.Security.Secure(planDir, s.UserSid);
        if (!securePlan.Ok) { s.Store.DeleteTree(planDir); stderr.WriteLine("error: could not secure plan folder: " + securePlan.Error); return 3; }
        var exe = s.Store.ReadAll(s.ExePath);
        if (exe == null) { s.Store.DeleteTree(planDir); stderr.WriteLine($"error: could not write plan: cannot read {s.ExePath}"); return 3; }
        long total = list.Files.Sum(x => x.LogicalBytes);
        string size = Format.Size(total);
        string sourceName = req.ListFullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? req.ListFullPath;
        var task = new PlanTask(id, run, expires, s.UserSid, planDir, $"dirused scheduled purge {id} ({list.Files.Count} file(s), {size}) from {sourceName}");
        string prefix = planDir + "\\";
        var files = new (string Name, byte[] Bytes)[]
        {
            ("dirused.exe", exe), ("list.txt", req.ListBytes), ("task.xml", TaskXml.Build(task)),
            ("plan.txt", Encoding.UTF8.GetBytes(PlanText(id, made, run, expires, s.ToolVersion, exe, req.ListBytes, list.Files.Count, total, host, req.ListFullPath, trusted)))
        };
        foreach (var file in files)
        {
            var written = s.Store.WriteNew(prefix + file.Name, file.Bytes);
            if (!written.Ok) { s.Store.DeleteTree(planDir); stderr.WriteLine("error: could not write plan: " + written.Error); return 3; }
        }
        var registration = s.Scheduler.Register(taskName, prefix + "task.xml", task);
        if (!registration.Ok) { s.Store.DeleteTree(planDir); stderr.WriteLine("error: " + registration.Error); return 3; }
        string log = $"schedule {id} run {run.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)} files {list.Files.Count} bytes {total}";
        var logged = s.Store.AppendLog(log);
        if (!logged.Ok) stderr.WriteLine("warning: could not write the purge log: " + logged.Error);
        string shownRun = RunDisplay(runUtc, run);
        stdout.WriteLine($"Scheduled {id} for {shownRun}. Check it with: dirused /schedule /list");
        return 0;
    }

    private static string PlanText(string id, DateTimeOffset created, DateTimeOffset run, DateTimeOffset expires, string version, byte[] exe, byte[] list, int count, long total, string host, string source, IReadOnlyList<TrustedAccount> trusted)
    {
        string F(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        string trust = string.Join(",", trusted.Select(x => x.Sid).OrderBy(x => x, StringComparer.Ordinal));
        return "# dirused plan v1\n" + $"# id: {id}\n# created: {F(created)}\n# run: {F(run)}\n# expires: {F(expires)}\n# tool: {version}\n# exe-sha256: {Hex(SHA256.HashData(exe))}\n# list-sha256: {Hex(SHA256.HashData(list))}\n# files: {count}\n# bytes: {total}\n# host: {host}\n# source: {source}\n# trusted: {(trust.Length == 0 ? "none" : trust)}\n";
    }
    private static string RunDisplay(DateTimeOffset utc, DateTimeOffset local) => $"{local.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {(Pacific().IsDaylightSavingTime(local) ? "PDT" : "PST")} ({utc.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC {utc.UtcDateTime.ToString("ddd", CultureInfo.InvariantCulture)})";
    private static DateTimeOffset Truncate(DateTimeOffset d) => new(d.Ticks - d.Ticks % TimeSpan.TicksPerSecond, d.Offset);
    private static string MakeId(DateTimeOffset when, int suffix) => when.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" + (suffix & 0xffff).ToString("x4", CultureInfo.InvariantCulture);
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static string Join(string a, string b) => a.TrimEnd('\\') + "\\" + b;
    private static string Parent(string p) { int i = p.TrimEnd('\\').LastIndexOf('\\'); return i <= 2 ? p[..3] : p[..i]; }
    private static TimeZoneInfo Pacific() { try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } }
}
