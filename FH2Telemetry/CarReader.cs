using System.Runtime.InteropServices;
using static FH2Telemetry.GameLayout;

namespace FH2Telemetry;

// Turns one CCarDynamics memory block into a CarSample. Pure, so it is
// unit-tested with synthetic blocks. Every field comes from the game.
public static class CarReader
{
    // Physical slip at which a typical tyre peaks, used to scale slip so that
    // 1 is roughly the limit of grip, as Forza's Data Out reports it.
    public const float PeakSlipRatio = 0.10f;
    public const float PeakSlipAngle = 0.14f;  // rad, about 8 degrees
    // Below this speed slip is measured against it, so it doesn't explode.
    const float MinSlipSpeed = 2f;

    // Raw gear byte -> gear (0 neutral, -1 reverse). Raw 0 is reverse (its
    // ratio is negative), 1.. are the forward gears; the count includes
    // reverse.
    public static int MapGear(byte raw, int gearCount) => raw switch
    {
        0 => -1,
        _ when raw >= gearCount && gearCount > 0 => 0,
        _ => raw,
    };

    public static bool IsPlayer(ReadOnlySpan<byte> b) => b[Abs(Icd.PlayerFlag)] == 1;

    public static bool IsAi(ReadOnlySpan<byte> b) => I(b, Icd.AiControlled) != 0;

    static float F(ReadOnlySpan<byte> b, int icd) => MemoryMarshal.Read<float>(b[Abs(icd)..]);
    static int I(ReadOnlySpan<byte> b, int icd) => MemoryMarshal.Read<int>(b[Abs(icd)..]);
    static float W(ReadOnlySpan<byte> b, int wheel, int field) => F(b, Wheel.At(wheel, field));

    public static void Fill(ReadOnlySpan<byte> b, CarSample s)
    {
        s.Driving = true;
        s.Rpm = MathF.Max(0, F(b, Icd.EngineRadS)) * RadSToRpm;
        s.RedlineRpm = F(b, Icd.EngineMaxRadS) * RadSToRpm;
        s.IdleRpm = F(b, Icd.EngineIdleRadS) * RadSToRpm;
        s.Torque = F(b, Icd.Torque);
        s.Power = F(b, Icd.Power);
        s.Clutch = F(b, Icd.Clutch);
        s.Throttle = F(b, Icd.Inputs + 0x0);
        s.Brake = F(b, Icd.Inputs + 0x4);
        s.Handbrake = F(b, Icd.Inputs + 0x10);
        float steerMax = F(b, Icd.SteerMax);
        s.Steer = steerMax != 0 ? F(b, Icd.SteerAngle) / steerMax : 0;  // -1 left, 1 right
        s.Gear = MapGear(b[Abs(Icd.Gear)], I(b, Icd.GearCount));
        s.SpeedMps = F(b, Icd.Speed);
        for (int i = 0; i < 3; i++) s.Position[i] = F(b, Icd.Position + 4 * i);

        float spinSum = 0;
        for (int w = 0; w < 4; w++)
        {
            float spin = W(b, w, Wheel.SpinRadS);
            spinSum += spin;
            s.WheelSpeedRadS[w] = spin;

            // Suspension: metres from rest (positive compressed), and 0 at
            // full extension to 1 at the bump stop.
            float pos = W(b, w, Wheel.SuspensionPos);
            float droop = W(b, w, Wheel.SuspensionDroop), bump = W(b, w, Wheel.SuspensionBump);
            s.SuspensionM[w] = pos - W(b, w, Wheel.SuspensionRest);
            s.SuspensionNormalized[w] = bump != droop ? Math.Clamp((pos - droop) / (bump - droop), 0f, 1f) : 0f;

            // Slip ratio from the tyre's surface speed against the contact
            // patch's speed; slip angle is the game's own.
            float vx = W(b, w, Wheel.ContactVLong), r = W(b, w, Wheel.TireRadius);
            s.WheelSlip[w] = (spin * r - vx) / MathF.Max(MathF.Abs(vx), MinSlipSpeed) / PeakSlipRatio;
            s.WheelSlipAngle[w] = W(b, w, Wheel.SlipAngle) / PeakSlipAngle;
        }
        // Forward velocity is negative when reversing (wheels spin backwards).
        s.VelocityForwardMps = spinSum < 0 ? -s.SpeedMps : s.SpeedMps;
    }
}
