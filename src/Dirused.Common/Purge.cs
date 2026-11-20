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
}
public static class PurgeLog { public const string DefaultPath = @"C:\temp\dirused-purge.log"; }

public static class Purge
{
    public static bool IsWouldDelete(string? status) =>
        status == "would delete" || (status != null && status.StartsWith("would delete after stopping ", StringComparison.Ordinal));

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
        Func<string, bool>? isFixedDrive = null)
    {
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

        void RecordHeldClassification(LargestFile f, bool isConfirm, out string statusText)
        {
            heldTotal++;
            var classification = ServiceQueryHelpers.ClassifyHeldFile(f, fs, services, isFixedDrive, serviceCache);
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
                            RecordHeldClassification(file, confirm, out status);
                            if (IsWouldDelete(status))
                            {
                                would++;
                                wouldBytes += file.LogicalBytes;
                                targetBytes += file.LogicalBytes;
                            }
                            else if (confirm && status.StartsWith("failed: in use by ", StringComparison.Ordinal))
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
                    status = ProcessFile(file, targetFinal, fs, utcNow, log, ref stopReason, out long freed, services, isFixedDrive, serviceCache, ref heldTotal, ref heldTomcatCount, ref heldTomcatBytes, ref heldOtherCount, ref heldService, ref heldHost, ref heldInstallRoot);
                    if (status == "deleted") { deleted++; deletedBytes += freed; targetBytes += freed; }
                }

                if (status.StartsWith("skipped:", StringComparison.Ordinal)) skipped++;
                if (status.StartsWith("failed:", StringComparison.Ordinal)) failed++;
                if (status.StartsWith("not attempted:", StringComparison.Ordinal)) notAttempted++;
                files.Add(file with { PurgeStatus = status });
            }
            output.Add(result with { Largest = result.Largest with { Files = files, PurgeBytes = targetBytes, PurgeDryRun = !confirm } });
        }

        HeldFilesSummary? heldSummary = heldTotal > 0
            ? new HeldFilesSummary(heldTotal, heldService, heldHost, heldInstallRoot, heldTomcatCount, heldTomcatBytes, heldOtherCount)
            : null;

        return new(output, would, deleted, skipped, failed, notAttempted, wouldBytes, deletedBytes)
        {
            HeldSummary = heldSummary,
            ProbeFailures = probeFailures,
            FirstProbeError = firstProbeError
        };
    }

    private static string ProcessFile(
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
        ref string? heldInstallRoot)
    {
        freed = 0;
        PurgeOpen? opened = null;
        string status = "failed: unknown error";
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
        else if (status.StartsWith("failed:", StringComparison.Ordinal) && status != "failed: log write failed")
        {
            try { log?.Invoke(LogLine(utcNow(), file.Path, file.LogicalBytes, status)); }
            catch { stopReason = "log write failed"; }
        }
        return status;
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
