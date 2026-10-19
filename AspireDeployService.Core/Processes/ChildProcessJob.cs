using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AspireDeployService.Core.Processes;

/// <summary>
/// Implementation plan D15 (1): one Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE for the whole
/// app. Every child process is assigned to it, so Windows kills the children (and their children, which
/// inherit the job) when this app exits for any reason: crash, Task Manager, or "Stop Debugging".
/// On other platforms it does nothing.
/// </summary>
public sealed partial class ChildProcessJob : IDisposable
{
    public static ChildProcessJob Shared { get; } = new();

    private IntPtr _handle;
    private int _warned;

    internal ChildProcessJob()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)size))
                _handle = handle;
            else
                CloseHandle(handle);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public bool IsActive => _handle != IntPtr.Zero;

    /// <summary>Assigns a started process. A failure is logged once and never fails the run (plan R21).</summary>
    public bool TryAssign(Process process, ILogger? logger = null)
    {
        if (!IsActive)
            return false;
        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle))
                return true;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                logger?.LogWarning(ex, "Could not add a child process to the kill-on-close job; it may outlive the app.");
            return false;
        }
    }

    /// <summary>Closing the last handle kills every process still in the job.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
