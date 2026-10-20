using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class ClientToServerParseTests
{
    [Fact]
    public void RoomStatusRequestPinsTwelveBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteInt32(0);
            w.WriteInt32(0);
        });
        Assert.Equal(12, payload.Length);
        var parsed = RoomStatusRequest.Parse(payload);
        Assert.True(parsed.Ready);
        Assert.False(parsed.InShop);
        Assert.False(parsed.InInventory);
        Assert.Throws<PacketFormatException>(() => RoomStatusRequest.Parse([.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => RoomStatusRequest.Parse(payload[..8]));
    }

    [Fact]
    public void CreateRoomRequestPinsEmptyStringsAndRuleRecord()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteInt32(0);
            for (var i = 0; i < 13; i++)
                w.WriteInt32(0);
            w.WriteByte(0);
            w.WriteInt32(0);
        });
        Assert.Equal(4 + 12 + 4 + 52 + 1 + 4, payload.Length);
        var parsed = CreateRoomRequest.Parse(payload);
        Assert.Equal(0, parsed.Rules.DeathMatchKill);
        Assert.Equal((byte)0, parsed.Rules.Flags52);
        Assert.Throws<PacketFormatException>(() => CreateRoomRequest.Parse(payload[..20]));
    }

    [Fact]
    public void CreateRoomRequestMapsRuleFieldsByVerifiedConfigKeys()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteInt32(0);
            // Wire order equals object order (sub_10D26C0 writes sequentially):
            // 0 EndGameTimeOver, 1/2/3 EndGameNotify, 4 Fever, 5 StarDrop,
            // 6 DeathMatchKill, 7 DeathMatchTime, 8 TimeAttackTime,
            // 9 ConquerTimeLimit, 10 StarModeTimeLimit, 11/12 unnamed, u8 flags.
            w.WriteInt32(180000);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteInt32(20);
            w.WriteInt32(300000);
            w.WriteInt32(420000);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteByte(0x21);
            w.WriteInt32(0);
        });

        var parsed = CreateRoomRequest.Parse(payload);
        Assert.Equal(180000, parsed.Rules.EndGameTimeOver);
        Assert.Equal(20, parsed.Rules.DeathMatchKill);
        Assert.Equal(300000, parsed.Rules.DeathMatchTimeMs);
        Assert.Equal(420000, parsed.Rules.TimeAttackTimeMs);
        Assert.Equal((byte)0x21, parsed.Rules.Flags52);
    }

    [Fact]
    public void JoinRoomRequestPinsOneInt32()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt32(7));
        Assert.Equal(4, payload.Length);
        Assert.Equal(7, JoinRoomRequest.Parse(payload).RoomId);
        Assert.Throws<PacketFormatException>(() => JoinRoomRequest.Parse([1, 2, 3, 4, 5]));
    }

    [Fact]
    public void QuickCreateRoomRequestPinsRecordFirstThenStrings()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            for (var i = 0; i < 13; i++)
                w.WriteInt32(i);
            w.WriteByte(0);
            w.WriteWideStringPrefixed("a");
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteInt32(0);
        });
        Assert.Equal(52 + 1 + 6 + 4 + 4 + 4, payload.Length);
        var parsed = QuickCreateRoomRequest.Parse(payload);
        Assert.Equal(0, parsed.Rules.EndGameTimeOver);
        Assert.Equal(12, parsed.Rules.Unknown48);
        Assert.Equal("a", parsed.Unknown1);
        Assert.Throws<PacketFormatException>(() => QuickCreateRoomRequest.Parse(payload[..60]));
    }

    [Fact]
    public void RoomChatPinsInt32PlusTwoStrings()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteWideStringPrefixed("hi");
            w.WriteWideStringPrefixed(string.Empty);
        });
        Assert.Equal(4 + 8 + 4, payload.Length);
        var parsed = RoomChatRequest.Parse(payload);
        Assert.Equal(1, parsed.Unknown0);
        Assert.Equal("hi", parsed.Unknown1);
        Assert.Throws<PacketFormatException>(() => RoomChatRequest.Parse([.. payload, 0]));
    }

    [Fact]
    public void RoomChatTypedPinsInt32StringInt32Int32()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(101);
            w.WriteWideStringPrefixed("x");
            w.WriteInt32(2);
            w.WriteInt32(3);
        });
        Assert.Equal(4 + 6 + 8, payload.Length);
        var parsed = RoomChatTypedRequest.Parse(payload);
        Assert.Equal(101, parsed.Unknown0);
        Assert.Equal(2, parsed.Unknown2);
        Assert.Throws<PacketFormatException>(() => RoomChatTypedRequest.Parse(payload[..10]));
    }

    [Fact]
    public void RoomStateCommandPinsFourInt32TwoBytesInt32()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteInt32(2);
            w.WriteInt32(3);
            w.WriteInt32(4);
            w.WriteByte(5);
            w.WriteByte(6);
            w.WriteInt32(7);
        });
        Assert.Equal(16 + 2 + 4, payload.Length);
        var parsed = RoomStateCommandRequest.Parse(payload);
        Assert.Equal(4, parsed.Unknown3);
        Assert.Equal((byte)6, parsed.Unknown5);
        Assert.Equal(7, parsed.Unknown6);
        Assert.Throws<PacketFormatException>(() => RoomStateCommandRequest.Parse(payload[..20]));
    }

    [Fact]
    public void GameplayActionPinsFixedFamilyAndRejectsUnknownType()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteByte(1);
            w.WriteInt32(10);
            w.WriteInt32(20);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteUInt16(0);
            w.WriteUInt16(0);
        });
        Assert.Equal(16, payload.Length);
        var parsed = GameplayAction.Parse(payload);
        Assert.Equal(1, parsed.Type);
        Assert.Equal(10, parsed.Unknown1);
        var bad = (byte[])payload.Clone();
        bad[0] = 21;
        Assert.Throws<PacketFormatException>(() => GameplayAction.Parse(bad));
    }

    [Fact]
    public void AlternateActionPinsSixBytes()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteByte(7);
            w.WriteInt32(3);
            w.WriteByte(1);
        });
        Assert.Equal(6, payload.Length);
        var parsed = AlternateAction.Parse(payload);
        Assert.Equal(7, parsed.Unknown0);
        Assert.Equal(3, parsed.Unknown1);
        Assert.Equal(1, parsed.Unknown2);
    }

    [Fact]
    public void RelayEndpointPinsSixteenBytes()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(3000);
            w.WriteInt32(1);
            w.WriteInt32(0);
            w.WriteInt32(0);
        });
        Assert.Equal(16, payload.Length);
        var parsed = RelayEndpointRequest.Parse(payload);
        Assert.Equal(3000, parsed.Unknown0);
    }

    [Fact]
    public void GameResultStatsPinsOneHundredTwentyBytes()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            for (var i = 0; i < GameResultStats.Count; i++)
                w.WriteInt32(i);
        });
        Assert.Equal(120, payload.Length);
        var parsed = GameResultStats.Parse(payload);
        Assert.Equal(29, parsed.Values[29]);
        Assert.Throws<PacketFormatException>(() => GameResultStats.Parse(payload[..116]));
    }

    [Fact]
    public void EmptyPayloadRejectsBytes()
    {
        EmptyPayload.Parse([], "C2S 17");
        Assert.Throws<PacketFormatException>(() => EmptyPayload.Parse([0], "C2S 17"));
    }

    [Fact]
    public void RelayAuthenticatePinsKeyAndIdentity()
    {
        var key = new RelaySessionKey(1, 2, 3, 4, 5, 6);
        var payload = WireLengthSupport.Written(w =>
        {
            key.Write(w);
            w.WriteByte(0);
            w.WriteInt32(0);
            w.WriteInt32(9);
            w.WriteWideStringPrefixed("nick");
        });
        Assert.Equal(20 + 1 + 8 + 4 + 8, payload.Length);
        var parsed = RelayAuthenticate.Parse(payload);
        Assert.Equal(key, parsed.Key);
        Assert.Equal(9, parsed.Unknown22);
        Assert.Equal("nick", parsed.Identity);
    }

    [Fact]
    public void RelayMotionRequiresEightByteIdentity()
    {
        Assert.Throws<PacketFormatException>(() => RelayMotion.Parse([1, 2, 3]));
        var payload = new byte[8];
        var parsed = RelayMotion.Parse(payload);
        Assert.Equal(0, parsed.ObjectIdentity);
        Assert.Empty(parsed.Remainder);
        var withRest = new byte[12];
        var longer = RelayMotion.Parse(withRest);
        Assert.Equal(4, longer.Remainder.Length);
    }
}
