using System.Runtime.InteropServices;

namespace AppleMusicPresence.Bench;

internal static class Native
{
    internal const uint ProcessQueryInformation = 0x0400, ProcessVmRead = 0x0010;
    internal const uint MemCommit = 0x1000, MemImage = 0x1000000, MemMapped = 0x40000;
    internal const uint ExecuteProtection = 0x10 | 0x20 | 0x40 | 0x80;
    internal const uint WmClose = 0x0010, WmNull = 0x0000, SmtoAbortIfHung = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryCounters
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WorkingSetExInformation
    {
        public nint VirtualAddress;
        public nuint VirtualAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("psapi.dll", SetLastError = true)] internal static extern bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation info, nuint length);
    [DllImport("psapi.dll", SetLastError = true)] internal static extern bool QueryWorkingSetEx(nint process, [In, Out] WorkingSetExInformation[] info, uint size);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetMappedFileNameW(nint process, nint address, char[] name, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool QueryProcessCycleTime(nint process, out ulong cycles);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessHandleCount(nint process, out uint count);
    [DllImport("user32.dll")] internal static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("ntdll.dll")] internal static extern int NtQuerySystemInformation(int infoClass, nint buffer, int length, out int returnLength);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint hwnd, char[] text, int max);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] internal static extern uint TimeBeginPeriod(uint milliseconds);
    [DllImport("user32.dll")] internal static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
