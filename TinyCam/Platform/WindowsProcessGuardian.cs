using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TinyCam.Models;

namespace TinyCam.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessGuardian : IProcessGuardian
{
    private IntPtr _hJob = IntPtr.Zero;
    private readonly object _lock = new();

    public ProcessStartInfo PrepareStartInfo(ProcessStartInfo psi, TinyCamConfig cfg)
        => psi;

    public void Attach(Process proc, TinyCamConfig cfg)
    {
        if (!cfg.UseJobObjectKillOnClose) return;
        lock (_lock)
        {
            if (_hJob == IntPtr.Zero)
            {
                _hJob = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE }
                };
                int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_hJob, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)len);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            _ = AssignProcessToJobObject(_hJob, proc.Handle);
        }
    }

    public async Task<bool> TryGracefulTerminateAsync(Process proc, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await proc.WaitForExitAsync(cts.Token);
            return true;
        }
        catch { return false; }
    }

    public bool TryKill(Process proc, int waitMs)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(waitMs);
            }
            return true;
        }
        catch { return false; }
    }

    private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public int LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public int ActiveProcessLimit; public long Affinity;
        public int PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    private enum JobObjectInfoType { ExtendedLimitInformation = 9 }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
