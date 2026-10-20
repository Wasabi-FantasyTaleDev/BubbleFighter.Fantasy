using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Byte-length pins the original suite left out. The bodies below carry the nested minimum lengths
/// (<c>SessionSetting</c> 225 B, <c>MapMeta</c> 155 B and <c>RoomPlayerInfo</c> 188 B), so a width regression
/// in any nested blob fails here instead of desynchronizing the live client's cursor.
/// </summary>
public sealed class WirePinGapTests
{
    /// <summary>The all-zero session setting: every scalar zero, every wstring empty.</summary>
    private static SessionSettingBlob EmptySetting() => new(
        0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
        SessionSettingBlob.MainMenuKind, MapMetaBlob.Empty(),
        0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0);

    [Fact]
    public void SessionSettingBlobPinsTheTwoHundredTwentyFiveByteAllZeroMinimum()
    {
        var body = WireLengthSupport.Written(EmptySetting().Write);

        // 28 (header) + 155 (MapMeta) + 42 (tail).
        Assert.Equal(225, body.Length);

        // The only non-zero bytes in the all-empty form are the four-byte kind
        // at wire offset 24 (u16 + i32 + wstring count + three u16 + i32 +
        // wstring count = 2 + 4 + 4 + 6 + 4 + 4), which is the room-stage gate.
        var expected = new byte[225];
        BinaryPrimitives.WriteInt32LittleEndian(
            expected.AsSpan(24), SessionSettingBlob.MainMenuKind);
        Assert.Equal(expected, body);

        // The S2C 501 body is exactly this blob and nothing else, so the
        // snapshot minimum is the same 225 bytes with no trailing scalar.
        var snapshot = RoomSessionBlob.RoomInfoSnapshot(EmptySetting());
        Assert.Equal(225, snapshot.Length);
        Assert.Equal(expected, snapshot);

        var r = new PacketReader(snapshot);
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal(0, r.Remaining);
        Assert.Equal(SessionSettingBlob.MainMenuKind, again.Kind);
        Assert.True(again.Map.IsEmpty);
    }

    [Fact]
    public void MapMetaBlobPinsTheOneHundredFiftyFiveByteAllZeroMinimum()
    {
        var body = WireLengthSupport.Written(MapMetaBlob.Empty().Write);

        // The 155-byte minimum breaks down as 10 + 28 + 16 + 4 + 24 + 12 + 4 + 4 + 16 + 20 + 4 + 8 + 1 + 8 + 8
        // + 4.
        Assert.Equal(155, body.Length);
        Assert.Equal(new byte[155], body);

        var r = new PacketReader(body);
        var parsed = MapMetaBlob.Read(ref r);
        Assert.Equal(0, r.Remaining);
        Assert.True(parsed.IsEmpty);
        Assert.Equal(body, WireLengthSupport.Written(parsed.Write));
    }

    [Fact]
    public void MapMetaWriteRejectsAFlagVectorThatIsNotFive()
    {
        // unknown322 is five i32-as-bool values on the wire; any other length
        // would silently shift every later field, so it must fail loud.
        Assert.Throws<InvalidOperationException>(() =>
            WireLengthSupport.Written((MapMetaBlob.Empty() with { Unknown322 = [0, 0, 0, 0] }).Write));
        Assert.Throws<InvalidOperationException>(() =>
            WireLengthSupport.Written((MapMetaBlob.Empty() with { Unknown322 = [0, 0, 0, 0, 0, 0] }).Write));
    }

    [Fact]
    public void GotoRoomPinsTheExactBodyWithNoTrailingBytes()
    {
        var bare = RoomSessionBlob.GotoRoom(EmptySetting(), []);
        Assert.Equal(225 + 4 + 4 + 4 + 4 + 4 + 12 + 1 + 4 + 4, bare.Length);

        // Only the kind dword at offset 24 is non-zero in the empty form.
        var expectedBare = new byte[bare.Length];
        BinaryPrimitives.WriteInt32LittleEndian(
            expectedBare.AsSpan(24), SessionSettingBlob.MainMenuKind);
        Assert.Equal(expectedBare, bare);
        // Slot two requires two empty records before its 188-byte record:
        // 0x9D9250 installs seats by vector position.
        var oneSeat = RoomSessionBlob.GotoRoom(
            EmptySetting(), [new RoomPlayerIdentity(9, string.Empty, 2)]);
        Assert.Equal(bare.Length + 3 * 188, oneSeat.Length);

        var r = new PacketReader(oneSeat);
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal(SessionSettingBlob.MainMenuKind, again.Kind);
        Assert.Equal(0, r.ReadInt32());                      // unknown544
        Assert.Equal(0, r.ReadInt32());                      // unknown548
        Assert.Equal(3u, r.ReadUInt32());                    // slot vector count
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(0, r.ReadInt32());
            r.ReadBytes(184);
        }
        // unknown0 is the occupancy dword: sub_9EE100 scans its first two
        // bytes at the seat-array base, so both must be nonzero.
        Assert.Equal(0x0103, r.ReadInt32());
        // unknown1 lands at seat+1; sub_980730 routes any seat whose seat+1 is nonzero into sub_980540(slot,
        // 1), which hides the slot's children and never writes its name, so it must stay zero.
        Assert.Equal(0, r.ReadInt32());                      // unknown1 (seat enable)
        Assert.Equal(0, r.ReadInt32());                      // unknown2
        Assert.Equal((byte)2, r.ReadByte());                 // unknown3 (seat)
        // unknown4 is the team-label key sub_980730 switches on; the waiting-room fixture
        // value is 0, and the in-round slot + 1 is written by the client's own S2C 510 handler, not by this
        // record.
        Assert.Equal((byte)0, r.ReadByte());                 // unknown4 (team label key, waiting value 0)
        Assert.Equal(9, r.ReadInt32());                      // userNum
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());
        Assert.Equal((ushort)1, r.ReadUInt16());             // unknown36: reference value, reaches sub_90BD50
        Assert.Equal((ushort)0, r.ReadUInt16());             // unknown40: native default color selector
        Assert.Equal(0, r.ReadInt32());                      // unknown96
        Assert.Equal(0, r.ReadInt32());                      // unknown97
        Assert.Equal(0, r.ReadInt32());                      // unknown98
        Assert.Equal(0, r.ReadInt32());                      // unknown44
        Assert.Equal(0, r.ReadInt32());                      // unknown48
        Assert.Equal(0u, r.ReadUInt32());                    // unknown52 count
        Assert.Equal(0, r.ReadInt32());                      // unknown64
        Assert.Equal(0, r.ReadInt32());                      // unknown68
        Assert.Equal(-1, r.ReadInt32());                     // unknown88 (no peer target)
        Assert.Equal(0, r.ReadInt32());                      // unknown94
        Assert.Equal(0, r.ReadInt32());                      // unknown100
        Assert.Equal(0, r.ReadInt32());                      // unknown104
        Assert.Equal(-1, r.ReadInt32());                     // unknown108 (no target)
        Assert.Equal(0, r.ReadInt32());                      // unknown112
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());  // unknown116
        Assert.Equal(0, r.ReadInt32());                      // unknown140
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());  // unknown144
        Assert.Equal(0, r.ReadInt32());                      // unknown168
        Assert.Equal(0, r.ReadInt32());                      // unknown169
        Assert.Equal(1, r.ReadInt32());                      // unknown172 (level, floored at one)
        Assert.Equal(0, r.ReadInt32());                      // unknown176
        Assert.Equal(1, r.ReadInt32());                      // unknown180 (constructor default)
        Assert.Equal((byte)0, r.ReadByte());                 // unknown184
        Assert.Equal(0, r.ReadInt32());                      // unknown185
        Assert.Equal(0, r.ReadInt32());                      // unknown188
        Assert.Equal(0, r.ReadInt32());                      // unknown192
        Assert.Equal(0, r.ReadInt32());                      // unknown196
        Assert.Equal(0, r.ReadInt32());                      // unknown200
        Assert.Equal((byte)0, r.ReadByte());                 // opt204tag (absent)
        Assert.Equal(0, r.ReadInt32());                      // unknown232
        Assert.Equal(new byte[48], r.ReadBytes(48).ToArray());   // SocialSystemInfo
        Assert.Equal(0u, r.ReadUInt32());                    // map564 count
        Assert.Equal(0u, r.ReadUInt32());                    // map588 count
        Assert.Equal(new byte[12], r.ReadBytes(12).ToArray());   // ServerTeamInfo
        Assert.Equal((byte)0, r.ReadByte());                 // opt648
        Assert.Equal(0, r.ReadInt32());                      // unknown728
        Assert.False(r.ReadBool32());                        // unknown732
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void GotoRoomGrowsByTheProfileEntryShapeAndNicknameWidth()
    {
        var bare = RoomSessionBlob.GotoRoom(EmptySetting(), []);
        var entryShape = WireLengthSupport.Written(
            w => new ProfileMapEntry(9, ProfileMapEntry.DaoCharacterItemId).Write(w));

        var withEntry = RoomSessionBlob.GotoRoom(
            EmptySetting(), [], [new ProfileMapEntry(9, ProfileMapEntry.DaoCharacterItemId)]);
        Assert.Equal(bare.Length + entryShape.Length, withEntry.Length);

        var shortName = RoomSessionBlob.GotoRoom(
            EmptySetting(), [new RoomPlayerIdentity(9, "ab", 0)]);
        var longName = RoomSessionBlob.GotoRoom(
            EmptySetting(), [new RoomPlayerIdentity(9, "abcd", 0)]);
        Assert.Equal(bare.Length + 188 + 4, shortName.Length);
        Assert.Equal(bare.Length + 188 + 8, longName.Length);

        // The live create path (C2S 402) echoes the client setting, whose map
        // carries the mode id and a non-empty map name. "training" is eight
        // characters, so its wstring costs 4 + 16 = 20 bytes where the empty
        // form costs 4: +16 over the all-zero setting.
        var liveSetting = RoomWireSupport.WaitingSetting() with { Kind = SessionSettingBlob.MainMenuKind };
        var live = RoomSessionBlob.GotoRoom(liveSetting, [new RoomPlayerIdentity(9, "ab", 0)]);
        Assert.Equal(bare.Length + 188 + 4 + 16, live.Length);
        Assert.Equal(40, liveSetting.Map.Unknown4);
    }

    [Fact]
    public void WriteRoomPlayerInfoPinsTheOneHundredEightyEightByteMinimum()
    {
        var identity = new RoomPlayerIdentity(9, "nick", 1);
        var body = WireLengthSupport.Written(w => RoomSessionBlob.WriteRoomPlayerInfo(w, identity));

        // 188 fixed bytes plus two per nickname character.
        Assert.Equal(188 + 2 * "nick".Length, body.Length);

        // The first dword's low two bytes are the occupancy bytes sub_9EE100
        // scans at the seat-array base (session+592+320*slot); both must be
        // nonzero for the seat to appear in the room member list.
        Assert.NotEqual(0, body[0]);
        Assert.NotEqual(0, body[1]);

        // The identity-only writer and the full record writer must agree byte
        // for byte, or the S2C 117 seat list and the S2C 503 create reply would
        // describe the same seat with different widths.
        var viaRecord = WireLengthSupport.Written(
            w => RoomPlayerInfo.ForSeat(9, "nick", 1).Write(w));
        Assert.Equal(viaRecord, body);
    }

    [Fact]
    public void WriteEmptyServerTeamInfoPinsTwelveZeroBytes()
    {
        var body = WireLengthSupport.Written(RoomSessionBlob.WriteEmptyServerTeamInfo);

        // Three zero counts: the byte-keyed map plus the two trailing vectors.
        Assert.Equal(12, body.Length);
        Assert.Equal(new byte[12], body);
        Assert.Equal(
            WireLengthSupport.Written(ServerTeamInfoBlob.Empty().Write),
            body);
    }

    [Fact]
    public void RelaySessionKeyFromBytesPinsTwentyBytes()
    {
        var bytes = new byte[RelaySessionKey.ByteLength];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i + 1);

        var key = RelaySessionKey.FromBytes(bytes);
        Assert.Equal(0x04030201, key.Unknown0);
        Assert.Equal(0x08070605, key.Unknown1);
        Assert.Equal(0x0C0B0A09, key.Unknown2);
        Assert.Equal((ushort)0x0E0D, key.Unknown3);
        Assert.Equal((ushort)0x100F, key.Unknown4);
        Assert.Equal(0x14131211, key.Unknown5);

        Assert.Equal(bytes, WireLengthSupport.Written(w => key.Write(w)));
        Assert.Throws<ArgumentException>(() => RelaySessionKey.FromBytes(bytes[..^1]));
        Assert.Throws<ArgumentException>(() => RelaySessionKey.FromBytes([.. bytes, 0]));
    }

    [Fact]
    public void CharacterPositionRequestPinsTheTwentyTwoByteBodyAndTwentySixByteReply()
    {
        var request = new CharacterPositionRequest(7, 1, 2, 3, 4, 5, 6);

        var body = WireLengthSupport.Written(request.Write);
        Assert.Equal(CharacterPositionRequest.BodyBytes, body.Length);
        Assert.Equal(22, body.Length);

        // The reply is the sender identity plus the C2S body verbatim, so it is
        // exactly four bytes longer and the tail must round-trip unchanged.
        var reply = WireLengthSupport.Written(w => request.WriteReply(w, 0x11223344));
        Assert.Equal(CharacterPositionRequest.ReplyBytes, reply.Length);
        Assert.Equal(26, reply.Length);
        Assert.Equal("44332211" + Convert.ToHexString(body), Convert.ToHexString(reply));

        var r = new PacketReader(reply);
        Assert.Equal(0x11223344, r.ReadInt32());
        var echoed = CharacterPositionRequest.Parse(reply[4..]);
        Assert.Equal(request, echoed);
    }
}
