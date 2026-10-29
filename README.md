# FH2Telemetry

Forza Data Out for **Forza Horizon 2 on Azimuth**, so SimHub (and a ButtKicker/shaker setup) gets
the same UDP packet as Forza Horizon 4/5. It reuses the packet encoder from the FH1 PinyonShift
telemetry fork.

It's a separate app that **reads** the game's memory: no DLL injection, nothing written to the
game, no changes to Azimuth. Force feedback isn't touched; Azimuth 1.0's own racing-wheel support
already runs the game's native FFB on the wheel.

How the fields were found: [docs/FINDINGS.md](docs/FINDINGS.md).

## Status

All from the game's own physics, confirmed with a recorded probe drive:

| Field | |
|---|---|
| RPM, idle RPM, max RPM, torque, power | yes |
| Speed, position, forward velocity | yes |
| Throttle, brake, clutch, handbrake, steering (after assists) | yes |
| Gear (R, 1-n) | yes |
| Wheel rotation speed (x4) | yes |
| Suspension travel, metres and normalised (x4) | yes |
| Tyre slip ratio and slip angle (x4) | yes (scaled so 1 is about peak grip) |
| Surface rumble | not found yet |
| Player car among AI/traffic | automatic (player flag); `N` still overrides |

## Download

Get `FH2Telemetry-v0.1.zip` from [Releases](../../releases) and unzip it anywhere. It needs the
[.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) and works with
`forza_durango_release_final.exe` 1.0.0.64 on Azimuth 1.0.x. It checks the game version on start
and says so if it doesn't match.

## Build

```
dotnet test -c Release
dotnet publish FH2Telemetry -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

## Use

1. In SimHub, pick **Forza Horizon 4** (or 5), and set the game's UDP port to match `--target`
   (default `127.0.0.1:8000`).
2. Start FH2 in Azimuth, then run `FH2Telemetry.exe` (any order).
3. The status line shows what's being sent. If it's showing another car's data (RPM/gear don't
   match yours), press `N` to cycle cars.

Options: `--target host:port`, `--format horizon|dash` (324- or 311-byte packet), `--hz 60`.

## Wheel binding tip (Azimuth 1.0)

If Azimuth's Controls page grabs the wrong axis every time (for example a Fanatec base's
"Combined Pedals" axis), you can set the bindings by hand in Azimuth's `data\azimuth.ini` with
the launcher closed (keep it UTF-16). Each axis line is
`<pid><vid>:axis:<slot>:<from>:<to>`, e.g. `wheel_throttle=57572497:axis:2:0:65535` for axis
slot 2 of a device with VID `2497`, PID `5757`. The slot numbers and IDs for every device are
listed in `data\logs\azimuth.log` (lines starting `wheel:`). Buttons are
`<pid><vid>:button:<n>` and hats `<pid><vid>:hat:0:up`. Don't open the Controls page afterwards,
or it may overwrite them.

## Probe drive (labels the remaining fields)

`dist\FH2Telemetry.exe --probe` records ~2.5 minutes of every car's physics block while spoken
cues tell you what to do, so you never need to alt-tab. It writes `dist\probes\probe-*.bin`;
`python -I tools\analyze_probe.py <file>` analyses it.
