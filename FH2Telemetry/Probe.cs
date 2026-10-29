using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace FH2Telemetry;

// Records every car-dynamics object while you follow spoken cues, so the
// remaining fields (suspension, slip, rumble, gear numbering, which car is
// the player's) can be labelled offline. You never need to leave the game.
//
// File: "FH2PROBE" u32 version u32 blockSize, then gzip'd records:
//   1 = frame:  f64 t, u32 count, count x (u64 address, blockSize bytes)
//   2 = marker: f64 t, u16 length, UTF-8 text
static class Probe
{
    static readonly (double At, string Say)[] Script =
    {
        (0,   "Probe started. Sit still in neutral with the engine idling."),
        (10,  "Stay stopped. Rev to the limiter three times."),
        (22,  "Accelerate hard through the gears to about sixty miles per hour."),
        (37,  "Hold a steady fifty miles per hour."),
        (47,  "Brake hard to a stop."),
        (55,  "Select reverse and back up slowly."),
        (63,  "Stop. Drive forward slowly, steer full left, then full right."),
        (75,  "Drive off road over rough ground, bumps or kerbs."),
        (100, "Back on the road. Do a handbrake turn or a big slide."),
        (115, "Pause the game now, and wait."),
        (123, "Unpause and drive normally."),
    };

    public static int Run(Options opt, CancellationToken ct)
    {
        using var g = Common.WaitForGame(ct);
        Console.WriteLine("Get into a car in free roam, then press Enter here and switch back to the game.");
        Console.WriteLine("Cues will be spoken (and beeped); you don't need to come back until it says it's finished.");
        if (!Console.IsInputRedirected) Console.ReadLine();

        Console.WriteLine("Scanning for cars ...");
        var cars = CarScanner.Scan(g, ct);
        Console.WriteLine($"{cars.Count} car(s): {string.Join(", ", cars.Select(a => a.ToString("X")))}");
        if (cars.Count == 0) { Console.WriteLine("No car found. Are you in a car (not a menu)?"); return 2; }

        string dir = Path.Combine(AppContext.BaseDirectory, "probes");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
        using var fs = File.Create(path);
        fs.Write(Encoding.ASCII.GetBytes("FH2PROBE"));
        fs.Write(BitConverter.GetBytes(1u));
        fs.Write(BitConverter.GetBytes((uint)GameLayout.BlockSize));
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var w = new BinaryWriter(gz);

        using var voice = new Voice();
        Console.WriteLine("Starting in 5 seconds - switch to the game now.");
        for (int i = 5; i > 0 && !ct.IsCancellationRequested; i--) { Thread.Sleep(1000); }

        var clock = Stopwatch.StartNew();
        var block = new byte[GameLayout.BlockSize];
        int cue = 0;
        double nextScan = 10;
        long frames = 0;
        Task<List<ulong>>? scan = null;
        var period = TimeSpan.FromSeconds(1.0 / 15);
        var next = clock.Elapsed;

        while (!ct.IsCancellationRequested && !g.HasExited)
        {
            double t = clock.Elapsed.TotalSeconds;
            if (t >= opt.ProbeSeconds) break;

            while (cue < Script.Length && t >= Script[cue].At)
            {
                Marker(w, t, Script[cue].Say);
                Console.WriteLine($"[{t,6:F1}s] {Script[cue].Say}");
                voice.Say(Script[cue].Say);
                cue++;
            }

            if (scan == null && t >= nextScan) scan = Task.Run(() => CarScanner.Scan(g, ct), ct);
            if (scan is { IsCompleted: true })
            {
                if (scan.IsCompletedSuccessfully)
                {
                    foreach (var c in scan.Result.Where(c => !cars.Contains(c)))
                    {
                        cars.Add(c);
                        Marker(w, t, $"new car {c:X}");
                    }
                }
                scan = null;
                nextScan = t + 10;
            }

            var live = new List<ulong>();
            var blocks = new List<byte[]>();
            foreach (var c in cars)
            {
                if (g.Read(c, block) && CarScanner.IsCar(block)) { live.Add(c); blocks.Add((byte[])block.Clone()); }
            }
            w.Write((byte)1);
            w.Write(t);
            w.Write((uint)live.Count);
            for (int i = 0; i < live.Count; i++) { w.Write(live[i]); w.Write(blocks[i]); }
            frames++;

            next += period;
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait); else next = clock.Elapsed;
        }

        Marker(w, clock.Elapsed.TotalSeconds, "end");
        voice.Say("Probe finished. Thank you.");
        Thread.Sleep(2500);
        w.Flush();
        Console.WriteLine($"\nRecorded {frames} frames to {path}");
        return 0;
    }

    static void Marker(BinaryWriter w, double t, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        w.Write((byte)2);
        w.Write(t);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    // Windows' built-in speech (SAPI), with a beep if it isn't available.
    sealed class Voice : IDisposable
    {
        readonly dynamic? _v;

        public Voice()
        {
            try
            {
                var t = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (t != null) _v = Activator.CreateInstance(t);
            }
            catch { _v = null; }
        }

        public void Say(string text)
        {
            try { Console.Beep(880, 150); } catch { }
            try { _v?.Speak(text, 1 /* SVSFlagsAsync */); } catch { }
        }

        public void Dispose() { }
    }
}
