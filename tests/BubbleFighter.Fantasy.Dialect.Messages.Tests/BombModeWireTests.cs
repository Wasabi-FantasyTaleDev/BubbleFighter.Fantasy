using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the bomb-mission frames of packets/mode-control.yaml: the twenty-four-byte broadcast, the
/// twenty-byte and twelve-byte requests, and the forty-one-byte dying-mob drop frame.
/// </summary>
public sealed class BombModeWireTests
{
    /// <summary>The broadcast carries operator, state, the three position floats and the timer alignment.</summary>
    [Fact]
    public void GspBombStatePinsTwentyFourBytes()
    {
        var body = BombModeBinding.WriteState(TestBuilds.Packets, "kr-812",
            new RoundBombStateChanged(RoundBombState.Countdown, 0x01020304, 1.0f, -2.5f, 100.0f));
        Assert.Equal(24, body.Length);
        Assert.Equal(0x01020304, BinaryPrimitives.ReadInt32LittleEndian(body));
        Assert.Equal((int)RoundBombState.Countdown, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(1.0f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8))));
        Assert.Equal(-2.5f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12))));
        Assert.Equal(100.0f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16))));
        // The plain countdown: the alignment word the server keeps clear (kr-812 sub_4245B0 case 2).
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));

        var aligned = BombModeBinding.WriteState(TestBuilds.Packets, "kr-812",
            new RoundBombStateChanged(RoundBombState.Exploded, 7, 0f, 0f, 0f) { TimerAligned = true });
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(aligned.AsSpan(20)));
    }

    /// <summary>The request round-trips its five words and refuses lengths and states the client never sends.</summary>
    [Fact]
    public void GcpBombStateRoundTripsFiveWords()
    {
        var body = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0x0A0B0C0D);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), (int)RoundBombState.Planting);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), BitConverter.SingleToInt32Bits(3.5f));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), BitConverter.SingleToInt32Bits(-1.25f));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), BitConverter.SingleToInt32Bits(42.0f));
        var report = BombModeBinding.ReadState(TestBuilds.Packets, "kr-812", body);
        Assert.Equal(RoundBombState.Planting, report.State);
        Assert.Equal(0x0A0B0C0D, report.Operator);
        Assert.Equal(3.5f, report.PositionX);
        Assert.Equal(-1.25f, report.PositionY);
        Assert.Equal(42.0f, report.PositionZ);

        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), (int)RoundBombState.Defused);
        Assert.Equal(RoundBombState.Defused, BombModeBinding.ReadState(TestBuilds.Packets, "kr-812", body).State);
        foreach (var state in new[] { -1, 7, 100 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), state);
            Assert.Throws<PacketFormatException>(() => BombModeBinding.ReadState(TestBuilds.Packets, "kr-812", body));
        }
        foreach (var malformed in new[] { new byte[3], new byte[19], new byte[21] })
            Assert.Throws<PacketFormatException>(() => BombModeBinding.ReadState(TestBuilds.Packets, "kr-812", malformed));
    }

    /// <summary>The mission-over request round-trips its three words.</summary>
    [Fact]
    public void GcpBombMissionOverRoundTripsThreeWords()
    {
        var body = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(body, 2);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 5);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 9);
        var report = BombModeBinding.ReadMissionOver(TestBuilds.Packets, "kr-812", body);
        Assert.Equal(2, report.WinningTeam);
        Assert.Equal(5, report.Planter);
        Assert.Equal(9, report.Defuser);
        Assert.Equal(new RoundBombMissionOverReported(1, -1, -1),
            BombModeBinding.ReadMissionOver(TestBuilds.Packets, "kr-812", Convert.FromHexString("01000000FFFFFFFFFFFFFFFF")));
        foreach (var malformed in new[] { new byte[8], new byte[13] })
            Assert.Throws<PacketFormatException>(() => BombModeBinding.ReadMissionOver(TestBuilds.Packets, "kr-812", malformed));
    }

    /// <summary>The dying-mob drop frame is eleven fields with one single-byte field in the middle.</summary>
    [Fact]
    public void GppMobDropBombOnDeadPinsFortyOneBytes()
    {
        // kr-812 sub_6DB120 writes two u32, one u8, then eight u32; the byte shifts serial to wire offset 33.
        var body = new byte[41];
        BinaryPrimitives.WriteInt32LittleEndian(body, 1_234_567);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 77);
        body[8] = 3;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(33), 5);
        var values = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "gppMobDropBombOnDead", body);
        Assert.Equal(1_234_567, (int)values["unknown_4"]);
        Assert.Equal(77, (int)values["unknown_8"]);
        Assert.Equal((byte)3, values["unknown_12"]);
        Assert.Equal(5, (int)values["serial"]);
        foreach (var malformed in new[] { new byte[40], new byte[42] })
            Assert.Throws<PacketFormatException>(
                () => PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "gppMobDropBombOnDead", malformed));
    }
}
