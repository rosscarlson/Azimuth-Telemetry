# Horizon Telemetry

**Telemetry to UDP for Forza Horizon 2 and 3 running on the Azimuth emulator.** A Windows app that
reads the game's own car physics and sends them out as standard Forza "Data Out" UDP packets (the
Forza Horizon 4/5 324-byte format, or Forza Motorsport 7 Dash), so anything that understands Forza
telemetry can use them: SimHub with a ButtKicker or shaker rig, dashboards, motion rigs, data
loggers, or your own scripts.

Along with the telemetry it also handles a few things around Azimuth:

- **Wheel bindings**: sets Azimuth's racing wheel, pedal and button bindings, with a live picture of
  what's bound to what.
- **Camera**: field of view and seat position while you drive, per view, from keys or wheel buttons.
- **Render Aspect Ratio**: renders at any resolution (e.g. 10240 x 1440 for 64:9 triple screens).

The game is detected automatically (FH2 or FH3). The packet encoder comes from the FH1 Pinyon Shift
telemetry fork. Force feedback isn't touched; Azimuth 1.0 already runs the game's own FFB on the
wheel.

The app **reads** the game's memory and sends packets; it injects nothing. It only ever writes to the
game for the Camera feature (the FOV and seat values of the camera views, and only once you change
one), and only changes Azimuth with the optional Render Aspect Ratio shim.

How the fields were found: [docs/FINDINGS.md](docs/FINDINGS.md).

## Status

All from the game's own physics, for both FH2 and FH3, confirmed with a recorded probe drive in
each:

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

Get `HorizonTelemetry-v0.1.zip` from [Releases](../../releases), unzip it and put
`HorizonTelemetry.exe` in Azimuth's folder (next to `azimuth.exe`). It needs the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) and supports
`forza_durango_release_final.exe` 1.0.0.64 (Forza Horizon 2) and the Xbox One Forza Horizon 3
1.0.125.0 on Azimuth 1.0.x. It says so if Azimuth is running something else.

## Build

```
dotnet test -c Release
dotnet publish HorizonTelemetry -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Projects: `HorizonTelemetry.Core` (game reading, packet encoder, engine, probe), `HorizonTelemetry`
(the WPF app), `HorizonTelemetry.Tests`.

## Use

1. Point your telemetry receiver at the app's address and port (default `127.0.0.1:8000`). In
   SimHub, for example, pick **Forza Horizon 4** (or 5) and set its UDP port to match.
2. Run `HorizonTelemetry.exe`. It works out which game Azimuth is running (FH2 or FH3) by itself.
3. Press **Start** (or your hotkey) and start the game in Azimuth, in either order. With
   **Start Azimuth** ticked, Start also launches `azimuth.exe` (put `HorizonTelemetry.exe` in
   Azimuth's folder, or pick the folder under Render Aspect Ratio).
4. If the live readout isn't your car (RPM and gear don't match), press **Next car**.

**Send telemetry to** (left): target address and port, packet format (Forza Horizon 324-byte or
FM7 Dash 311-byte) and rate. One set for every game; bindings and the render resolution are
Azimuth's own and apply to every game it runs too.

**Hotkey:** any key (with Ctrl/Alt/Shift) or a button on a wheel, button box or controller starts
and stops telemetry, including while the game has focus. It can say
"telemetry on/off" so you know without looking.

Settings are saved in `%APPDATA%\HorizonTelemetry\settings.json`.

## Camera (FOV and seat position)

The **Camera** card changes the field of view of the view you're driving in (cockpit, dashboard,
hood, bumper, chase near and far), and the seat position in the cockpit, dashboard, hood and bumper
views, while you drive. Each view keeps its own change. There's a key or a wheel/button-box button
for each step: FOV 1 degree at a time, seat 1 cm forward, back, up, down, left or right, and Reset
(the view in use back to the game's own). Defaults are Ctrl + the number pad (+ and - for FOV, 8/2,
9/3, 4/6, 5 to reset); holding a key repeats. Changes are kept per game and view and put back
whenever the game resets the view (a new car, switching cameras).

This is the one place the app writes to the game: only those values of the camera views, and
nothing until you change something.

## Render Aspect Ratio (triple screens, 64:9)

Azimuth 1.0.3 offers 16:9 and your main screen's own aspect, but nothing wider than 4:1, so a
2560 + 5120 + 2560 triple (10240x1440, 64:9) can't be picked. **Render Aspect Ratio** (Telemetry
tab, Azimuth games) sets the resolution Azimuth renders at. Enter the **Width** and **Height**
(for example 10240 x 1440, 64:9) and press **Set**: it puts a small `dinput8.dll` next to
`azimuth.exe` (source in `AzimuthSpan/`) that adds that aspect to the launcher's **Aspect** list
(up to 8:1) and makes **Fullscreen** that size, centred on the main screen. The size is saved in
`azimuth-span.ini` next to `azimuth.exe` and applies the next time Azimuth starts. **Clear**
removes both.

It changes Azimuth in memory only, only inside `azimuth.exe`, and only when Azimuth's code
matches 1.0.3 exactly; other versions are left alone. Press Set with Azimuth closed the first time, then pick the new aspect
and Fullscreen in the launcher. Each start is logged in
`azimuth-span.log` next to `azimuth.exe`.

Building it needs the Visual Studio 2022 Build Tools (C++); `dotnet build` runs
`AzimuthSpan\build.cmd` and embeds the DLL. `AzimuthSpan\tests\build_test.cmd` checks it.

## Wheel bindings (Controls tab)

Azimuth's own Controls page can grab the wrong axis every time (for example a Fanatec base's
"Combined Pedals" axis while you press a separate pedal set). The **Controls** tab sets
the bindings instead. A live picture of the wheel, pedals, buttons and shifter at the top shows
what each control is bound to as you move it (faint = not bound). **Bind…** listens for the control (turn the wheel right; press a pedal fully
and let go; press a button, hat or D-pad direction) on any connected device, and **Save to
Azimuth** writes them, plus rotation and force feedback, to `data\azimuth.ini` with Azimuth
closed. The first save keeps the original as `azimuth.ini.before-horizon-telemetry`. Azimuth keeps
one set of bindings for every game it runs. Don't re-detect them on Azimuth's Controls page
afterwards, or it may replace them.

The app reads controllers through DirectInput with the same data format as Azimuth, so axis
numbers match. Extra axes that DirectInput places beyond the standard eight (some wheel bases'
dials) can't be bound here.

By hand, with the launcher closed (keep the file UTF-16), each axis line is
`<pid><vid>:axis:<slot>:<from>:<to>`, e.g. `wheel_throttle=57572497:axis:2:0:65535` for axis
slot 2 of a device with VID `2497`, PID `5757`. The slot numbers and IDs for every device are
listed in `data\logs\azimuth.log` (lines starting `wheel:`). Buttons are
`<pid><vid>:button:<n>` and hats `<pid><vid>:hat:0:up`. Don't open the Controls page afterwards,
or it may overwrite them.

## Probe drive (labels more fields)

**Record probe** (Telemetry tab) records ~2.5 minutes of every car's physics while spoken
cues tell you what to drive, so you never need to alt-tab. Get into a car in free roam first. It
writes `probes\probe-<game>-*.bin` next to the exe; `python -I tools\analyze_probe.py <file>`
analyses it.
