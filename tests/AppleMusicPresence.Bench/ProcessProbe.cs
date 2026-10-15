using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AppleMusicPresence.Bench;

internal sealed record Sample(
    double Seconds, double WorkingSetMB, double PrivateWorkingSetMB, double PrivateBytesMB, double PeakWorkingSetMB,
    double CpuMs, double CpuCyclesM, long ContextSwitches, int Threads, uint Handles, uint GdiObjects, uint UserObjects, long PageFaults);

/// <summary>Reads OS-level counters of another process without injecting anything into it.</summary>
internal sealed class ProcessProbe : IDisposable
{
    private const double MB = 1024 * 1024;
    private readonly nint _handle;
    private Dictionary<(long Thread, long Created), uint> _threadSwitches = new();
    private long _switches;
    public int Pid { get; }

    public ProcessProbe(int pid)
    {
        Pid = pid;
        _handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, pid);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({pid})");
    }

    public nint Handle => _handle;

    public Sample Read(double seconds)
    {
        var memory = new Native.MemoryCounters { cb = (uint)Marshal.SizeOf<Native.MemoryCounters>() };
        if (!Native.GetProcessMemoryInfo(_handle, ref memory, memory.cb)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Native.GetProcessTimes(_handle, out _, out _, out var kernel, out var user);
        Native.QueryProcessCycleTime(_handle, out var cycles);
        Native.GetProcessHandleCount(_handle, out var handles);
        var threads = ThreadActivity(Pid);
        // Summing live threads goes backwards when a thread exits, so accumulate per-thread deltas
        // instead. A thread that exits between two reads loses only its switches since the last read.
        foreach (var (thread, count) in threads)
            _switches += (long)count - _threadSwitches.GetValueOrDefault(thread);
        _threadSwitches = threads;
        return new(Math.Round(seconds, 3), memory.WorkingSetSize / MB, memory.PrivateWorkingSetSize / MB, memory.PrivateUsage / MB,
            memory.PeakWorkingSetSize / MB, (kernel + user) / 10_000.0, cycles / 1e6, _switches, threads.Count, handles,
            Native.GetGuiResources(_handle, 0), Native.GetGuiResources(_handle, 1), memory.PageFaultCount);
    }

    /// <summary>Context switches per live thread, keyed by thread id and creation time (SystemProcessInformation).</summary>
    private static Dictionary<(long Thread, long Created), uint> ThreadActivity(int pid)
    {
        var size = 2 * 1024 * 1024;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = Native.NtQuerySystemInformation(5, buffer, size, out var needed);
                if (status == unchecked((int)0xC0000004)) { size = Math.Max(size * 2, needed + 65536); continue; }
                if (status != 0) throw new Win32Exception($"NtQuerySystemInformation 0x{status:X8}");
                var entry = buffer;
                while (true)
                {
                    if (Marshal.ReadIntPtr(entry, 0x50) == pid)
                    {
                        var count = Marshal.ReadInt32(entry, 0x04);
                        var threads = new Dictionary<(long, long), uint>(count);
                        for (var i = 0; i < count; i++)
                        {
                            var thread = entry + 0x100 + i * 0x50;
                            threads[(Marshal.ReadInt64(thread, 0x30), Marshal.ReadInt64(thread, 0x10))] = (uint)Marshal.ReadInt32(thread, 0x40);
                        }
                        return threads;
                    }
                    var next = Marshal.ReadInt32(entry, 0);
                    if (next == 0) return new();
                    entry += next;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public void Dispose() => Native.CloseHandle(_handle);
}
