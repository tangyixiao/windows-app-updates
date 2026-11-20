using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsAppUpdates;

internal sealed class InstallerProcessGroup : IDisposable
{
    private readonly SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);

    public bool Attached { get; private set; }

    public void Attach(Process installer)
    {
        if (job.IsInvalid)
            return;

        // Permit installers that explicitly request to run outside the job.
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x800 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            return;

        try { Attached = AssignProcessToJobObject(job, installer.SafeHandle); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    public bool Contains(Process candidate) =>
        Attached && IsProcessInJob(candidate.SafeHandle, job, out bool belongs) && belongs;

    // There is no kill-on-close limit. Closing the handle leaves installers running.
    public void Dispose() => job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);
}
