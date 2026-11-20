using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Dirused.Core;

namespace Dirused.Common;

public sealed record PurgeFileInfo(string FinalPath, long Length, DateTime LastWriteUtc, bool IsDirectory, bool IsReparsePoint, int LinkCount)
{
    public bool IsReadOnly { get; init; }
    public uint? VolumeSerial { get; init; }
    public ulong? FileIndex { get; init; }
}

public sealed class PurgeOpen : IDisposable
{
    private readonly IDisposable? handle;
    private readonly Action? mark;
    private readonly Action? unmark;
    public PurgeFileInfo? Info { get; }
    public string? Error { get; }
    public bool NotFound { get; }
    public int ErrorCode { get; }
    public PurgeOpen(PurgeFileInfo info, IDisposable handle, Action mark, Action? unmark = null) { Info = info; this.handle = handle; this.mark = mark; this.unmark = unmark; }
    public PurgeOpen(string error, bool notFound = false, int errorCode = 0) { Error = error; NotFound = notFound; ErrorCode = errorCode; }
    public void MarkDelete() => (mark ?? throw new InvalidOperationException(Error ?? "No open handle"))();
    public void UnmarkDelete() => (unmark ?? throw new InvalidOperationException(Error ?? "No open handle"))();
    public void Dispose() => handle?.Dispose();
}

public interface IPurgeFileSystem
{
    string? ResolveRoot(string path, out string? error);
    PurgeOpen Open(string path);
    PurgeFileInfo? Stat(string path, out string? error, out bool notFound) { error = "Stat unavailable"; notFound = false; return null; }
    bool? ProbeHeld(string path, out string? error) { error = "probe unavailable"; return null; }
}

public sealed record HeldFilesSummary(int Total, string? Service, string? Host, string? InstallRoot, int TomcatCount, long TomcatBytes, int OtherCount);

public sealed record PurgeRun(IReadOnlyList<ScanResult> Results, int WouldDelete, int Deleted, int Skipped, int Failed, int NotAttempted, long WouldDeleteBytes, long DeletedBytes)
{
    public int ListErrors { get; init; }
    public IReadOnlyList<BadPurgeListLine> BadListLines { get; init; } = Array.Empty<BadPurgeListLine>();
    public HeldFilesSummary? HeldSummary { get; init; }
    public int ProbeFailures { get; init; }
    public string? FirstProbeError { get; init; }
    public bool ServiceStartFailed { get; init; }
}
public static class PurgeLog { public const string DefaultPath = @"C:\temp\dirused-purge.log"; }

public static class Purge
{
    public static bool IsWouldDelete(string? status) =>
        status == "would delete" || (status != null && status.StartsWith("would delete after stopping ", StringComparison.Ordinal));

    internal static bool PathContainsOrContained(string pathA, string pathB)
    {
        static string[] Parts(string s) => s.Replace('/', '\\').TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var a = Parts(pathA);
        var b = Parts(pathB);
        if (a.Length == 0 || b.Length == 0) return false;
        int min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
        {
            if (!a[i].Equals(b[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static bool IsStrictlyUnder(string path, string root)
    {
        static string[] Parts(string s) => s.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var p = Parts(path); var r = Parts(root);
        if (r.Length == 0 || p.Any(x => x is "." or "..") || r.Any(x => x is "." or "..") || p.Length <= r.Length) return false;
        for (int i = 0; i < r.Length; i++) if (!p[i].Equals(r[i], StringComparison.OrdinalIgnoreCase)) return false;
        bool absolute = path.StartsWith('\\') || path.StartsWith('/') || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/'));
        bool rootAbsolute = root.StartsWith('\\') || root.StartsWith('/') || (root.Length >= 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && (root[2] is '\\' or '/'));
        return absolute && rootAbsolute;
    }

    private sealed class HeldBackRow
    {
        public required LargestFile File { get; set; }
        public required int ResultIndex { get; init; }
        public required int FileIndex { get; init; }
        public required string ScanRoot { get; init; }
        public required string? TargetFinal { get; init; }
        public required string Host { get; init; }
        public required string ServiceKey { get; init; }
        public required ServiceEntry MatchedService { get; init; }
        public required bool IsRecent { get; init; }
    }

    public static PurgeRun Apply(
        IReadOnlyList<ScanResult> results,
        int days,
        long minBytes,
        bool confirm,
        IPurgeFileSystem fs,
        Func<DateTime> utcNow,
        Action<string>? log,
        CancellationToken ct,
        string? purgeLogPath = null,
        IServiceQuery? services = null,
        Func<string, bool>? isFixedDrive = null,
        IServiceControl? control = null,
        Func<DateTime>? localNow = null,
        Action<TimeSpan>? sleep = null,
        TextReader? input = null,
        bool inputRedirected = false,
        bool yes = false,
        TextWriter? error = null)
    {
        localNow ??= () => DateTime.Now;
        sleep ??= t => Thread.Sleep(t);
        input ??= Console.In;
        error ??= Console.Error;
        DateTime cutoff = utcNow().AddDays(-days);
        purgeLogPath ??= PurgeLog.DefaultPath;
        int would = 0, deleted = 0, skipped = 0, failed = 0, notAttempted = 0;
        long wouldBytes = 0, deletedBytes = 0;
        string? stopReason = null;
        var output = new List<ScanResult>();
        int probeFailures = 0;
        string? firstProbeError = null;
        int heldTotal = 0;
        int heldTomcatCount = 0;
        long heldTomcatBytes = 0;
        int heldOtherCount = 0;
        string? heldService = null;
        string? heldHost = null;
        string? heldInstallRoot = null;
        var serviceCache = new Dictionary<string, (IReadOnlyList<ServiceEntry>? List, string? Error)>(StringComparer.OrdinalIgnoreCase);

        if (services != null && control != null)
        {
            var warnedServices = new HashSet<(string Host, string Key)>();
            foreach (var result in results)
            {
                string scanRoot = result.ScanRoot ?? result.Target;
                if (ServiceQueryHelpers.TryMapHostAndLocalPath(scanRoot, isFixedDrive, out string? host, out string? localPath) && host != null && localPath != null)
                {
                    if (!serviceCache.TryGetValue(host, out var cached))
                    {
                        var list = services.ListProcrun(host, out var svcErr);
                        cached = (list, svcErr);
                        serviceCache[host] = cached;
                    }
                    if (cached.List != null)
                    {
                        foreach (var s in cached.List)
                        {
                            if (!s.Running && PathContainsOrContained(s.InstallRoot, localPath))
                            {
                                if (warnedServices.Add((host, s.Key)))
                                {
                                    if (host == ".")
                                    {
                                        error.WriteLine($"warning: {s.Key} is not running; start it with: sc start {s.Key}");
                                    }
                                    else
                                    {
                                        error.WriteLine($"warning: {s.Key} on {host} is not running; start it with: sc \\\\{host} start {s.Key}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        var heldBackRows = new List<HeldBackRow>();

        void RecordHeldClassification(LargestFile f, bool isConfirm, out string? statusText) =>
            RecordHeldClassificationWithDetails(f, isConfirm, out statusText, out _);

        void RecordHeldClassificationWithDetails(LargestFile f, bool isConfirm, out string? statusText, out (string Reason, ServiceEntry? Service, string? Host, string? InstallRoot) classification)
        {
            heldTotal++;
            classification = ServiceQueryHelpers.ClassifyHeldFile(f, fs, services, isFixedDrive, serviceCache);
            if (classification.Reason == "tomcat")
            {
                heldTomcatCount++;
                heldTomcatBytes += f.LogicalBytes;
                heldService ??= classification.Service!.Key;
                heldHost ??= classification.Host;
                heldInstallRoot ??= classification.InstallRoot;
                statusText = isConfirm
                    ? $"failed: in use by {classification.Service!.Key} (stop not available yet)"
                    : $"would delete after stopping {classification.Service!.Key}";
            }
            else
            {
                heldOtherCount++;
                statusText = isConfirm
                    ? $"failed: in use ({classification.Reason})"
                    : $"skipped: in use ({classification.Reason})";
            }
        }

        foreach (var result in results)
        {
            if (result.Largest == null) { output.Add(result); continue; }
            string scanRoot = result.ScanRoot ?? result.Target;
            string? targetFinal = scanRoot;
            string? rootError = null;
            if (confirm)
            {
                try { targetFinal = fs.ResolveRoot(scanRoot, out rootError); }
                catch (Exception ex) { targetFinal = null; rootError = ex.Message; }
            }

            long targetBytes = 0;
            var files = new List<LargestFile>();
            foreach (var file in result.Largest.Files)
            {
                string? status;
                if (stopReason != null) status = "not attempted: " + stopReason;
                else if (ct.IsCancellationRequested) { status = "not attempted: cancelled"; stopReason = "cancelled"; }
                else if (!IsAbsoluteWindowsPath(file.Path)) status = "failed: path is not absolute";
                else if (!IsStrictlyUnder(file.Path, scanRoot) || HasUnsafeName(file.Path)) status = "skipped: outside target";
                else if (IsPurgeLog(file.Path, purgeLogPath)) status = "skipped: purge log";
                else if (file.LogicalBytes < minBytes) status = $"skipped: smaller than {Dirused.Common.Renderers.Format.Size(minBytes)}";
                else if (file.LastWriteUtc == null) status = "skipped: modification time unknown";
                else if (file.LastWriteUtc >= cutoff)
                {
                    if (services != null && ServiceQueryHelpers.IsRedirectLogName(file.Path))
                    {
                        bool? probed = fs.ProbeHeld(file.Path, out _);
                        if (probed == true)
                        {
                            RecordHeldClassificationWithDetails(file, confirm, out status, out var classification);
                            if (IsWouldDelete(status))
                            {
                                would++;
                                wouldBytes += file.LogicalBytes;
                                targetBytes += file.LogicalBytes;
                            }
                            else if (confirm && control != null && classification.Reason == "tomcat")
                            {
                                heldBackRows.Add(new HeldBackRow
                                {
                                    File = file,
                                    ResultIndex = output.Count,
                                    FileIndex = files.Count,
                                    ScanRoot = scanRoot,
                                    TargetFinal = targetFinal,
                                    Host = classification.Host!,
                                    ServiceKey = classification.Service!.Key,
                                    MatchedService = classification.Service!,
                                    IsRecent = true
                                });
                                status = null;
                            }
                            else if (confirm && status != null && status.StartsWith("failed: in use by ", StringComparison.Ordinal))
                            {
                                try { log?.Invoke(LogLine(utcNow(), file.Path, file.LogicalBytes, status)); }
                                catch { stopReason = "log write failed"; }
                            }
                            else
                            {
                                status = $"skipped: modified within {days} day(s)";
                            }
                        }
                        else
                        {
                            status = $"skipped: modified within {days} day(s)";
                        }
                    }
                    else
                    {
                        status = $"skipped: modified within {days} day(s)";
                    }
                }
                else if (file.FromList && !confirm)
                {
                    PurgeFileInfo? stat = null; string? statError = null; bool missing = false;
                    try { stat = fs.Stat(file.Path, out statError, out missing); } catch (Exception ex) { statError = ex.Message; }
                    if (missing) status = "skipped: no longer exists";
                    else if (stat == null) status = "failed: " + (statError ?? "unknown error");
                    else if (stat.VolumeSerial == null || stat.VolumeSerial != file.VolumeSerial) status = "skipped: file changed since list (volume)";
                    else if (stat.FileIndex == null || stat.FileIndex != file.FileId) status = "skipped: file changed since list (id)";
                    else if (stat.IsDirectory || stat.IsReparsePoint) status = "skipped: not a regular file";
                    else if (stat.LinkCount != 1) status = "skipped: hard-linked";
                    else
                    {
                        string? finalRoot; string? resolveError;
                        try { finalRoot = fs.ResolveRoot(scanRoot, out resolveError); }
                        catch (Exception ex) { finalRoot = null; resolveError = ex.Message; }
                        if (finalRoot == null) status = "failed: cannot open target: " + (resolveError ?? "unknown error");
                        else if (!IsStrictlyUnder(stat.FinalPath, finalRoot)) status = "skipped: outside target";
                        else if (stat.Length != file.LogicalBytes || stat.LastWriteUtc != file.LastWriteUtc)
                        {
                            if (services != null && ServiceQueryHelpers.IsRedirectLogName(file.Path) && stat.Length >= file.LogicalBytes)
                            {
                                bool? probed = fs.ProbeHeld(file.Path, out _);
                                if (probed == true)
                                {
                                    RecordHeldClassification(file, false, out status);
                                    if (IsWouldDelete(status))
                                    {
                                        would++;
                                        wouldBytes += file.LogicalBytes;
                                        targetBytes += file.LogicalBytes;
                                    }
                                    else
                                    {
                                        status = "skipped: file changed since list (size or time)";
                                    }
                                }
                                else
                                {
                                    status = "skipped: file changed since list (size or time)";
                                }
                            }
                            else
                            {
                                status = "skipped: file changed since list (size or time)";
                            }
                        }
                        else if (stat.IsReadOnly) status = "skipped: read-only";
                        else
                        {
                            if (services != null)
                            {
                                bool? probed = fs.ProbeHeld(file.Path, out string? probeErr);
                                if (probed == null)
                                {
                                    probeFailures++;
                                    firstProbeError ??= probeErr;
                                    status = "would delete";
                                    would++;
                                    wouldBytes += file.LogicalBytes;
                                    targetBytes += file.LogicalBytes;
                                }
                                else if (probed == false)
                                {
                                    status = "would delete";
                                    would++;
                                    wouldBytes += file.LogicalBytes;
                                    targetBytes += file.LogicalBytes;
                                }
                                else
                                {
                                    RecordHeldClassification(file, false, out status);
                                    if (IsWouldDelete(status))
                                    {
                                        would++;
                                        wouldBytes += file.LogicalBytes;
                                        targetBytes += file.LogicalBytes;
                                    }
                                }
                            }
                            else
                            {
                                status = "would delete";
                                would++;
                                wouldBytes += file.LogicalBytes;
                                targetBytes += file.LogicalBytes;
                            }
                        }
                    }
                }
                else if (!confirm)
                {
                    if (services != null)
                    {
                        bool? probed = fs.ProbeHeld(file.Path, out string? probeErr);
                        if (probed == null)
                        {
                            probeFailures++;
                            firstProbeError ??= probeErr;
                            status = "would delete";
                            would++;
                            wouldBytes += file.LogicalBytes;
                            targetBytes += file.LogicalBytes;
                        }
                        else if (probed == false)
                        {
                            status = "would delete";
                            would++;
                            wouldBytes += file.LogicalBytes;
                            targetBytes += file.LogicalBytes;
                        }
                        else
                        {
                            RecordHeldClassification(file, false, out status);
                            if (IsWouldDelete(status))
                            {
                                would++;
                                wouldBytes += file.LogicalBytes;
                                targetBytes += file.LogicalBytes;
                            }
                        }
                    }
                    else
                    {
                        status = "would delete";
                        would++;
                        wouldBytes += file.LogicalBytes;
                        targetBytes += file.LogicalBytes;
                    }
                }
                else if (targetFinal == null)
                {
                    status = "failed: cannot open target: " + (rootError ?? "unknown error");
                    try { log?.Invoke(LogLine(utcNow(), file.Path, file.LogicalBytes, status)); }
                    catch { stopReason = "log write failed"; }
                }
                else
                {
                    status = ProcessFile(file, targetFinal, fs, utcNow, log, ref stopReason, out long freed, services, isFixedDrive, serviceCache, ref heldTotal, ref heldTomcatCount, ref heldTomcatBytes, ref heldOtherCount, ref heldService, ref heldHost, ref heldInstallRoot, confirm && control != null, out var heldBack);
                    if (status == null && heldBack.HasValue)
                    {
                        heldBackRows.Add(new HeldBackRow
                        {
                            File = file,
                            ResultIndex = output.Count,
                            FileIndex = files.Count,
                            ScanRoot = scanRoot,
                            TargetFinal = targetFinal,
                            Host = heldBack.Value.Host,
                            ServiceKey = heldBack.Value.Service.Key,
                            MatchedService = heldBack.Value.Service,
                            IsRecent = false
                        });
                    }
                    else if (status == "deleted") { deleted++; deletedBytes += freed; targetBytes += freed; }
                }

                if (status != null)
                {
                    if (status.StartsWith("skipped:", StringComparison.Ordinal)) skipped++;
                    if (status.StartsWith("failed:", StringComparison.Ordinal)) failed++;
                    if (status.StartsWith("not attempted:", StringComparison.Ordinal)) notAttempted++;
                }
                files.Add(file with { PurgeStatus = status });
            }
            output.Add(result with { Largest = result.Largest with { Files = files, PurgeBytes = targetBytes, PurgeDryRun = !confirm } });
        }

        void AssignStatus(HeldBackRow row, string status, long freedBytes = 0)
        {
            var scanRes = output[row.ResultIndex];
            var largest = scanRes.Largest!;
            var fileList = largest.Files.ToList();
            fileList[row.FileIndex] = fileList[row.FileIndex] with { PurgeStatus = status };
            long purgeBytes = (largest.PurgeBytes ?? 0) + freedBytes;
            output[row.ResultIndex] = scanRes with { Largest = largest with { Files = fileList, PurgeBytes = purgeBytes } };
        }

        bool serviceStartFailed = false;
        var groups = new List<(string Host, string ServiceKey, List<HeldBackRow> Rows)>();
        foreach (var row in heldBackRows)
        {
            int idx = groups.FindIndex(g => g.Host.Equals(row.Host, StringComparison.OrdinalIgnoreCase) && g.ServiceKey.Equals(row.ServiceKey, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                groups[idx].Rows.Add(row);
            }
            else
            {
                groups.Add((row.Host, row.ServiceKey, new List<HeldBackRow> { row }));
            }
        }

        foreach (var group in groups)
        {
            if (stopReason != null || ct.IsCancellationRequested)
            {
                stopReason ??= "cancelled";
                string st = "not attempted: " + stopReason;
                foreach (var row in group.Rows)
                {
                    AssignStatus(row, st);
                    notAttempted++;
                }
                continue;
            }
            if (serviceStartFailed)
            {
                string st = "not attempted: earlier service start failed";
                foreach (var row in group.Rows)
                {
                    AssignStatus(row, st);
                    notAttempted++;
                }
                continue;
            }

            if (yes)
            {
                DateTime nowLocal = localNow();
                TimeSpan tod = nowLocal.TimeOfDay;
                bool inWindow = tod >= new TimeSpan(19, 0, 0) || tod < new TimeSpan(6, 0, 0);
                if (!inWindow)
                {
                    string st = "skipped: in use, outside 19:00-06:00 for /yes";
                    foreach (var row in group.Rows)
                    {
                        AssignStatus(row, st);
                        skipped++;
                    }
                    continue;
                }
            }
            else
            {
                long groupBytes = group.Rows.Sum(r => r.File.LogicalBytes);
                foreach (var row in group.Rows)
                {
                    error.WriteLine($"{Dirused.Common.Renderers.Format.Short(row.File.LogicalBytes)}  {row.File.Path}");
                }
                error.Write($"Stop service {group.ServiceKey} on {group.Host} to delete {group.Rows.Count} held file(s), {Dirused.Common.Renderers.Format.Size(groupBytes)}? Tomcat is down until the deletes finish. [y/N] ");

                if (inputRedirected)
                {
                    string st = "skipped: in use, not confirmed";
                    foreach (var row in group.Rows)
                    {
                        AssignStatus(row, st);
                        skipped++;
                    }
                    continue;
                }

                string? line = input?.ReadLine();
                string trimmed = line?.Trim() ?? "";
                if (!trimmed.Equals("y", StringComparison.OrdinalIgnoreCase) && !trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    string st = "skipped: in use, not confirmed";
                    foreach (var row in group.Rows)
                    {
                        AssignStatus(row, st);
                        skipped++;
                    }
                    continue;
                }
            }

            var stayingRows = new List<HeldBackRow>();
            foreach (var row in group.Rows)
            {
                bool? probed = fs.ProbeHeld(row.File.Path, out string? probeErr);
                if (probed == false)
                {
                    if (row.IsRecent)
                    {
                        string st = $"skipped: modified within {days} day(s)";
                        AssignStatus(row, st);
                        skipped++;
                    }
                    else
                    {
                        string targetFinal = row.TargetFinal ?? row.ScanRoot;
                        string st = ProcessFile(row.File, targetFinal, fs, utcNow, log, ref stopReason, out long freed, services, isFixedDrive, serviceCache, ref heldTotal, ref heldTomcatCount, ref heldTomcatBytes, ref heldOtherCount, ref heldService, ref heldHost, ref heldInstallRoot, false, out _)!;
                        AssignStatus(row, st, freed);
                        if (st == "deleted") { deleted++; deletedBytes += freed; }
                        else if (st.StartsWith("skipped:", StringComparison.Ordinal)) skipped++;
                        else if (st.StartsWith("failed:", StringComparison.Ordinal)) failed++;
                        else if (st.StartsWith("not attempted:", StringComparison.Ordinal)) notAttempted++;
                    }
                    continue;
                }

                if (probed == null)
                {
                    string st = $"failed: in use ({probeErr ?? "unknown error"})";
                    AssignStatus(row, st);
                    failed++;
                    try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                    catch { stopReason = "log write failed"; }
                    continue;
                }

                var reclass = ServiceQueryHelpers.ClassifyHeldFile(row.File, fs, services, isFixedDrive, serviceCache);
                if (reclass.Reason != "tomcat")
                {
                    string st = $"failed: in use ({reclass.Reason})";
                    AssignStatus(row, st);
                    failed++;
                    try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                    catch { stopReason = "log write failed"; }
                    continue;
                }

                stayingRows.Add(row);
            }

            if (stayingRows.Count == 0)
            {
                continue;
            }

            using var handle = control!.Open(group.Host, group.ServiceKey, out string? openErr);
            if (handle == null)
            {
                string st = $"failed: could not stop {group.ServiceKey}: {openErr}";
                foreach (var row in stayingRows)
                {
                    AssignStatus(row, st);
                    failed++;
                    try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                    catch { stopReason = "log write failed"; }
                }
                continue;
            }

            var dependents = handle.RunningDependents(out string? depErr);
            if (dependents == null)
            {
                string st = $"failed: could not stop {group.ServiceKey}: {depErr}";
                foreach (var row in stayingRows)
                {
                    AssignStatus(row, st);
                    failed++;
                    try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                    catch { stopReason = "log write failed"; }
                }
                continue;
            }

            if (dependents.Count > 0)
            {
                string st = $"failed: in use (service has running dependents: {string.Join(", ", dependents)})";
                foreach (var row in stayingRows)
                {
                    AssignStatus(row, st);
                    failed++;
                    try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                    catch { stopReason = "log write failed"; }
                }
                continue;
            }

            var (initialState, _) = handle.QueryState();
            if (initialState != ServiceState.Running)
            {
                string st = $"failed: in use ({group.ServiceKey} not running)";
                foreach (var row in stayingRows)
                {
                    AssignStatus(row, st);
                    failed++;
                    try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                    catch { stopReason = "log write failed"; }
                }
                continue;
            }

            try
            {
                log?.Invoke(LogLine(utcNow(), $"{group.ServiceKey}@{group.Host}", 0, "service-stop"));
            }
            catch
            {
                stopReason = "log write failed";
                string st = "failed: log write failed";
                foreach (var row in stayingRows)
                {
                    AssignStatus(row, st);
                    failed++;
                }
                continue;
            }

            bool owned = false;
            bool stopTimedOut = false;
            var (stopOk, stopErr) = handle.Stop();
            if (!stopOk)
            {
                if (stopErr is 1051 or 1062)
                {
                    string st = $"failed: could not stop {group.ServiceKey}: {new Win32Exception(stopErr).Message}";
                    foreach (var row in stayingRows)
                    {
                        AssignStatus(row, st);
                        failed++;
                        try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                        catch { stopReason = "log write failed"; }
                    }
                    continue;
                }

                var (postStopState, _) = handle.QueryState();
                if (postStopState == ServiceState.Running)
                {
                    string st = $"failed: could not stop {group.ServiceKey}: {new Win32Exception(stopErr).Message}";
                    foreach (var row in stayingRows)
                    {
                        AssignStatus(row, st);
                        failed++;
                        try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                        catch { stopReason = "log write failed"; }
                    }
                    continue;
                }

                owned = true;
                string stOwned = $"failed: could not stop {group.ServiceKey}: {new Win32Exception(stopErr).Message}";
                foreach (var row in stayingRows)
                {
                    AssignStatus(row, stOwned);
                    failed++;
                    try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, stOwned)); }
                    catch { stopReason = "log write failed"; }
                }
            }
            else
            {
                owned = true;
                DateTime pollStart = utcNow();
                bool isStopped = false;
                while ((utcNow() - pollStart).TotalSeconds < 120)
                {
                    var (s, _) = handle.QueryState();
                    if (s == ServiceState.Stopped)
                    {
                        isStopped = true;
                        break;
                    }
                    sleep(TimeSpan.FromSeconds(2));
                }

                if (!isStopped)
                {
                    stopTimedOut = true;
                    string st = $"failed: could not stop {group.ServiceKey}: timed out";
                    foreach (var row in stayingRows)
                    {
                        AssignStatus(row, st);
                        failed++;
                        try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                        catch { stopReason = "log write failed"; }
                    }
                }
            }

            try
            {
                if (owned && !stopTimedOut && stopOk)
                {
                    string rootLogs = group.Rows[0].MatchedService.InstallRoot.Replace('/', '\\').TrimEnd('\\') + @"\logs";
                    for (int rIdx = 0; rIdx < stayingRows.Count; rIdx++)
                    {
                        var row = stayingRows[rIdx];
                        if (ct.IsCancellationRequested)
                        {
                            for (int remIdx = rIdx; remIdx < stayingRows.Count; remIdx++)
                            {
                                AssignStatus(stayingRows[remIdx], "not attempted: cancelled");
                                notAttempted++;
                            }
                            break;
                        }

                        string status;
                        long freed = 0;
                        try
                        {
                            status = DeleteHeldRow(row, fs, utcNow, log, sleep, rootLogs, group.ServiceKey, ref stopReason, out freed);
                            if (status == "deleted")
                            {
                                deleted++;
                                deletedBytes += freed;
                            }
                            else if (status.StartsWith("skipped:", StringComparison.Ordinal)) skipped++;
                            else if (status.StartsWith("failed:", StringComparison.Ordinal)) failed++;
                            else if (status.StartsWith("not attempted:", StringComparison.Ordinal)) notAttempted++;
                        }
                        catch (Exception ex)
                        {
                            status = "failed: " + ex.Message;
                            failed++;
                            try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, status)); }
                            catch { stopReason = "log write failed"; }
                        }
                        AssignStatus(row, status, freed);
                    }
                }
            }
            finally
            {
                if (owned)
                {
                    bool verified = false;
                    string failedReason = "start failed";
                    ServiceState finalReportedState = ServiceState.Other;

                    for (int attempt = 0; attempt < 2 && !verified; attempt++)
                    {
                        DateTime waitStopPendingStart = utcNow();
                        while ((utcNow() - waitStopPendingStart).TotalSeconds < 300)
                        {
                            var (st, _) = handle.QueryState();
                            if (st != ServiceState.StopPending) break;
                            sleep(TimeSpan.FromSeconds(2));
                        }

                        var (startSuccess, sErr) = handle.Start();
                        if (!startSuccess)
                        {
                            if (sErr == 1056)
                            {
                                var (s1056, _) = handle.QueryState();
                                if (s1056 == ServiceState.StopPending)
                                {
                                    DateTime waitSecond = utcNow();
                                    while ((utcNow() - waitSecond).TotalSeconds < 300)
                                    {
                                        var (stWait, _) = handle.QueryState();
                                        if (stWait != ServiceState.StopPending) break;
                                        sleep(TimeSpan.FromSeconds(2));
                                    }
                                    handle.Start();
                                }
                            }
                            else
                            {
                                sleep(TimeSpan.FromSeconds(5));
                                handle.Start();
                            }
                        }

                        DateTime pollRunningStart = utcNow();
                        bool reachedRunning = false;
                        while ((utcNow() - pollRunningStart).TotalSeconds < 120)
                        {
                            var (st, _) = handle.QueryState();
                            finalReportedState = st;
                            if (st == ServiceState.Running)
                            {
                                reachedRunning = true;
                                break;
                            }
                            sleep(TimeSpan.FromSeconds(2));
                        }

                        if (!reachedRunning)
                        {
                            failedReason = "timed out waiting for RUNNING";
                            continue;
                        }

                        try
                        {
                            log?.Invoke(LogLine(utcNow(), $"{group.ServiceKey}@{group.Host}", 0, "service-start"));
                        }
                        catch (Exception ex)
                        {
                            try { error.WriteLine($"error writing purge log: {ex.Message}"); } catch { }
                        }

                        sleep(TimeSpan.FromSeconds(60));
                        var (settledState, _) = handle.QueryState();
                        finalReportedState = settledState;
                        if (settledState == ServiceState.Running)
                        {
                            verified = true;
                            try
                            {
                                log?.Invoke(LogLine(utcNow(), $"{group.ServiceKey}@{group.Host}", 0, "service-started"));
                            }
                            catch (Exception ex)
                            {
                                try { error.WriteLine($"error writing purge log: {ex.Message}"); } catch { }
                            }
                            break;
                        }
                        else
                        {
                            failedReason = "stopped after settle";
                        }
                    }

                    if (!verified)
                    {
                        serviceStartFailed = true;
                        try
                        {
                            log?.Invoke(LogLine(utcNow(), $"{group.ServiceKey}@{group.Host}", 0, $"service-start-failed: {failedReason}"));
                        }
                        catch (Exception ex)
                        {
                            try { error.WriteLine($"error writing purge log: {ex.Message}"); } catch { }
                        }

                        if (group.Host == ".")
                        {
                            error.WriteLine($"error: {group.ServiceKey} did not start ({failedReason}, state {finalReportedState}). Start it with: sc start {group.ServiceKey}");
                        }
                        else
                        {
                            error.WriteLine($"error: {group.ServiceKey} on {group.Host} did not start ({failedReason}, state {finalReportedState}). Start it with: sc \\\\{group.Host} start {group.ServiceKey}");
                        }
                    }
                }
            }
        }

        HeldFilesSummary? heldSummary = heldTotal > 0
            ? new HeldFilesSummary(heldTotal, heldService, heldHost, heldInstallRoot, heldTomcatCount, heldTomcatBytes, heldOtherCount)
            : null;

        return new(output, would, deleted, skipped, failed, notAttempted, wouldBytes, deletedBytes)
        {
            HeldSummary = heldSummary,
            ProbeFailures = probeFailures,
            FirstProbeError = firstProbeError,
            ServiceStartFailed = serviceStartFailed
        };
    }

    private static string? ProcessFile(
        LargestFile file,
        string targetFinal,
        IPurgeFileSystem fs,
        Func<DateTime> utcNow,
        Action<string>? log,
        ref string? stopReason,
        out long freed,
        IServiceQuery? services,
        Func<string, bool>? isFixedDrive,
        Dictionary<string, (IReadOnlyList<ServiceEntry>? List, string? Error)> serviceCache,
        ref int heldTotal,
        ref int heldTomcatCount,
        ref long heldTomcatBytes,
        ref int heldOtherCount,
        ref string? heldService,
        ref string? heldHost,
        ref string? heldInstallRoot,
        bool holdBackForStop,
        out (string Host, ServiceEntry Service, string InstallRoot)? heldBack)
    {
        heldBack = null;
        freed = 0;
        PurgeOpen? opened = null;
        string? status = "failed: unknown error";
        bool marked = false;
        bool deletionLineWritten = false;
        try
        {
            opened = fs.Open(file.Path);
            if (opened.NotFound) status = "skipped: no longer exists";
            else if (opened.Info == null)
            {
                if (services != null && opened.ErrorCode == 32)
                {
                    heldTotal++;
                    var classification = ServiceQueryHelpers.ClassifyHeldFile(file, fs, services, isFixedDrive, serviceCache);
                    if (classification.Reason == "tomcat")
                    {
                        heldTomcatCount++;
                        heldTomcatBytes += file.LogicalBytes;
                        heldService ??= classification.Service!.Key;
                        heldHost ??= classification.Host;
                        heldInstallRoot ??= classification.InstallRoot;
                        if (holdBackForStop)
                        {
                            heldBack = (classification.Host!, classification.Service!, classification.InstallRoot!);
                            opened.Dispose();
                            return null;
                        }
                        status = $"failed: in use by {classification.Service!.Key} (stop not available yet)";
                    }
                    else
                    {
                        heldOtherCount++;
                        status = $"failed: in use ({classification.Reason})";
                    }
                }
                else
                {
                    status = "failed: " + (opened.Error ?? "unknown error");
                }
            }
            else
            {
                var info = opened.Info;
                if (file.FromList && (info.VolumeSerial == null || info.VolumeSerial != file.VolumeSerial)) status = "skipped: file changed since list (volume)";
                else if (file.FromList && (info.FileIndex == null || info.FileIndex != file.FileId)) status = "skipped: file changed since list (id)";
                else if (!file.FromList && file.VolumeSerial != null && file.FileId != null && (info.VolumeSerial != file.VolumeSerial || info.FileIndex != file.FileId)) status = "skipped: changed since scan (id)";
                else if (info.IsDirectory || info.IsReparsePoint) status = "skipped: not a regular file";
                else if (info.LinkCount != 1) status = "skipped: hard-linked";
                else if (!IsStrictlyUnder(info.FinalPath, targetFinal)) status = "skipped: outside target";
                else if (info.Length != file.LogicalBytes || info.LastWriteUtc != file.LastWriteUtc) status = file.FromList ? "skipped: file changed since list (size or time)" : "skipped: changed since scan";
                else if (info.IsReadOnly) status = "skipped: read-only";
                else
                {
                    opened.MarkDelete();
                    marked = true;
                    status = "deleted";
                    try
                    {
                        log?.Invoke(LogLine(utcNow(), file.Path, info.Length, status));
                        deletionLineWritten = true;
                    }
                    catch
                    {
                        try { opened.UnmarkDelete(); } catch { }
                        marked = false;
                        status = "failed: log write failed";
                        stopReason = "log write failed";
                    }
                }
            }
        }
        catch (Exception ex) { status = "failed: " + ex.Message; }

        try { opened?.Dispose(); }
        catch (Exception ex)
        {
            if (status == "deleted") status = "failed: " + ex.Message;
        }

        if (status == "deleted" && marked && deletionLineWritten)
        {
            try
            {
                using var verification = fs.Open(file.Path);
                if (verification.NotFound)
                {
                    freed = file.LogicalBytes;
                    return "deleted";
                }
                status = "failed: delete pending (another process has the file open)";
            }
            catch (Exception ex) { status = "failed: " + ex.Message; }
            try { log?.Invoke(LogLine(utcNow(), file.Path, file.LogicalBytes, status)); }
            catch { stopReason = "log write failed"; }
        }
        else if (status != null && status.StartsWith("failed:", StringComparison.Ordinal) && status != "failed: log write failed")
        {
            try { log?.Invoke(LogLine(utcNow(), file.Path, file.LogicalBytes, status)); }
            catch { stopReason = "log write failed"; }
        }
        return status;
    }

    private static string DeleteHeldRow(
        HeldBackRow row,
        IPurgeFileSystem fs,
        Func<DateTime> utcNow,
        Action<string>? log,
        Action<TimeSpan> sleep,
        string rootLogs,
        string serviceKey,
        ref string? stopReason,
        out long freed)
    {
        freed = 0;
        DateTime startOpen = utcNow();
        PurgeOpen opened = fs.Open(row.File.Path);
        while (opened.ErrorCode == 32 && (utcNow() - startOpen).TotalSeconds < 30)
        {
            opened.Dispose();
            sleep(TimeSpan.FromSeconds(2));
            opened = fs.Open(row.File.Path);
        }

        long handleLength = 0;
        try
        {
            if (opened.ErrorCode == 32)
            {
                string st = $"failed: in use after stopping {serviceKey}";
                try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                catch { stopReason = "log write failed"; }
                return st;
            }
            if (opened.NotFound)
            {
                return "skipped: no longer exists";
            }
            if (opened.Info == null)
            {
                string st = "failed: " + (opened.Error ?? "unknown error");
                try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, st)); }
                catch { stopReason = "log write failed"; }
                return st;
            }

            var info = opened.Info;
            handleLength = info.Length;
            if (info.VolumeSerial == null || info.FileIndex == null ||
                info.VolumeSerial != row.File.VolumeSerial || info.FileIndex != row.File.FileId)
            {
                return row.File.FromList ? "skipped: file changed since list (id)" : "skipped: changed since scan (id)";
            }

            if (info.Length < row.File.LogicalBytes)
            {
                return row.File.FromList ? "skipped: file changed since list (smaller)" : "skipped: changed since scan (smaller)";
            }

            string cleanFinal = info.FinalPath.StartsWith(@"\\?\", StringComparison.Ordinal) && !info.FinalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                ? info.FinalPath.Substring(4)
                : info.FinalPath;
            bool mapped = ServiceQueryHelpers.TryMapHostAndLocalPath(cleanFinal, null, out _, out string? localFinal);
            string pathForDir = (mapped && localFinal != null) ? localFinal : cleanFinal;
            string finalDir = (WindowsPath.GetDirectoryName(pathForDir.Replace('/', '\\')) ?? "").TrimEnd('\\');
            if (!finalDir.Equals(rootLogs, StringComparison.OrdinalIgnoreCase) || !ServiceQueryHelpers.IsRedirectLogName(info.FinalPath))
            {
                return "skipped: changed since scan (name)";
            }

            if (info.IsDirectory || info.IsReparsePoint) return "skipped: not a regular file";
            if (info.LinkCount != 1) return "skipped: hard-linked";
            string targetFinal = row.TargetFinal ?? row.ScanRoot;
            if (!IsStrictlyUnder(info.FinalPath, targetFinal)) return "skipped: outside target";
            if (info.IsReadOnly) return "skipped: read-only";

            opened.MarkDelete();
            try
            {
                log?.Invoke(LogLine(utcNow(), row.File.Path, info.Length, "deleted"));
            }
            catch
            {
                try { opened.UnmarkDelete(); } catch { }
                stopReason = "log write failed";
                return "failed: log write failed";
            }
        }
        finally
        {
            opened.Dispose();
        }

        using var verification = fs.Open(row.File.Path);
        if (verification.NotFound)
        {
            freed = handleLength;
            return "deleted";
        }
        string pending = "failed: delete pending (another process has the file open)";
        try { log?.Invoke(LogLine(utcNow(), row.File.Path, row.File.LogicalBytes, pending)); }
        catch { stopReason = "log write failed"; }
        return pending;
    }

    private static bool HasUnsafeName(string path) => path.Replace('/', '\\').Split('\\').Any(s => s.EndsWith('.') || s.EndsWith(' '));
    private static bool IsAbsoluteWindowsPath(string path) => path.StartsWith('\\') || path.StartsWith('/') || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/');
    private static bool IsPurgeLog(string path, string logPath) => Normalize(path).Equals(Normalize(logPath), StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static string LogLine(DateTime time, string path, long bytes, string status) => $"{time:o}\t{Environment.UserName}\t{Environment.MachineName}\t{Clean(path)}\t{bytes}\t{Clean(status)}";
}

[SupportedOSPlatform("windows")]
public sealed class RealPurgeFileSystem : IPurgeFileSystem
{
    private const uint Delete = 0x00010000, ReadAttributes = 0x80, ShareAll = 0x1 | 0x2 | 0x4, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000, FileNameOpened = 0x8;
    private const uint FileReadData = 0x0001;
    private uint finalPathFlags;

    public string? ResolveRoot(string path, out string? error)
    {
        error = null;
        finalPathFlags = 0;
        if (!IsAbsoluteWindowsPath(path)) { error = "path is not absolute"; return null; }
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(Extended(path), ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return null; }
            if (TryGetFinalPath(handle, 0, out var finalPath)) return finalPath;
            finalPathFlags = FileNameOpened;
            if (TryGetFinalPath(handle, finalPathFlags, out finalPath)) return finalPath;
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }
        catch (Exception ex) { error = ex.Message; return null; }
        finally { handle?.Dispose(); }
    }

    public PurgeFileInfo? Stat(string path, out string? error, out bool notFound)
    {
        error = null; notFound = false;
        if (!IsAbsoluteWindowsPath(path)) { error = "path is not absolute"; return null; }
        using var handle = CreateFileW(Extended(path), ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) { int e = Marshal.GetLastWin32Error(); notFound = e is 2 or 3; error = new Win32Exception(e).Message; return null; }
        if (!GetFileInformationByHandle(handle, out var d)) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return null; }
        if (!TryGetFinalPath(handle, finalPathFlags, out var finalPath)) finalPath = path;
        long size = ((long)d.SizeHigh << 32) | d.SizeLow; long ticks = ((long)d.WriteHigh << 32) | d.WriteLow;
        return new PurgeFileInfo(finalPath, size, DateTime.FromFileTimeUtc(ticks), (d.Attributes & 0x10) != 0, (d.Attributes & 0x400) != 0, (int)d.Links) { IsReadOnly = (d.Attributes & 0x1) != 0, VolumeSerial = d.Volume, FileIndex = ((ulong)d.IndexHigh << 32) | d.IndexLow };
    }

    public bool? ProbeHeld(string path, out string? error)
    {
        error = null;
        if (!IsAbsoluteWindowsPath(path)) { error = "path is not absolute"; return null; }
        try
        {
            using var handle = CreateFileW(Extended(path), FileReadData, 0, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error();
                if (e == 32) return true;
                error = new Win32Exception(e).Message;
                return null;
            }
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public PurgeOpen Open(string path)
    {
        if (!IsAbsoluteWindowsPath(path)) return new PurgeOpen("path is not absolute");
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(Extended(path), Delete | ReadAttributes, 0, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error(); handle.Dispose(); handle = null;
                return new PurgeOpen(new Win32Exception(e).Message, e is 2 or 3, e);
            }
            if (!GetFileInformationByHandle(handle, out var data)) return FailAndClose(handle, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (!TryGetFinalPath(handle, finalPathFlags, out var finalPath)) return FailAndClose(handle, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            long size = ((long)data.SizeHigh << 32) | data.SizeLow;
            long ticks = ((long)data.WriteHigh << 32) | data.WriteLow;
            var info = new PurgeFileInfo(finalPath, size, DateTime.FromFileTimeUtc(ticks), (data.Attributes & 0x10) != 0, (data.Attributes & 0x400) != 0, (int)data.Links)
            { IsReadOnly = (data.Attributes & 0x1) != 0, VolumeSerial = data.Volume, FileIndex = ((ulong)data.IndexHigh << 32) | data.IndexLow };
            var owned = handle;
            handle = null;
            return new PurgeOpen(info, owned,
                () => SetDisposition(owned, true),
                () => SetDisposition(owned, false));
        }
        catch (Exception ex)
        {
            handle?.Dispose();
            return new PurgeOpen(ex.Message);
        }
        finally { handle?.Dispose(); }
    }

    private static PurgeOpen FailAndClose(SafeFileHandle handle, string message) { handle.Dispose(); return new PurgeOpen(message); }
    private static void SetDisposition(SafeFileHandle handle, bool delete)
    {
        var disposition = new FileDispositionInfo { DeleteFile = delete };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static bool TryGetFinalPath(SafeFileHandle handle, uint flags, out string path)
    {
        var buffer = new System.Text.StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
        if (length == 0 || length >= buffer.Capacity) { path = ""; return false; }
        path = buffer.ToString(); return true;
    }
    private static bool IsAbsoluteWindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/');
    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential)] private struct ByHandle { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation; public System.Runtime.InteropServices.ComTypes.FILETIME Access; public uint WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct FileDispositionInfo { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandle info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, System.Text.StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FileDispositionInfo info, uint size);
}
