using System.Buffers.Binary;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Direct byte-length pins for GameToClient codecs that A1 flagged as missing
/// or only covered indirectly (S2C 4/5/6/7/609/32885). Lengths come from each
/// method's XML returns clause or field table, not from hand-derived totals.
///
/// </summary>
public sealed class GameToClientCodecLengthTests
{
    [Fact]
    public void ArenaZoneMasksPinsTwoHundredSixtyEightBytesAndActorId()
    {
        // XML: "The 268-byte payload".
        var payload = GameToClient.ArenaZoneMasks(0x0010F447);
        Assert.Equal(268, payload.Length);
        Assert.Equal(0x0010F447, BinaryPrimitives.ReadInt32LittleEndian(payload));
    }

    [Fact]
    public void ArenaCellGridPinsOneThousandEightBytesAndActorId()
    {
        // XML: "The 1008-byte payload".
        var payload = GameToClient.ArenaCellGrid(0x0010F447);
        Assert.Equal(1008, payload.Length);
        Assert.Equal(0x0010F447, BinaryPrimitives.ReadInt32LittleEndian(payload));
    }

    [Fact]
    public void ArenaThirdMaskPinsOneHundredThirtySixZeroedDwords()
    {
        // XML: i32 actor + u32 count 32 + 32 x i32 = 136 bytes.
        var payload = GameToClient.ArenaThirdMask(42);
        Assert.Equal(136, payload.Length);
        Assert.Equal(
            "2A000000" + "20000000" + new string('0', 32 * 8),
            Convert.ToHexString(payload));
    }

    [Fact]
    public void ArenaAltitudePinsEightBytes()
    {
        // XML: "The 8-byte payload" (u32 actor + f32 altitude).
        const float altitude = 110.0f;
        var payload = GameToClient.ArenaAltitude(1000000, altitude);
        Assert.Equal(8, payload.Length);
        Assert.Equal(1000000, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(altitude, BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(4)));
    }

    [Fact]
    public void RoundClockUnboundedPinsAnEmptyBody()
    {
        // XML: dispatcher case reads nothing; empty body is proven (sub_9E1EB0).
        Assert.Empty(GameToClient.RoundClockUnbounded());
    }

    [Fact]
    public void MovementStateFramePinsTwentyOneBytes()
    {
        // XML: "The 21-byte payload" (sub_709700: 4+4+4+4+1+1+1+2).
        var payload = GameToClient.MovementStateFrame(
            actorId: 1000000,
            x: 1.0f,
            y: 2.0f,
            z: 3.0f,
            yaw: 4,
            pitch: 5,
            state: 6,
            tick: 7);
        Assert.Equal(21, payload.Length);
        Assert.Equal(1000000, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(4)));
        Assert.Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(8)));
        Assert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(12)));
        Assert.Equal(4, payload[16]);
        Assert.Equal(5, payload[17]);
        Assert.Equal(6, payload[18]);
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(19)));
    }
}
