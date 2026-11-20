using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dirused.Common;

public enum ServiceState
{
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other
}

public interface IServiceHandle : IDisposable
{
    IReadOnlyList<string>? RunningDependents(out string? error);
    (bool Ok, int Win32Error) Stop();
    (bool Ok, int Win32Error) Start();
    (ServiceState State, string? Error) QueryState();
}

public interface IServiceControl
{
    IServiceHandle? Open(string host, string key, out string? error);
}

[SupportedOSPlatform("windows")]
public sealed class RealServiceControl : IServiceControl
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint DesiredServiceAccess = SERVICE_STOP | SERVICE_START | SERVICE_QUERY_STATUS | SERVICE_ENUMERATE_DEPENDENTS;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr hService, int infoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServicesW(
        IntPtr hService,
        uint dwServiceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
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
    private struct ENUM_SERVICE_STATUS_RAW
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS ServiceStatus;
    }

    public IServiceHandle? Open(string host, string key, out string? error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Windows only";
            return null;
        }

        error = null;
        string? machineName = null;
        if (host != "." && !string.IsNullOrEmpty(host))
        {
            machineName = host.StartsWith(@"\\", StringComparison.Ordinal) ? host : @"\\" + host;
        }

        IntPtr scm = OpenSCManagerW(machineName, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        IntPtr svc = OpenServiceW(scm, key, DesiredServiceAccess);
        if (svc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseServiceHandle(scm);
            error = new Win32Exception(err).Message;
            return null;
        }

        return new RealServiceHandle(scm, svc);
    }

    private sealed class RealServiceHandle : IServiceHandle
    {
        private const uint SERVICE_CONTROL_STOP = 0x0001;
        private const uint SERVICE_ACTIVE = 0x0001;
        private const int SC_STATUS_PROCESS_INFO = 0;
        private const int ERROR_MORE_DATA = 234;

        private IntPtr scm;
        private IntPtr svc;

        public RealServiceHandle(IntPtr scm, IntPtr svc)
        {
            this.scm = scm;
            this.svc = svc;
        }

        public IReadOnlyList<string>? RunningDependents(out string? error)
        {
            error = null;
            uint bytesNeeded = 0;
            uint servicesReturned = 0;

            bool ok = EnumDependentServicesW(svc, SERVICE_ACTIVE, IntPtr.Zero, 0, out bytesNeeded, out servicesReturned);
            int lastErr = Marshal.GetLastWin32Error();
            if (!ok && lastErr != ERROR_MORE_DATA)
            {
                error = new Win32Exception(lastErr).Message;
                return null;
            }

            if (bytesNeeded == 0)
            {
                return Array.Empty<string>();
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                ok = EnumDependentServicesW(svc, SERVICE_ACTIVE, buffer, bytesNeeded, out bytesNeeded, out servicesReturned);
                if (!ok)
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return null;
                }

                var list = new List<string>();
                int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_RAW>();
                for (int i = 0; i < servicesReturned; i++)
                {
                    IntPtr ptr = IntPtr.Add(buffer, i * structSize);
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_RAW>(ptr);
                    string? name = Marshal.PtrToStringUni(entry.lpServiceName);
                    if (!string.IsNullOrEmpty(name))
                    {
                        list.Add(name);
                    }
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public (bool Ok, int Win32Error) Stop()
        {
            var status = new SERVICE_STATUS();
            if (ControlService(svc, SERVICE_CONTROL_STOP, ref status))
            {
                return (true, 0);
            }
            return (false, Marshal.GetLastWin32Error());
        }

        public (bool Ok, int Win32Error) Start()
        {
            if (StartServiceW(svc, 0, IntPtr.Zero))
            {
                return (true, 0);
            }
            return (false, Marshal.GetLastWin32Error());
        }

        public (ServiceState State, string? Error) QueryState()
        {
            int size = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, buffer, (uint)size, out _))
                {
                    int err = Marshal.GetLastWin32Error();
                    return (ServiceState.Other, new Win32Exception(err).Message);
                }

                var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buffer);
                ServiceState state = status.dwCurrentState switch
                {
                    1 => ServiceState.Stopped,
                    2 => ServiceState.StartPending,
                    3 => ServiceState.StopPending,
                    4 => ServiceState.Running,
                    _ => ServiceState.Other
                };
                return (state, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            if (svc != IntPtr.Zero)
            {
                CloseServiceHandle(svc);
                svc = IntPtr.Zero;
            }
            if (scm != IntPtr.Zero)
            {
                CloseServiceHandle(scm);
                scm = IntPtr.Zero;
            }
        }
    }
}
