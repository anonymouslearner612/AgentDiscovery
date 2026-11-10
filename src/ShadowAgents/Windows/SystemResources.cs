using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ShadowAgents;

/// <summary>
/// Keeps the scanner inside its budgets (Section 9): background CPU and I/O priority, plus a Job Object
/// with a hard CPU cap and a memory limit.
/// </summary>
/// <remarks>
/// In production the watcher creates the Job Object and starts the scanner inside it (Section 4.2), so the
/// .NET GC sees the memory limit from the first allocation. When the scanner runs on its own, it puts
/// itself in a Job Object here instead.
/// </remarks>
public static class ResourceLimiter
{
    private const uint ProcessModeBackgroundBegin = 0x00100000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectCpuRateControlInformationClass = 15;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;

    // Closing the job handle does not end the limits for a process already inside the job, but the
    // handle is kept open for the life of the process anyway so that nothing depends on that detail.
    internal static IntPtr JobHandle { get; private set; }

    /// <summary>Applies the limits and returns a short description of what was applied, for the summary record.</summary>
    public static string Apply(int cpuPercentOfOneCore, int memoryMb)
    {
        List<string> appliedLimits = new List<string>();

        // Lowers both CPU and I/O priority of this process.
        bool isBackground = SetPriorityClass(GetCurrentProcess(), ProcessModeBackgroundBegin);
        if (isBackground)
        {
            appliedLimits.Add("background-priority");
        }
        else
        {
            appliedLimits.Add("background-priority-failed(" + Marshal.GetLastWin32Error() + ")");
        }

        IntPtr jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            appliedLimits.Add($"job-object-failed({Marshal.GetLastWin32Error()})");
            return string.Join(", ", appliedLimits);
        }

        appliedLimits.Add(TrySetCpuCap(jobHandle, cpuPercentOfOneCore));
        appliedLimits.Add(TrySetMemoryLimit(jobHandle, memoryMb));

        if (!AssignProcessToJobObject(jobHandle, GetCurrentProcess()))
        {
            appliedLimits.Add($"assign-job-failed({Marshal.GetLastWin32Error()})");
            return string.Join(", ", appliedLimits);
        }

        JobHandle = jobHandle;
        return string.Join(", ", appliedLimits);
    }

    /// <summary>
    /// The Job Object CPU rate is a share of the whole machine, in 1/100 of a percent. The budget is a
    /// share of one core, so divide by the number of cores: 25% of one core on 8 cores is 3.125% (312).
    /// </summary>
    private static string TrySetCpuCap(IntPtr jobHandle, int cpuPercentOfOneCore)
    {
        int machineRate = cpuPercentOfOneCore * 100 / Environment.ProcessorCount;
        // The Job Object accepts 1 to 10,000 (0.01% to 100% of the machine).
        int limitedRate = Math.Min(Math.Max(machineRate, 1), 10_000);
        uint cpuRate = (uint)limitedRate;

        JobObjectCpuRateControlInformation cpuRateInformation = new JobObjectCpuRateControlInformation
        {
            ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
            CpuRate = cpuRate,
        };

        bool isSet = SetInformationJobObject(
            jobHandle,
            JobObjectCpuRateControlInformationClass,
            ref cpuRateInformation,
            (uint)Marshal.SizeOf<JobObjectCpuRateControlInformation>());

        if (isSet)
        {
            return "cpu-cap=" + cpuPercentOfOneCore + "%-of-one-core";
        }

        return "cpu-cap-failed(" + Marshal.GetLastWin32Error() + ")";
    }

    private static string TrySetMemoryLimit(IntPtr jobHandle, int memoryMb)
    {
        JobObjectExtendedLimitInformation limitInformation = new JobObjectExtendedLimitInformation();
        limitInformation.BasicLimitInformation.LimitFlags = JobObjectLimitProcessMemory;
        limitInformation.ProcessMemoryLimit = (UIntPtr)((ulong)memoryMb * 1024 * 1024);

        bool isSet = SetInformationJobObject(
            jobHandle,
            JobObjectExtendedLimitInformationClass,
            ref limitInformation,
            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());

        if (isSet)
        {
            return "memory-limit=" + memoryMb + "MB";
        }

        return "memory-limit-failed(" + Marshal.GetLastWin32Error() + ")";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformation
    {
        public uint ControlFlags;

        // Union in the Windows header: CpuRate, Weight, or MinRate/MaxRate. We only use CpuRate.
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectCpuRateControlInformation info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);
}

/// <summary>Facts about the machine that decide whether to scan now (Section 9: pause on battery or heavy load).</summary>
public class SystemConditions
{
    private long lastIdleTime;
    private long lastTotalTime;

    /// <summary>True when the machine is a laptop running on battery.</summary>
    public static bool IsOnBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.SystemPowerStatus status))
        {
            return false;
        }

        return status.AcLineStatus == 0;
    }

    /// <summary>True when this process runs as administrator or SYSTEM (needed for NTFS and journal reads).</summary>
    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return identity.IsSystem || principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Whole-machine CPU use, in percent, since the previous call. The first call returns 0.
    /// Windows reports idle, kernel and user time; kernel time includes idle time.
    /// </summary>
    public int SampleCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
        {
            return 0;
        }

        long totalTime = kernelTime + userTime;
        long idleDelta = idleTime - lastIdleTime;
        long totalDelta = totalTime - lastTotalTime;
        bool isFirstSample = lastTotalTime == 0;

        lastIdleTime = idleTime;
        lastTotalTime = totalTime;

        if (isFirstSample || totalDelta <= 0)
        {
            return 0;
        }

        long busyPercent = 100 - (idleDelta * 100 / totalDelta);
        long limitedPercent = Math.Min(Math.Max(busyPercent, 0), 100);
        return (int)limitedPercent;
    }
}
