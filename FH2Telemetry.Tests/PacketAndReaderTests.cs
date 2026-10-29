using System.Buffers.Binary;
using FH2Telemetry;
using Xunit;
using static FH2Telemetry.GameLayout;

public class ForzaPacketTests
{
    static float F(byte[] p, int o) => BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(o));

    [Fact]
    public void NotDrivingSendsOnlyTimestamp()
    {
        var p = ForzaPacket.EncodeDash(new CarSample { Rpm = 5000 }, 1234);
        Assert.Equal(ForzaPacket.DashSize, p.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(p));
        Assert.Equal(1234u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)));
        Assert.All(p.Skip(8), b => Assert.Equal(0, b));
    }

    [Fact]
    public void DashFieldsLandAtFm7Offsets()
    {
        var s = new CarSample
        {
            Driving = true, Rpm = 4000, IdleRpm = 800, RedlineRpm = 7000, SpeedMps = 25,
            Gear = 3, Throttle = 1, Brake = 0.5f, Steer = -1, Boost = 1.5f, Power = 1e5f, Torque = 300,
        };
        s.WheelSlip[2] = 0.3f;
        s.WheelSlipAngle[2] = 0.4f;
        s.SuspensionNormalized[1] = 0.6f;
        s.Position[2] = 12;
        var p = ForzaPacket.EncodeDash(s, 0);

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(p));
        Assert.Equal(7000, F(p, 8));
        Assert.Equal(800, F(p, 12));
        Assert.Equal(4000, F(p, 16));
        Assert.Equal(0.6f, F(p, 68 + 4));
        Assert.Equal(0.3f, F(p, 84 + 8));
        Assert.Equal(0.5f, F(p, 180 + 8), 5);
        Assert.Equal(12, F(p, 240));
        Assert.Equal(25, F(p, 244));
        Assert.Equal(1e5f, F(p, 248));
        Assert.Equal(300, F(p, 252));
        Assert.Equal(1.5f, F(p, 272));
        Assert.Equal(255, p[303]);
        Assert.Equal(128, p[304]);
        Assert.Equal(3, p[307]);
        Assert.Equal(-127, (sbyte)p[308]);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 11)]
    [InlineData(1, 1)]
    [InlineData(12, 10)]
    public void GearUsesFm7Numbering(int gear, byte expected)
    {
        var p = ForzaPacket.EncodeDash(new CarSample { Driving = true, Gear = gear }, 0);
        Assert.Equal(expected, p[307]);
    }

    [Fact]
    public void HorizonInsertsTwelveBytesAt232()
    {
        var s = new CarSample { Driving = true, Rpm = 3000, SpeedMps = 10, Gear = 2 };
        var d = ForzaPacket.EncodeDash(s, 99);
        var h = ForzaPacket.EncodeHorizon(s, 99);
        Assert.Equal(ForzaPacket.HorizonSize, h.Length);
        Assert.Equal(d.Take(232), h.Take(232));
        Assert.All(h.Skip(232).Take(12), b => Assert.Equal(0, b));
        Assert.Equal(d.Skip(232), h.Skip(244).Take(ForzaPacket.DashSize - 232));
        Assert.Equal(0, h[323]);  // trailing pad byte
        Assert.Equal(10, F(h, 256));  // Speed
        Assert.Equal(2, h[319]);       // Gear
    }

    [Fact]
    public void NonFiniteValuesBecomeZero()
    {
        var p = ForzaPacket.EncodeDash(new CarSample { Driving = true, Rpm = float.NaN, Throttle = float.PositiveInfinity }, 0);
        Assert.Equal(0, F(p, 16));
        Assert.Equal(0, p[303]);
    }
}

public class CarReaderTests
{
    static byte[] Block()
    {
        var b = new byte[BlockSize];
        BinaryPrimitives.WriteUInt64LittleEndian(b, VtCarDynamics);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(ICarDynamicsOffset), VtICarDynamics);
        return b;
    }

    static void Put(byte[] b, int icd, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(Abs(icd)), v);

    [Fact]
    public void RecognisesBothVtablePairs()
    {
        Assert.True(CarScanner.IsCar(Block()));
        var rc = new byte[BlockSize];
        BinaryPrimitives.WriteUInt64LittleEndian(rc, VtCarDynamicsRc);
        BinaryPrimitives.WriteUInt64LittleEndian(rc.AsSpan(ICarDynamicsOffset), VtICarDynamicsRc);
        Assert.True(CarScanner.IsCar(rc));
        var mixed = Block();
        BinaryPrimitives.WriteUInt64LittleEndian(mixed.AsSpan(ICarDynamicsOffset), VtICarDynamicsRc);
        Assert.False(CarScanner.IsCar(mixed));
    }

    [Fact]
    public void ReadsEngineAndInputs()
    {
        var b = Block();
        Put(b, Icd.EngineRadS, 400);
        Put(b, Icd.EngineMaxRadS, 700);
        Put(b, Icd.EngineIdleRadS, 90);
        Put(b, Icd.Inputs, 0.75f);
        Put(b, Icd.Inputs + 4, 0.25f);
        Put(b, Icd.Inputs + 0x10, 1);
        Put(b, Icd.Clutch, 0.5f);
        Put(b, Icd.SteerAngle, -0.2f);
        Put(b, Icd.SteerMax, 0.4f);
        b[Abs(Icd.Gear)] = 3;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Abs(Icd.GearCount)), 6);

        var s = new CarSample();
        CarReader.Fill(b, s);
        Assert.True(s.Driving);
        Assert.Equal(400 * RadSToRpm, s.Rpm, 2);
        Assert.Equal(700 * RadSToRpm, s.RedlineRpm, 2);
        Assert.Equal(90 * RadSToRpm, s.IdleRpm, 2);
        Assert.Equal(0.75f, s.Throttle);
        Assert.Equal(0.25f, s.Brake);
        Assert.Equal(1f, s.Handbrake);
        Assert.Equal(0.5f, s.Clutch);
        Assert.Equal(-0.5f, s.Steer, 5);
        Assert.Equal(3, s.Gear);
    }

    static void PutWheel(byte[] b, int wheel, int field, float v) =>
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(Abs(Wheel.At(wheel, field))), v);

    [Fact]
    public void SpeedIsTheGamesOwnAndReverseIsNegative()
    {
        var b = Block();
        Put(b, Icd.Speed, 22.3f);
        for (int w = 0; w < 4; w++) PutWheel(b, w, Wheel.SpinRadS, -9f);
        var s = new CarSample();
        CarReader.Fill(b, s);
        Assert.Equal(22.3f, s.SpeedMps);
        Assert.Equal(-22.3f, s.VelocityForwardMps);
        Assert.Equal(-9f, s.WheelSpeedRadS[3]);
    }

    [Fact]
    public void SuspensionFromWheelPositionAndLimits()
    {
        // Front-left values seen in the probe drive.
        var b = Block();
        PutWheel(b, 0, Wheel.SuspensionDroop, -0.181f);
        PutWheel(b, 0, Wheel.SuspensionBump, -0.0744f);
        PutWheel(b, 0, Wheel.SuspensionRest, -0.1355f);
        PutWheel(b, 0, Wheel.SuspensionPos, -0.1036f);
        var s = new CarSample();
        CarReader.Fill(b, s);
        Assert.Equal(0.0319f, s.SuspensionM[0], 4);
        Assert.Equal((0.181f - 0.1036f) / (0.181f - 0.0744f), s.SuspensionNormalized[0], 4);
        Assert.Equal(0f, s.SuspensionNormalized[1]);  // no limits: zero, not NaN
    }

    [Fact]
    public void SlipFromContactPatchVelocity()
    {
        var b = Block();
        // Rear-left spinning up: 49 rad/s x 0.318 m = 15.6 m/s against 4 m/s.
        PutWheel(b, 2, Wheel.SpinRadS, 49f);
        PutWheel(b, 2, Wheel.TireRadius, 0.318f);
        PutWheel(b, 2, Wheel.ContactVLong, 4f);
        PutWheel(b, 2, Wheel.SlipAngle, 0.42f);
        // Front-left rolling freely at 20 m/s.
        PutWheel(b, 0, Wheel.SpinRadS, 20f / 0.32f);
        PutWheel(b, 0, Wheel.TireRadius, 0.32f);
        PutWheel(b, 0, Wheel.ContactVLong, 20f);
        var s = new CarSample();
        CarReader.Fill(b, s);
        Assert.Equal((49f * 0.318f - 4f) / 4f / CarReader.PeakSlipRatio, s.WheelSlip[2], 3);
        Assert.Equal(0.42f / CarReader.PeakSlipAngle, s.WheelSlipAngle[2], 3);
        Assert.Equal(0f, s.WheelSlip[0], 3);
        Assert.Equal(0f, s.WheelSlipAngle[0], 3);
    }

    [Fact]
    public void PlayerAndAiFlags()
    {
        var b = Block();
        Assert.False(CarReader.IsPlayer(b));
        b[Abs(Icd.PlayerFlag)] = 1;
        Assert.True(CarReader.IsPlayer(b));
        Assert.False(CarReader.IsAi(b));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Abs(Icd.AiControlled)), 1);
        Assert.True(CarReader.IsAi(b));
    }

    [Theory]
    [InlineData(0, 7, -1)]
    [InlineData(1, 7, 1)]
    [InlineData(6, 7, 6)]
    [InlineData(7, 7, 0)]
    public void GearNumbering(byte raw, int count, int expected) =>
        Assert.Equal(expected, CarReader.MapGear(raw, count));
}
