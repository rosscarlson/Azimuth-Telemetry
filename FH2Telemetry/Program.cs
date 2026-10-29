using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FH2Telemetry;

var opt = Options.Parse(args);
if (opt == null) return 1;

Console.WriteLine("FH2Telemetry - Forza Horizon 2 (Azimuth) -> Forza Data Out");
Console.WriteLine("Reads game memory only; nothing is written to the game.\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return opt.ProbeSeconds > 0 ? Probe.Run(opt, cts.Token) : Runner.Run(opt, cts.Token);
}
catch (OperationCanceledException)
{
    return 0;
}

sealed class Options
{
    public IPEndPoint Target = new(IPAddress.Loopback, 8000);
    public bool Horizon = true;
    public int Hz = 60;
    public int ProbeSeconds;

    public static Options? Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            switch (a[i].ToLowerInvariant())
            {
                case "--target":
                    var t = Next().Split(':');
                    o.Target = new IPEndPoint(IPAddress.Parse(t[0]), int.Parse(t[1]));
                    break;
                case "--format":
                    o.Horizon = Next().ToLowerInvariant() != "dash";
                    break;
                case "--hz":
                    o.Hz = Math.Clamp(int.Parse(Next()), 10, 240);
                    break;
                case "--probe":
                    o.ProbeSeconds = 150;
                    if (i + 1 < a.Length && int.TryParse(a[i + 1], out int secs)) { o.ProbeSeconds = secs; i++; }
                    break;
                default:
                    Console.WriteLine("""
                        Usage: FH2Telemetry [--target 127.0.0.1:8000] [--format horizon|dash] [--hz 60]
                               FH2Telemetry --probe [seconds]   record a guided test drive for analysis
                        """);
                    return null;
            }
        }
        return o;
    }
}

static class Common
{
    public static GameProcess WaitForGame(CancellationToken ct)
    {
        Console.Write("Waiting for azimuth.exe ...");
        GameProcess? g;
        while ((g = GameProcess.Attach()) == null)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(1000);
        }
        Console.WriteLine($" found (pid {g.Pid}).");
        string last = "";
        while (!CarScanner.CheckFingerprint(g, out string why))
        {
            ct.ThrowIfCancellationRequested();
            if (g.HasExited) throw new InvalidOperationException("azimuth.exe exited");
            if (why != last) Console.WriteLine($"  {why}");
            last = why;
            Thread.Sleep(1000);
        }
        Console.WriteLine("Game build verified (forza_durango_release_final.exe 1.0.0.64).");
        return g;
    }

}

// Normal mode: find the player's car, send Forza Data Out packets.
static class Runner
{
    public static int Run(Options opt, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Connect(opt.Target);
        Console.WriteLine($"Sending {(opt.Horizon ? "Forza Horizon (324-byte)" : "FM7 Dash (311-byte)")} packets to {opt.Target} at {opt.Hz} Hz.");
        Console.WriteLine("Keys: N = next car candidate, R = rescan, Q = quit.\n");

        while (!ct.IsCancellationRequested)
        {
            using var g = Common.WaitForGame(ct);
            RunSession(g, udp, opt, ct);
            if (!ct.IsCancellationRequested) Console.WriteLine("\nGame closed; waiting for it to start again.");
        }
        return 0;
    }

    static void RunSession(GameProcess g, UdpClient udp, Options opt, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var block = new byte[GameLayout.BlockSize];
        var prev = new byte[GameLayout.BlockSize];
        var sample = new CarSample();
        var idle = new CarSample();

        List<ulong> cars = new();
        Task<List<ulong>>? scan = null;
        double nextScan = 0, lastChange = 0, nextStatus = 0;
        ulong selected = 0;
        bool pinned = false, paused = false, haveSample = false;
        int readFailures = 0;
        long sent = 0;
        var period = TimeSpan.FromSeconds(1.0 / opt.Hz);
        var nextTick = clock.Elapsed;

        while (!ct.IsCancellationRequested && !g.HasExited)
        {
            double t = clock.Elapsed.TotalSeconds;

            // Background scan: at start, every 10 s, or when the car is lost.
            if (scan == null && t >= nextScan)
                scan = Task.Run(() => CarScanner.Scan(g, ct), ct);
            if (scan is { IsCompleted: true })
            {
                if (scan.IsCompletedSuccessfully)
                {
                    var fresh = scan.Result;
                    // A scan can miss a car while the game reshuffles memory;
                    // keep the one we're reading as long as it still checks out.
                    if (selected != 0 && !fresh.Contains(selected) && readFailures == 0) fresh.Add(selected);
                    if (fresh.Count != cars.Count || !fresh.SequenceEqual(cars))
                        Console.WriteLine($"\n[{t,7:F1}s] {fresh.Count} car(s) found: {string.Join(", ", fresh.Select(a => a.ToString("X")))}");
                    cars = fresh;
                    // Follow the player into a new car (unless N picked one).
                    if (!pinned && selected != 0)
                    {
                        ulong best = PickCar(g, cars);
                        if (best != 0 && best != selected)
                        {
                            selected = best;
                            Console.WriteLine($"\n[{t,7:F1}s] Switched to player car {selected:X}");
                        }
                    }
                }
                scan = null;
                nextScan = t + 10;
            }

            HandleKeys(ref selected, ref pinned, cars, ref nextScan, t);

            if (selected == 0 || !cars.Contains(selected))
            {
                selected = PickCar(g, cars);
                pinned = false;
                if (selected != 0) Console.WriteLine($"\n[{t,7:F1}s] Using car {selected:X}");
            }

            CarSample outSample = idle;
            if (selected != 0)
            {
                if (g.Read(selected, block) && CarScanner.IsCar(block))
                {
                    readFailures = 0;
                    if (!block.AsSpan().SequenceEqual(prev)) { lastChange = t; block.CopyTo(prev, 0); }
                    // Memory frozen for a while = game paused (or alt-tabbed).
                    bool nowPaused = t - lastChange >= 0.4;
                    if (nowPaused != paused)
                        Console.WriteLine($"\n[{t,7:F1}s] {(nowPaused ? "Game paused (car data frozen)" : "Resumed")}");
                    paused = nowPaused;
                    // Not the player's car any more (new car, replay): look again soon.
                    if (!pinned && !CarReader.IsPlayer(block) && nextScan > t + 1) nextScan = t + 1;
                    if (!paused)
                    {
                        CarReader.Fill(block, sample);
                        haveSample = true;
                        outSample = sample;
                    }
                }
                else if (++readFailures < 30)
                {
                    // A failed read is usually transient; repeat the last sample.
                    if (haveSample && !paused) outSample = sample;
                    if (readFailures == 1) Console.WriteLine($"\n[{t,7:F1}s] Read of car {selected:X} failed; retrying.");
                }
                else
                {
                    Console.WriteLine($"\n[{t,7:F1}s] Car {selected:X} gone; rescanning.");
                    cars.Remove(selected);
                    selected = 0;
                    haveSample = false;
                    readFailures = 0;
                    nextScan = 0;
                }
            }

            var pkt = opt.Horizon
                ? ForzaPacket.EncodeHorizon(outSample, (uint)clock.ElapsedMilliseconds)
                : ForzaPacket.EncodeDash(outSample, (uint)clock.ElapsedMilliseconds);
            try { udp.Send(pkt, pkt.Length); sent++; } catch (SocketException) { }

            if (t >= nextStatus)
            {
                nextStatus = t + 0.25;
                Status(cars.Count, selected, pinned, outSample, sample, block, sent);
            }

            nextTick += period;
            var wait = nextTick - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            else nextTick = clock.Elapsed;
        }
    }

    // The player's car carries the player flag; failing that, take a car
    // that isn't AI-controlled, then any car.
    static ulong PickCar(GameProcess g, List<ulong> cars)
    {
        var b = new byte[GameLayout.BlockSize];
        ulong notAi = 0, any = 0;
        foreach (var c in cars)
        {
            if (!g.Read(c, b) || !CarScanner.IsCar(b)) continue;
            if (CarReader.IsPlayer(b)) return c;
            if (notAi == 0 && !CarReader.IsAi(b)) notAi = c;
            if (any == 0) any = c;
        }
        return notAi != 0 ? notAi : any;
    }

    static void HandleKeys(ref ulong selected, ref bool pinned, List<ulong> cars, ref double nextScan, double t)
    {
        if (Console.IsInputRedirected) return;
        while (Console.KeyAvailable)
        {
            var k = Console.ReadKey(true).Key;
            if (k == ConsoleKey.N && cars.Count > 0)
            {
                int i = cars.IndexOf(selected);
                selected = cars[(i + 1) % cars.Count];
                pinned = true;
                Console.WriteLine($"\n[{t,7:F1}s] Switched to car {selected:X} ({cars.IndexOf(selected) + 1}/{cars.Count})");
            }
            else if (k == ConsoleKey.R) nextScan = 0;
            else if (k == ConsoleKey.Q) throw new OperationCanceledException();
        }
    }

    static void Status(int count, ulong car, bool pinned, CarSample o, CarSample s, byte[] block, long sent)
    {
        string state = car == 0 ? "no car" : o.Driving ? "driving" : "paused";
        byte rawGear = car != 0 ? block[GameLayout.Abs(GameLayout.Icd.Gear)] : (byte)0;
        string line = $"\r{state,-7} cars:{count} {(pinned ? "*" : " ")} rpm:{s.Rpm,6:F0}/{s.RedlineRpm,5:F0} gear:{s.Gear,2}(raw {rawGear}) " +
                      $"{s.SpeedMps * 3.6f,5:F0}km/h {s.SpeedMps * 2.23694f,4:F0}mph thr:{s.Throttle,4:F2} brk:{s.Brake,4:F2} " +
                      $"clu:{s.Clutch,4:F2} hb:{s.Handbrake,4:F2} str:{s.Steer,5:F2} sent:{sent}";
        int width = Console.IsOutputRedirected ? 0 : Console.BufferWidth;
        Console.Write(line.Length < width ? line.PadRight(width - 1) : line);
    }
}
