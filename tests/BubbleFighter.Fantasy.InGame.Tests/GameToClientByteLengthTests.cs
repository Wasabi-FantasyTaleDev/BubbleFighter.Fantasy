using System.Net;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class GameToClientByteLengthTests
{
    [Fact]
    public void GotoMenuPinsFourBytes()
    {
        var payload = GameToClient.GotoMenu(0);
        Assert.Equal(4, payload.Length);
        Assert.Equal("00000000", Convert.ToHexString(payload));
    }

    [Fact]
    public void ConnectRelayPinsNetworkIpv4PlusPortPlusKey()
    {
        var key = new RelaySessionKey(1, 2, 3, 4, 5, 6);
        var payload = GameToClient.ConnectRelay(IPAddress.Parse("127.0.0.1"), 7101, key);
        Assert.Equal(4 + 2 + RelaySessionKey.ByteLength, payload.Length);
        Assert.Equal(127, payload[0]);
        Assert.Equal(0, payload[1]);
        Assert.Equal(0, payload[2]);
        Assert.Equal(1, payload[3]);
        Assert.Equal(7101, BitConverter.ToUInt16(payload, 4));
    }

    [Fact]
    public void WaitingDialogPinsTwoDwordsAndAnEmptyWideString()
    {
        // sub_84F6C0 reads two i32 and then a prefixed wstring; the empty form is 12 bytes.
        Assert.Equal("000000000000000000000000", Convert.ToHexString(GameToClient.WaitingDialog(0, 0)));
        Assert.Equal("140000000000000000000000", Convert.ToHexString(GameToClient.WaitingDialog(0x14, 0)));
    }

    [Fact]
    public void PlayerMapLoadedPinsFourBytes()
    {
        Assert.Equal(4, GameToClient.PlayerMapLoaded(0).Length);
    }

    [Fact]
    public void SeatTeamLabelPinsSixBytesAndTheAbsentTailTag()
    {
        // sub_9DAE50 reads an i32 seat, a u8 label and then the optional tail,
        // whose absent form is a single zero tag byte (sub_9DAE50,
        // sub_10C3F00). A missing tail byte would shift nothing after it in
        // this body but would leave the reader waiting for a tag it never gets.
        var payload = GameToClient.SeatTeamLabel(3, 2);
        Assert.Equal(6, payload.Length);
        Assert.Equal("03000000" + "02" + "00", Convert.ToHexString(payload));
    }

    [Fact]
    public void InSessionSpawnPinsEntityAndEventCodeAsEightBytes()
    {
        // The second i32 is the event code the handler switches on directly, not a
        // spare argument: 21 is the unguarded stand-up (sub_49D020 -> sub_49D5B0),
        // 29 the respawn sub_49C870 that bails unless the sub-state read through
        // +0xDBC sits in 500..599, and 0 only an animation (sub_48D3E0,
        // sub_49C870, sub_6D55D0 case at 0x6D8952).
        var payload = GameToClient.InSessionSpawn(1000000, 21);
        Assert.Equal(8, payload.Length);
        Assert.Equal(1000000, BitConverter.ToInt32(payload, 0));
        Assert.Equal(21, BitConverter.ToInt32(payload, 4));
    }

    [Fact]
    public void SceneObjectPinsFourBytes()
    {
        var payload = GameToClient.SceneObject(1100000);
        Assert.Equal(4, payload.Length);
        Assert.Equal(1100000, BitConverter.ToInt32(payload, 0));
    }

    [Fact]
    public void EnterGameStagePinsTheThirtyNineByteMinimum()
    {
        // sub_10D1C10 allocates three list heads (object +96, +104, +112) and
        // sub_10D1D70 fills all three, so the minimum body is 39 bytes.
        Assert.Equal(39, GameToClient.EnterGameStage(EnterGameStageRecord.Default).Length);
    }

    [Fact]
    public void EnterGameStagePinsTheEmptyBodyLayout()
    {
        var payload = GameToClient.EnterGameStage(EnterGameStageRecord.Default);
        // 3 empty wstrings (12) + i32 + i32 + u8 + u8 + i32 + u8 (15) + three
        // empty counted lists (12).
        Assert.Equal(39, payload.Length);
        Assert.Equal(
            "00000000" + "00000000" + "00000000" +
            "00000000" + "00000000" + "00" + "00" + "00000000" + "00" +
            "00000000" + "00000000" + "00000000",
            Convert.ToHexString(payload));
    }

    [Fact]
    public void EnterGameStageCountsEveryListEntryByItsOwnWidth()
    {
        var record = EnterGameStageRecord.Default with
        {
            Unknown0 = "map",
            Unknown9 = [new StartStageEntry(1, 2, 3), new StartStageEntry(4, 5, 6)],
            Unknown10 = [new StartStageTriple(7, 8, 9)],
            Unknown11 = [new StartStageEntry(10, 11, 12)],
        };
        var payload = GameToClient.EnterGameStage(record);

        // 39-byte minimum, plus the 3 wchars of "map" (6 bytes). The two outer
        // lists take u8 + i32 + i32 (9 bytes) per entry; the middle list takes
        // three i32 (12 bytes) because its reader sub_10D30B0 has no leading
        // byte. Each count is an empty u32 on top.
        Assert.Equal(39 + 6 + (3 * 9) + 12, payload.Length);
    }

    [Fact]
    public void SeatInfoPinsSixteenBytesPlusTheMaskPopcount()
    {
        Assert.Equal(16, GameToClient.SeatInfo(0, 0, 0, []).Length);
        Assert.Equal(18, GameToClient.SeatInfo(0, 0, 0b11, [0, 0]).Length);
        Assert.Equal(19, GameToClient.SeatInfo(0, 0, 0b1011, [0, 0, 0]).Length);
        Assert.Equal(16 + 32, GameToClient.SeatInfo(0, 0, 0xFFFFFFFF, new byte[32]).Length);
    }

    [Fact]
    public void SeatInfoRejectsAMaskValueCountMismatch()
    {
        Assert.Throws<ArgumentException>(() => GameToClient.SeatInfo(0, 0, 0b11, [0]));
        Assert.Throws<ArgumentException>(() => GameToClient.SeatInfo(0, 0, 0, [0]));
    }

    [Fact]
    public void SeatInfoRejectsMaskBitsTheReaderNeverConsumes()
    {
        // sub_10D22D0 walks slots 0..35 only; bits above that are silently
        // dropped by the client, so the writer refuses them.
        Assert.Throws<ArgumentOutOfRangeException>(() => GameToClient.SeatInfo(0, 0, 1L << 36, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameToClient.SeatInfo(0, 0, -1, new byte[32]));
    }

    [Fact]
    public void StartFlagsPinsTwelveBytes()
    {
        var payload = GameToClient.StartFlags(1, 0, 1);
        Assert.Equal(12, payload.Length);
        Assert.Equal("010000000000000001000000", Convert.ToHexString(payload));
    }

    [Fact]
    public void StartTimerPinsFourBytes()
    {
        Assert.Equal(4, GameToClient.StartTimer(1000).Length);
    }

    [Fact]
    public void RoundInitializePinsEightBytes()
    {
        Assert.Equal(8, GameToClient.RoundInitialize(0, 0).Length);
    }

    [Fact]
    public void ResultOpenPinsEightBytes()
    {
        Assert.Equal(8, GameToClient.ResultOpen(0, 0).Length);
    }

    [Fact]
    public void ResultBannerPinsTenBytes()
    {
        // Body i32 + i32 + u8 + u8 (sub_9E2FE0): the two integers are
        // unread by the handler, the two bytes land at GameInfo+308/+312. The
        // all-zero form is what the round end emits - the draw/neutral branch
        // of the kr-1256 sub_9E9710 banner selection.
        var payload = GameToClient.ResultBanner(0, 0);
        Assert.Equal(10, payload.Length);
        Assert.Equal("0000000000000000" + "0000", Convert.ToHexString(payload));
        Assert.Equal(GameToClient.BannerTypeDraw, 0);

        // The field order itself: unknown0, unknown1, type, flag.
        Assert.Equal(
            "01000000" + "02000000" + "03" + "04",
            Convert.ToHexString(GameToClient.ResultBanner(1, 2, 3, 4)));
    }

    [Fact]
    public void ResultClosePinsOneByte()
    {
        var payload = GameToClient.ResultClose(1);
        var only = Assert.Single(payload);
        Assert.Equal(1, only);
    }

    [Fact]
    public void RelayStateClearPinsAnEmptyBody()
    {
        // The dispatcher case (9D4790-9D4833 in sub_9D2FF0) never reads the
        // stream, so any body byte would leave the client's cursor uneaten -
        // the kr-1256 dispatcher therefore requires an empty body.
        Assert.Empty(GameToClient.RelayStateClear());
    }

    [Fact]
    public void RoundStageResetPinsAnEmptyBody()
    {
        // sub_9EA690 (case 0x26C) reads nothing either; it drops the phase and
        // repeats the stage move, both from kr-1256 handler state.
        Assert.Empty(GameToClient.RoundStageReset());
    }
}
