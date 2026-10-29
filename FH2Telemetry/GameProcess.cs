using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FH2Telemetry;

// Read-only access to azimuth.exe. Nothing is ever written to the game.
public sealed class GameProcess : IDisposable
{
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_PRIVATE = 0x20000;
    const uint MEM_MAPPED = 0x40000;
    const uint PAGE_GUARD = 0x100;
    const uint PAGE_NOACCESS = 0x01;
    const uint WritableMask = 0x04 | 0x08 | 0x40 | 0x80;  // RW, WC, XRW, XWC

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool ReadProcessMemory(IntPtr h, ulong addr, void* buf, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nuint VirtualQueryEx(IntPtr h, ulong addr, out MEMORY_BASIC_INFORMATION info, nuint size);

    readonly IntPtr _h;
    public int Pid { get; }
    public Process Process { get; }

    GameProcess(Process p, IntPtr h) { Process = p; Pid = p.Id; _h = h; }

    public static GameProcess? Attach(string name = "azimuth")
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            var g = Attach(p);
            if (g != null) return g;
        }
        return null;
    }

    public static GameProcess? Attach(Process p)
    {
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
        return h != IntPtr.Zero ? new GameProcess(p, h) : null;
    }

    public bool HasExited
    {
        get { try { return Process.HasExited; } catch { return true; } }
    }

    public unsafe bool Read(ulong addr, Span<byte> buf)
    {
        fixed (byte* b = buf)
            return ReadProcessMemory(_h, addr, b, (nuint)buf.Length, out var n) && (int)n == buf.Length;
    }

    public bool TryU64(ulong addr, out ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        v = 0;
        if (!Read(addr, b)) return false;
        v = BitConverter.ToUInt64(b);
        return true;
    }

    public bool TryU32(ulong addr, out uint v)
    {
        Span<byte> b = stackalloc byte[4];
        v = 0;
        if (!Read(addr, b)) return false;
        v = BitConverter.ToUInt32(b);
        return true;
    }

    public bool TryF32(ulong addr, out float v)
    {
        Span<byte> b = stackalloc byte[4];
        v = 0;
        if (!Read(addr, b)) return false;
        v = BitConverter.ToSingle(b);
        return true;
    }

    // Committed, readable, writable regions (where heap objects live).
    public IEnumerable<(ulong Base, ulong Size)> WritableRegions()
    {
        ulong addr = 0x10000;
        int sz = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFF_FFFF_0000UL)
        {
            if (VirtualQueryEx(_h, addr, out var mbi, (nuint)sz) == 0) break;
            ulong next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= addr) break;
            if (mbi.State == MEM_COMMIT && (mbi.Type == MEM_PRIVATE || mbi.Type == MEM_MAPPED)
                && (mbi.Protect & WritableMask) != 0 && (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0)
                yield return (mbi.BaseAddress, mbi.RegionSize);
            addr = next;
        }
    }

    public void Dispose() => CloseHandle(_h);
}
