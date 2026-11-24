using System.Globalization;
using Dirused.Common.Renderers;

namespace Dirused.Common;

public static class Schedule
{
    public static string? NotAvailableForm(Options o) => o.RunPlan != null ? "/runplan" : o.ScheduleList ? "/schedule /list" : o.ScheduleCancelId != null ? "/schedule /cancel" : o.ScheduleTrust ? "/schedule /trust" : o.ScheduleUntrust != null ? "/schedule /untrust" : null;
    private const string ConfirmUnavailable = "/schedule /confirm is not available in this version; nothing was scheduled";
    public static int Run(Options options, ParsedPurgeList list, string listPathAsTyped, TextWriter stdout, TextWriter stderr, Func<DateTimeOffset> now, TimeZoneInfo local)
    {
        var rows = list.Files;
        if (rows.Count == 0) { stderr.WriteLine("/schedule: the list has no files to delete"); return 1; }
        var hosts = new List<string>();
        foreach (var row in rows)
        {
            string? host = GetHost(row.Path);
            if (host == null) { stderr.WriteLine($"/schedule: cannot tell which server {row.Path} is on"); return 1; }
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
        }
        if (hosts.Count > 1) { stderr.WriteLine($"/schedule: the list covers more than one host ({string.Join(", ", hosts)}); save one list per host and schedule each"); return 1; }
        var created = now();
        if (!TryRunTime(options, created, local, stderr, out var run)) return 1;
        var expires = created.AddHours(24);
        DateTimeOffset pacificRun = TimeZoneInfo.ConvertTime(run, Pacific());
        DateTimeOffset pacificExpires = TimeZoneInfo.ConvertTime(expires, Pacific());
        string weekdayRun = TimeZoneInfo.ConvertTime(run, TimeZoneInfo.Utc).ToString("ddd", CultureInfo.InvariantCulture);
        string weekdayExpires = TimeZoneInfo.ConvertTime(expires, TimeZoneInfo.Utc).ToString("ddd", CultureInfo.InvariantCulture);
        string suffix = $"{(Pacific().IsDaylightSavingTime(run) ? "PDT" : "PST")} ({run.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC {weekdayRun})";
        stdout.WriteLine("Scheduled purge preview (nothing is scheduled yet)");
        stdout.WriteLine($"  Run:      {pacificRun.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {suffix}, {Relative(run - created)}");
        stdout.WriteLine($"  Expires:  {pacificExpires.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {(Pacific().IsDaylightSavingTime(expires) ? "PDT" : "PST")} ({expires.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC {weekdayExpires})");
        stdout.WriteLine($"  Host:     {hosts[0]}");
        WriteWrappedPath(stdout, listPathAsTyped);
        stdout.WriteLine($"            {rows.Count} file(s), {Format.Size(rows.Sum(x => x.LogicalBytes))} in the list");
        stdout.WriteLine("  Held:     not checked now; files held open at run time are skipped");
        stdout.WriteLine("It runs only while you are logged on to this machine (disconnected is fine).");
        if (!options.Confirm) stdout.WriteLine("Preview only. Add /confirm to schedule it.");
        if (list.Errors.Count > 0) { stderr.WriteLine("Bad list lines:"); foreach (var e in list.Errors) stderr.WriteLine($"  line {e.Line}: {e.Reason}"); }
        if (options.Confirm) { stderr.WriteLine(ConfirmUnavailable); return 1; }
        if (list.Errors.Count > 0) return 3;
        return 0;
    }

    private static bool TryRunTime(Options options, DateTimeOffset created, TimeZoneInfo local, TextWriter error, out DateTimeOffset run)
    {
        run = default;
        string value = options.ScheduleTime ?? "";
        if (!TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var time)) { error.WriteLine("/schedule: time must be HH:MM (00:00-23:59), optionally followed by PT, PST, PDT or UTC"); return false; }
        var pacific = Pacific();
        string? zone = options.ScheduleZone;
        if (zone == null && local.Id != "Pacific Standard Time" && local.Id != "America/Los_Angeles") { error.WriteLine("/schedule: this machine is not on Pacific time; add PT, PST, PDT or UTC to the time"); return false; }
        DateTimeOffset candidate;
        if (zone == "UTC")
        {
            var utcNow = created.UtcDateTime;
            var wall = utcNow.Date.Add(time);
            if (wall <= utcNow) wall = wall.AddDays(1);
            candidate = new DateTimeOffset(wall, TimeSpan.Zero);
        }
        else if (zone is "PST" or "PDT")
        {
            var offset = zone == "PST" ? TimeSpan.FromHours(-8) : TimeSpan.FromHours(-7);
            var wall = TimeZoneInfo.ConvertTime(created, pacific).Date.Add(time);
            candidate = new DateTimeOffset(wall, offset);
            if (candidate <= created) candidate = candidate.AddDays(1);
        }
        else
        {
            var pacificNow = TimeZoneInfo.ConvertTime(created, pacific);
            var wall = pacificNow.Date.Add(time);
            bool rollToTomorrow;
            if (pacific.IsInvalidTime(wall))
            {
                var gapEnd = wall;
                while (pacific.IsInvalidTime(gapEnd)) gapEnd = gapEnd.AddMinutes(1);
                var gapEndInstant = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(gapEnd, pacific));
                rollToTomorrow = gapEndInstant <= created;
            }
            else if (pacific.IsAmbiguousTime(wall))
            {
                var offsets = pacific.GetAmbiguousTimeOffsets(wall);
                var later = new DateTimeOffset(wall, offsets.Min());
                rollToTomorrow = later <= created;
            }
            else
            {
                var today = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, pacific));
                rollToTomorrow = today <= created;
            }
            if (rollToTomorrow) wall = wall.AddDays(1);
            if (pacific.IsInvalidTime(wall)) { error.WriteLine($"/schedule: {wall:yyyy-MM-dd} {value} does not exist in Pacific time (clocks go forward); pick another time"); return false; }
            if (pacific.IsAmbiguousTime(wall)) { error.WriteLine($"/schedule: {wall:yyyy-MM-dd} {value} happens twice in Pacific time; write PST or PDT"); return false; }
            candidate = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, pacific));
        }
        var delta = candidate - created;
        if (delta < TimeSpan.FromMinutes(2)) { error.WriteLine($"/schedule: {value} is less than 2 minutes away; pick a later time"); return false; }
        if (delta > TimeSpan.FromMinutes(23 * 60 + 45)) { error.WriteLine($"/schedule: {value} is too far away; the plan expires 24 h after it is made, so pick a time within 23 h 45 min"); return false; }
        run = candidate;
        return true;
    }
    private static TimeZoneInfo Pacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
    }
    private static string Relative(TimeSpan delta)
    {
        int minutes = (int)Math.Floor(delta.TotalMinutes);
        return minutes < 60 ? $"in {minutes} min" : $"in {minutes / 60} h {minutes % 60} min";
    }
    private static string? GetHost(string path)
    {
        string p = path.Replace('/', '\\');
        if ((p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) && p.Length >= 6 && char.IsAsciiLetter(p[4]) && p[5] == ':') return "this machine";
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return ParseServer(p[8..]);
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return ParseServer(p[2..]);
        return "this machine";
    }
    private static string? ParseServer(string path)
    {
        string server = path.Split('\\')[0];
        return string.IsNullOrEmpty(server) || server is "." or "?" ? null : server;
    }
    private static void WriteWrappedPath(TextWriter w, string path)
    {
        const string prefix = "  List:     ";
        if (prefix.Length + path.Length <= 80) { w.WriteLine(prefix + path); return; }
        for (int i = 0; i < path.Length; i += 68) w.WriteLine((i == 0 ? prefix : "            ") + path.Substring(i, Math.Min(68, path.Length - i)));
    }
}
