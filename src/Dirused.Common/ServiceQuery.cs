using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Dirused.Core;

namespace Dirused.Common;

public sealed record ServiceEntry(string Key, string ProcrunName, string InstallRoot, bool Running);

public interface IServiceQuery
{
    // null + error when the service manager cannot be opened or enumerated; never partial results
    IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error);
}

public static class ProcrunPath
{
    // "D:\Tomcat\Tomcat\bin\Tomcat85.exe //RS//Tomcat85" -> ("D:\Tomcat\Tomcat\bin\Tomcat85.exe", "Tomcat85")
    // quoted executable with spaces accepted; null when there is no //RS//<name> argument
    public static (string Exe, string Name)? Parse(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath)) return null;
        string trimmed = binaryPath.Trim();
        string exe;
        string rest;
        if (trimmed.StartsWith('"'))
        {
            int closeQuote = trimmed.IndexOf('"', 1);
            if (closeQuote < 0) return null;
            exe = trimmed.Substring(1, closeQuote - 1).Trim();
            rest = trimmed.Substring(closeQuote + 1);
        }
        else
        {
            int rsIndex = trimmed.IndexOf("//RS//", StringComparison.OrdinalIgnoreCase);
            if (rsIndex < 0) return null;
            exe = trimmed.Substring(0, rsIndex).Trim();
            rest = trimmed.Substring(rsIndex);
        }

        if (string.IsNullOrEmpty(exe)) return null;

        int rsPos = rest.IndexOf("//RS//", StringComparison.OrdinalIgnoreCase);
        if (rsPos < 0) return null;
        string afterRs = rest.Substring(rsPos + 6).TrimStart();
        if (string.IsNullOrEmpty(afterRs)) return null;

        int nextSpace = afterRs.IndexOfAny(new[] { ' ', '\t' });
        string name = nextSpace >= 0 ? afterRs.Substring(0, nextSpace) : afterRs;
        name = name.Trim().Trim('"');
        if (string.IsNullOrEmpty(name)) return null;

        return (exe, name);
    }

    public static string InstallRoot(string exe)
    {
        string? dir = WindowsPath.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir)) return exe;
        string folderName = WindowsPath.GetFileName(dir);
        if (string.Equals(folderName, "bin", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = WindowsPath.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent)) return parent;
            return ".";
        }
        return dir;
    }
}

public static class WindowsPath
{
    public static string GetFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string trimmed = path.TrimEnd('\\', '/');
        int lastSlash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
    }

    public static string? GetDirectoryName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string trimmed = path.TrimEnd('\\', '/');
        int lastSlash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        if (lastSlash < 0) return null;

        if (lastSlash == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return trimmed.Substring(0, 3);
        }

        return trimmed.Substring(0, lastSlash);
    }

    public static string Combine(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return name;
        if (string.IsNullOrEmpty(name)) return dir;
        return dir.TrimEnd('\\', '/') + "\\" + name.TrimStart('\\', '/');
    }
}

public static class DriveHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string lpRootPathName);

    public static bool DefaultIsFixedDrive(string driveRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetDriveTypeW(driveRoot) == 3; // DRIVE_FIXED = 3
        }
        return false;
    }
}

public static class ServiceQueryHelpers
{
    public static bool IsRedirectLogName(string path)
    {
        string fileName = WindowsPath.GetFileName(path);
        return Regex.IsMatch(fileName, @"^.+-(stdout|stderr)\..*\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool TryMapHostAndLocalPath(string path, Func<string, bool>? isFixedDrive, out string? host, out string? localPath)
    {
        host = null;
        localPath = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Replace('/', '\\');

        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            string rem = normalized.Substring(8);
            return ParseUncAdminShare(rem, out host, out localPath);
        }

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string rem = normalized.Substring(2);
            return ParseUncAdminShare(rem, out host, out localPath);
        }

        if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\')
        {
            string driveRoot = $"{char.ToUpperInvariant(normalized[0])}:\\";
            bool fixedDrive = isFixedDrive != null ? isFixedDrive(driveRoot) : DriveHelper.DefaultIsFixedDrive(driveRoot);
            if (fixedDrive)
            {
                host = ".";
                localPath = $"{char.ToUpperInvariant(normalized[0])}:{normalized.Substring(2)}";
                return true;
            }
            return false;
        }

        return false;
    }

    private static bool ParseUncAdminShare(string uncRemainder, out string? host, out string? localPath)
    {
        host = null;
        localPath = null;
        int firstSlash = uncRemainder.IndexOf('\\');
        if (firstSlash <= 0) return false;

        host = uncRemainder.Substring(0, firstSlash);
        string afterHost = uncRemainder.Substring(firstSlash + 1);
        int secondSlash = afterHost.IndexOf('\\');
        string share = secondSlash >= 0 ? afterHost.Substring(0, secondSlash) : afterHost;
        string rest = secondSlash >= 0 ? afterHost.Substring(secondSlash + 1) : "";

        if (share.Length == 2 && char.IsAsciiLetter(share[0]) && share[1] == '$')
        {
            char drive = char.ToUpperInvariant(share[0]);
            localPath = $"{drive}:\\{rest}";
            return true;
        }

        return false;
    }

    public static (string Reason, ServiceEntry? Service, string? Host, string? InstallRoot) ClassifyHeldFile(
        LargestFile file,
        IPurgeFileSystem fs,
        IServiceQuery? services,
        Func<string, bool>? isFixedDrive,
        Dictionary<string, (IReadOnlyList<ServiceEntry>? List, string? Error)> serviceCache)
    {
        if (!TryMapHostAndLocalPath(file.Path, isFixedDrive, out string? host, out string? localPath))
        {
            return ("no service found", null, null, null);
        }

        string fileName = WindowsPath.GetFileName(localPath!);
        if (fileName.EndsWith(".hprof", StringComparison.OrdinalIgnoreCase))
        {
            return ("heap dump, holder unknown", null, null, null);
        }

        if (services == null)
        {
            return ("no service found", null, null, null);
        }

        if (!serviceCache.TryGetValue(host!, out var cached))
        {
            var list = services.ListProcrun(host!, out var svcErr);
            cached = (list, svcErr);
            serviceCache[host!] = cached;
        }

        if (cached.List == null)
        {
            return ($"could not list services on {host}: {cached.Error ?? "unknown error"}", null, null, null);
        }

        var runningCandidates = cached.List.Where(s => s.Running).ToList();
        string localParent = NormalizePath(WindowsPath.GetDirectoryName(localPath!) ?? "");

        var matchedServices = new List<ServiceEntry>();
        foreach (var s in runningCandidates)
        {
            string sLogFolder = NormalizePath(WindowsPath.Combine(s.InstallRoot, "logs"));
            if (localParent.Equals(sLogFolder, StringComparison.OrdinalIgnoreCase))
            {
                string pattern = $"^{Regex.Escape(s.ProcrunName)}-(stdout|stderr)\\..*\\.log$";
                if (Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    matchedServices.Add(s);
                }
            }
        }

        if (matchedServices.Count == 0)
        {
            bool underCandidateRoot = runningCandidates.Any(c => Purge.IsStrictlyUnder(localPath!, c.InstallRoot));
            return (underCandidateRoot ? "not a Tomcat log" : "no service found", null, null, null);
        }

        if (matchedServices.Count > 1)
        {
            return ("no service found", null, null, null);
        }

        var matched = matchedServices[0];

        if (!file.VolumeSerial.HasValue || file.VolumeSerial.Value == 0 || !file.FileId.HasValue || file.FileId.Value == 0)
        {
            return ("id mismatch", null, null, null);
        }

        var stat = fs.Stat(file.Path, out _, out bool missing);
        if (stat == null || missing || !stat.VolumeSerial.HasValue || stat.VolumeSerial.Value == 0 || !stat.FileIndex.HasValue || stat.FileIndex.Value == 0)
        {
            return ("id mismatch", null, null, null);
        }

        if (stat.VolumeSerial.Value != file.VolumeSerial.Value || stat.FileIndex.Value != file.FileId.Value)
        {
            return ("id mismatch", null, null, null);
        }

        return ("tomcat", matched, host, matched.InstallRoot);
    }

    private static string NormalizePath(string path) => path.Replace('/', '\\').TrimEnd('\\');
}

[SupportedOSPlatform("windows")]
public sealed class RealServiceQuery : IServiceQuery
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_STATE_ALL = 0x00000003;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const int ERROR_MORE_DATA = 234;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(
        IntPtr hSCManager,
        int infoLevel,
        uint dwServiceType,
        uint dwServiceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(
        IntPtr hService,
        IntPtr lpServiceConfig,
        uint cbBufSize,
        out uint pcbBytesNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_SERVICE_STATUS_PROCESS_RAW
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QUERY_SERVICE_CONFIG_RAW
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    public IReadOnlyList<ServiceEntry>? ListProcrun(string host, out string? error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Windows only";
            return null;
        }

        error = null;
        string? machineName = (host == "." || string.IsNullOrEmpty(host)) ? null : host;
        IntPtr scm = OpenSCManagerW(machineName, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        try
        {
            var entries = new List<ServiceEntry>();
            uint resumeHandle = 0;

            while (true)
            {
                uint bytesNeeded = 0;
                uint servicesReturned = 0;
                bool ok = EnumServicesStatusExW(
                    scm,
                    SC_ENUM_PROCESS_INFO,
                    SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_STATE_ALL,
                    IntPtr.Zero,
                    0,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                int lastError = Marshal.GetLastWin32Error();
                if (!ok && lastError != ERROR_MORE_DATA)
                {
                    error = new Win32Exception(lastError).Message;
                    return null;
                }

                if (bytesNeeded == 0) break;

                IntPtr buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    ok = EnumServicesStatusExW(
                        scm,
                        SC_ENUM_PROCESS_INFO,
                        SERVICE_WIN32_OWN_PROCESS,
                        SERVICE_STATE_ALL,
                        buffer,
                        bytesNeeded,
                        out bytesNeeded,
                        out servicesReturned,
                        ref resumeHandle,
                        null);

                    lastError = Marshal.GetLastWin32Error();
                    if (!ok && lastError != ERROR_MORE_DATA)
                    {
                        error = new Win32Exception(lastError).Message;
                        return null;
                    }

                    int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS_RAW>();
                    for (int i = 0; i < servicesReturned; i++)
                    {
                        IntPtr ptr = IntPtr.Add(buffer, i * structSize);
                        var item = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS_RAW>(ptr);
                        string? serviceName = Marshal.PtrToStringUni(item.lpServiceName);
                        if (string.IsNullOrEmpty(serviceName)) continue;

                        bool isRunning = item.dwCurrentState == 4; // SERVICE_RUNNING

                        IntPtr svc = OpenServiceW(scm, serviceName, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS);
                        if (svc == IntPtr.Zero)
                        {
                            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                            return null;
                        }

                        try
                        {
                            uint configBytesNeeded = 0;
                            QueryServiceConfigW(svc, IntPtr.Zero, 0, out configBytesNeeded);
                            int cfgErr = Marshal.GetLastWin32Error();
                            if (cfgErr != ERROR_INSUFFICIENT_BUFFER || configBytesNeeded == 0)
                            {
                                error = new Win32Exception(cfgErr).Message;
                                return null;
                            }

                            IntPtr cfgBuffer = Marshal.AllocHGlobal((int)configBytesNeeded);
                            try
                            {
                                if (!QueryServiceConfigW(svc, cfgBuffer, configBytesNeeded, out _))
                                {
                                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                                    return null;
                                }

                                var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG_RAW>(cfgBuffer);
                                string? binaryPath = Marshal.PtrToStringUni(config.lpBinaryPathName);
                                if (!string.IsNullOrEmpty(binaryPath))
                                {
                                    var parsed = ProcrunPath.Parse(binaryPath);
                                    if (parsed.HasValue)
                                    {
                                        string installRoot = ProcrunPath.InstallRoot(parsed.Value.Exe);
                                        entries.Add(new ServiceEntry(serviceName, parsed.Value.Name, installRoot, isRunning));
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(cfgBuffer);
                            }
                        }
                        finally
                        {
                            CloseServiceHandle(svc);
                        }
                    }

                    if (ok || resumeHandle == 0) break;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return entries;
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
