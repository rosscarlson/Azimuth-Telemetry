using System.Runtime.InteropServices;

namespace FH2Telemetry;

// Finds every CCarDynamics object in the game's heap by its vtable pair.
public static class CarScanner
{
    const int Chunk = 8 << 20;

    public static bool CheckFingerprint(GameProcess g, out string why)
    {
        if (!g.TryU32(GameLayout.ImageBase + 0x3C, out uint lfanew)
            || !g.TryU32(GameLayout.ImageBase + lfanew + 8, out uint stamp))
        {
            why = "the game image is not mapped at 0x140000000 yet (still loading?)";
            return false;
        }
        if (stamp != GameLayout.PeTimestamp)
        {
            why = $"unexpected game build (PE timestamp {stamp:X8}, expected {GameLayout.PeTimestamp:X8})";
            return false;
        }
        if (!g.TryU64(GameLayout.VtRpmSlot, out ulong f) || f != GameLayout.RpmGetter)
        {
            why = "the car-dynamics vtable does not match this build";
            return false;
        }
        why = "";
        return true;
    }

    public static bool IsCar(ReadOnlySpan<byte> block)
    {
        if (block.Length < GameLayout.ICarDynamicsOffset + 8) return false;
        ulong a = MemoryMarshal.Read<ulong>(block);
        ulong b = MemoryMarshal.Read<ulong>(block[GameLayout.ICarDynamicsOffset..]);
        return (a == GameLayout.VtCarDynamics && b == GameLayout.VtICarDynamics)
            || (a == GameLayout.VtCarDynamicsRc && b == GameLayout.VtICarDynamicsRc);
    }

    public static List<ulong> Scan(GameProcess g, CancellationToken ct = default)
    {
        var found = new List<ulong>();
        var buf = new byte[Chunk + 8];
        Span<byte> probe = stackalloc byte[8];
        foreach (var (rbase, rsize) in g.WritableRegions())
        {
            for (ulong off = 0; off < rsize; off += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Chunk, rsize - off);
                var span = buf.AsSpan(0, n);
                if (g.Read(rbase + off, span))
                {
                    Search(g, rbase + off, span, found);
                    continue;
                }
                // Part of the chunk changed under us (the game freed or
                // re-protected pages); retry it in small pieces.
                for (int p = 0; p < n; p += Piece)
                {
                    var piece = buf.AsSpan(p, Math.Min(Piece, n - p));
                    if (g.Read(rbase + off + (ulong)p, piece)) Search(g, rbase + off + (ulong)p, piece, found);
                }
            }
        }
        return found;
    }

    const int Piece = 64 << 10;

    static void Search(GameProcess g, ulong at, ReadOnlySpan<byte> span, List<ulong> found)
    {
        var q = MemoryMarshal.Cast<byte, ulong>(span);
        for (int i = 0; i < q.Length; i++)
        {
            ulong v = q[i];
            if (v != GameLayout.VtCarDynamics && v != GameLayout.VtCarDynamicsRc) continue;
            ulong addr = at + (ulong)i * 8;
            ulong want = v == GameLayout.VtCarDynamics ? GameLayout.VtICarDynamics : GameLayout.VtICarDynamicsRc;
            if (g.TryU64(addr + GameLayout.ICarDynamicsOffset, out ulong sub) && sub == want)
                found.Add(addr);
        }
    }
}
