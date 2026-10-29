# How the telemetry fields were found

Target: `forza_durango_release_final.exe` 1.0.0.64 (PE timestamp `565501ED`), the build fh2wheel
targets. Azimuth maps it at its preferred base `0x140000000`, so every address below is static.
All found by static analysis with `tools/pe_an.py` (Python + capstone + pefile); no Ghidra needed.

## The way in: the game's own Telemetry HUD

FH2 ships a telemetry overlay (strings `TelemetryViewModel`, `TireViewModel`,
`SuspensionViewModel`, `IDS_HudOptionValue_TelemetryOn`). Its properties are registered by name
with the game's reflection system (`RPM`, `SteeringAngle`, `BrakeMeter`, `HandBrakeMeter`,
`AccelMeter`, `ClutchMeter`, `EngineTorque`, `EnginePower`, `ClutchState`, `Boost`, `GForce`; per
corner `Wear`, `Friction`, `Camber`, `Pressure`, `Temperature*`, `Travel`).

- The `RPM` property getter (`0x140c2c170`) reads `TelemetryViewModel+0x1D8`.
- The function that fills `+0x1D8..+0x224` is `0x140c3e5c0`. It takes the car with
  `vm[0xC8]` (a `ForzaCar`) `+0x20 -> vfunc 0x70`, then calls that interface's virtual getters:
  `0x1A0` RPM, `0x910` steering, `0x158` inputs, `0x8D8` clutch, `0x3A0` torque, `0x3E0` power,
  `0x8D0` clutch state, `0x410/0x420/0x3B0` boost.
- That interface is **`ICarDynamics`**, the sub-object at `+0x3A0` of **`CCarDynamics`** (MSVC
  RTTI). Its vtable `0x142EB4BB8` has 381 entries; the getters are mostly one-liners, which gives
  the field layout directly (`tools/icardyn_vtable.txt` lists them all).

## Finding the object at runtime

`CCarDynamics` lives on the heap, but its vtables are static: primary `0x142EB4B08` at `+0`, and
`0x142EB4BB8` at `+0x3A0` (or `0x142EB5D68` / `0x142EB5E18` for the
`TRefCountedObjectThreadSafe<CCarDynamics>` variant). Scanning writable memory for that pair finds
every car, each session, with no pointer chains. Fingerprint check: vtable slot `0x142EB4BB8+0x1A0`
must be `0x1412C81A0`.

## Field map (offsets from the ICarDynamics pointer = CCarDynamics + 0x3A0)

| Offset | Field | Source getter (vtable entry) |
|---|---|---|
| `+0x008` / `+0x058` | steering angle / max (HUD divides) | 290 (`0x910`) |
| `+0x148` | engine speed, rad/s (x9.5493 = RPM) | 52 (`0x1A0`), HUD RPM |
| `+0x18C` | engine torque | 116 (`0x3A0`) |
| `+0x194` | engine power | 124 (`0x3E0`) |
| `+0x1CC` | engine max speed, rad/s (HUD clamps RPM to it) | 102/103 |
| `+0x5C0` | engine idle, rad/s (`(cur-idle)/(max-idle)` = normalised RPM) | 104/105 |
| `+0xA8C` | clutch 0-1 | 283 (`0x8D8`) |
| `+0xAA0` | gear (byte), indexes the ratio table | 51, 245 |
| `+0xABC` | number of gears | 222, 328 |
| `+0xAC0` | gear ratios, 12-byte stride | 244 |
| `+0xB90` | final drive | 286 |
| `+0xDF8` | inputs: `+0` throttle, `+4` brake, `+0x10` handbrake | 43 (`0x158`) |
| `+0xEF0` | world position (vec4) | 144 |
| `+0x11B0 + i*0x4B0` | per-wheel blocks (4 wheels) | 48, 59, 68, 140-218 |
| wheel `+0x0C` (`+0x11BC`) | wheel spin, rad/s | 152/153 |
| wheel `+0x490` (`+0x1640`) | pointer to tyre data; `+8` = diameter in inches | 143 |
| `+0xE28` | speed, m/s (always positive) | 84 (`0x2A0`) |
| `+0x4EE7` | byte, 1 = player's car (0 on all 44 AI/traffic cars in the probe) | 377 |
| `+0xFBC` | int, 1 = AI-controlled (0 on the player's car) | setter 20 |

## Labelled from the probe drive (2026-10-29)

`dist/probes/probe-20261029-111935.bin`, analysed with `tools/extract_probe.py` + `tools/pa.py`.
Wheel order is FL, FR, RL, RR (rears spin up on the RWD test car; the outer wheel spins faster
in a turn). Offsets within each `0x4B0` wheel block:

| Wheel offset | Field | Evidence |
|---|---|---|
| `+0x000` | suspension position, m (more negative = extended) | fronts rise, rears drop under braking; range inside droop..bump |
| `+0x00C` | wheel spin, rad/s | negative in reverse |
| `+0x024` | slip angle, rad | 0 in straight braking, ~0.007 cruising, 0.3-0.5 on the rears in a handbrake slide, sign follows the slide |
| `+0x2C4` | contact-patch longitudinal velocity, m/s | equals spin x radius within 0.4 % at cruise |
| `+0x3C0` | tyre radius, m | 0.3216 front, 0.3179 rear |
| `+0x3F4` | suspension position at full droop | -0.181 |
| `+0x3FC` | suspension position at the bump stop | -0.0744 |
| `+0x484` | suspension position at rest | -0.1355 |

Gear: raw `0` is reverse (ratio table entry 0 is negative; the game auto-selects it when the brake
is held at a stop), `1..n` forward, `+0xABC` counts reverse too. Steering: -1 full left, +1 full
right. Not fields: `+0x2C8` looks like slip-velocity magnitude (it spikes in straight braking);
`+0x388..+0x394` exist only on the front wheels (steering related).

Derived in `CarReader`: slip ratio = `(spin x radius - v_long) / max(|v_long|, 2)`, and both slips
are divided by nominal peaks (0.10, 0.14 rad) so 1 is roughly the limit of grip.

## Still open

- Surface rumble: no per-wheel field found yet (SimHub's road effects work from suspension).
- Torque/power units, boost, tyre temperatures (the HUD has them).
