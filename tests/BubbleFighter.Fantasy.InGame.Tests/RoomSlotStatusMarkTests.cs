using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins for the room-slot status-mark gates. The alert chrome on <c>pnAlertWeapon</c> /
/// <c>pnAlertCharacter</c> is unreachable on this build, because <c>sub_10E3420(45)</c> tests a feature key
/// that <c>sub_10F2E20</c> never inserts. What is left drawn per present seat is the level badge and the ping
/// glyphs, both of which the client computes locally, so no packet can clear them. These tests pin the values
/// that keep the *state* widgets - the ones a server packet could plausibly switch on - suppressed, so a future
/// change that enables one of them fails here instead of putting a spurious mark beside the character in a live
/// room.
/// </summary>
public sealed class RoomSlotStatusMarkTests
{
    /// <summary>The all-zero session setting: every scalar zero, every wstring empty.</summary>
    private static SessionSettingBlob EmptySetting() => new(
        0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
        SessionSettingBlob.MainMenuKind, MapMetaBlob.Empty(),
        0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0);

    /// <summary>
    /// Reads one <c>RoomPlayerInfo</c> body back field by field in the reader's
    /// own order, so the assertions below name wire positions rather than
    /// record property names.
    ///
    /// </summary>
    /// <param name="identity">The seat identity to write.</param>
    /// <returns>The opened reader positioned at the record's first field.</returns>
    private static PacketReader ReadSeatRecord(RoomPlayerIdentity identity)
    {
        var body = WireLengthSupport.Written(
            w => RoomSessionBlob.WriteRoomPlayerInfo(w, identity));
        return new PacketReader(body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DefaultColorDoesNotChangeTeamOrPlayerIdentity(byte team)
    {
        var record = RoomPlayerInfo.ForSeat(17, "paint-test", 0, 1, team);
        Assert.Equal((ushort)0, record.Unknown40);
        Assert.Equal(team, record.Unknown4);
        Assert.Equal(17, record.UserNum);
    }

    [Fact]
    public void SeatRecordKeepsEveryStateWidgetGateAtItsSuppressingValue()
    {
        var r = ReadSeatRecord(new RoomPlayerIdentity(9, "nick", 1));

        // The three gates that switch a per-seat state widget on are seat+140 (rank crown, sub_980730),
        // seat+108 (emblem / clan, sub_980730) and seat+188 (ready / loaded, sub_980730). Each must
        // stay at the value that leaves its widget hidden.
        Assert.Equal(0x0102, r.ReadInt32());          // unknown0 (occupancy dword)
        Assert.Equal(0, r.ReadInt32());               // unknown1 (seat enable)
        Assert.Equal(0, r.ReadInt32());               // unknown2
        Assert.Equal((byte)1, r.ReadByte());          // unknown3 (seat)
        Assert.Equal((byte)0, r.ReadByte());          // unknown4 (team label key, waiting value 0)
        Assert.Equal(9, r.ReadInt32());               // userNum
        Assert.Equal("nick", r.ReadWideStringPrefixed());
        Assert.Equal((ushort)1, r.ReadUInt16());      // unknown36: reference value, reaches sub_90BD50
        Assert.Equal((ushort)0, r.ReadUInt16()); // unknown40: native default color, independent of team
        Assert.Equal(0, r.ReadInt32());               // unknown96
        Assert.Equal(0, r.ReadInt32());               // unknown97
        Assert.Equal(0, r.ReadInt32());               // unknown98
        Assert.Equal(0, r.ReadInt32());               // unknown44
        Assert.Equal(0, r.ReadInt32());               // unknown48
        Assert.Equal(0u, r.ReadUInt32());             // unknown52 count
        Assert.Equal(0, r.ReadInt32());               // unknown64
        Assert.Equal(0, r.ReadInt32());               // unknown68
        Assert.Equal(-1, r.ReadInt32());              // unknown88 (no peer target)
        Assert.Equal(0, r.ReadInt32());               // unknown94
        Assert.Equal(0, r.ReadInt32());               // unknown100
        Assert.Equal(0, r.ReadInt32());               // unknown104 (icon table pick)
        Assert.Equal(-1, r.ReadInt32());              // unknown108 (icon index; -1 hides)
        Assert.Equal(0, r.ReadInt32());               // unknown112
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());   // unknown116
        Assert.Equal(0, r.ReadInt32());               // unknown140 (rank gate; 0 hides)
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());   // unknown144
        Assert.Equal(0, r.ReadInt32());               // unknown168
        Assert.Equal(0, r.ReadInt32());               // unknown169
        Assert.Equal(1, r.ReadInt32());               // unknown172 (level badge)
        Assert.Equal(0, r.ReadInt32());               // unknown176
        Assert.Equal(1, r.ReadInt32());               // unknown180 (constructor default)
        Assert.Equal((byte)0, r.ReadByte());          // unknown184
        Assert.Equal(0, r.ReadInt32());               // unknown185
        Assert.Equal(0, r.ReadInt32());               // unknown188 (ready gate; 0 hides)
        Assert.Equal(0, r.ReadInt32());               // unknown192
        Assert.Equal(0, r.ReadInt32());               // unknown196
        Assert.Equal(0, r.ReadInt32());               // unknown200
        Assert.Equal((byte)0, r.ReadByte());          // opt204tag (absent)
        Assert.Equal(0, r.ReadInt32());               // unknown232
        Assert.Equal(new byte[48], r.ReadBytes(48).ToArray());   // SocialSystemInfo
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void SeatRecordLeavesTheEmblemIconHiddenForEverySeatIndex()
    {
        // sub_980730 gates the emblem widget on seat present AND
        // seat+108 >= 0. unknown108 is the only field the emulator writes as
        // -1, and it must stay -1 for every seat: a non-negative value would
        // index the icon table at dword_18E566C+224 through sub_9315F0 and put
        // an emblem beside the character.
        for (byte slot = 0; slot < 8; slot++)
        {
            var record = RoomPlayerInfo.ForSeat(9, "nick", slot);
            Assert.Equal(-1, record.Unknown108);
            Assert.Equal(0, record.Unknown104);
        }
    }

    [Fact]
    public void SeatRecordLeavesTheRankCrownHiddenForEverySeatIndex()
    {
        // sub_980730 gates the rank crown on seat+140 != 0. The rank
        // icon index itself comes from dword_18E56A4+4*seat+12112, which
        // sub_9DFAD0 resets to -1, so with unknown140 at zero the crown stays
        // hidden no matter what the per-seat array holds.
        for (byte slot = 0; slot < 8; slot++)
        {
            var record = RoomPlayerInfo.ForSeat(9, "nick", slot);
            Assert.Equal(0, record.Unknown140);
        }
    }

    [Fact]
    public void SeatRecordKeepsTheReadyMarkerHiddenForEverySeatIndex()
    {
        // sub_980730 shows the ready widget only when seat+188 == 1.
        // A waiting room has no ready state, so it must stay zero.
        for (byte slot = 0; slot < 8; slot++)
        {
            var record = RoomPlayerInfo.ForSeat(9, "nick", slot);
            Assert.Equal(0, record.Unknown188);
        }
    }

    [Fact]
    public void SeatRecordKeepsTheLevelBadgeAtLeastOneSoItRenders()
    {
        // sub_9250D0 clamps the symbol index to count-1 and sub_EE9550 builds
        // the icon from level-1, so a zero level renders blank. Flooring at one
        // is what makes the badge show the first symbol.
        Assert.Equal(1, RoomPlayerInfo.ForSeat(9, "nick", 0).Unknown172);
        Assert.Equal(1, RoomPlayerInfo.ForSeat(9, "nick", 0, level: 0).Unknown172);
        Assert.Equal(1, RoomPlayerInfo.ForSeat(9, "nick", 0, level: -5).Unknown172);
        Assert.Equal(7, RoomPlayerInfo.ForSeat(9, "nick", 0, level: 7).Unknown172);
    }

    [Fact]
    public void OccupancyDwordKeepsBothLowBytesNonZeroAcrossTheSeatRange()
    {
        // sub_9EE100 clears the room occupancy flag when both bytes at
        // session+592+320*slot and +593 are zero, and those two bytes are the
        // low half of unknown0. Both must be nonzero for every seat the room
        // can carry, or the seat vanishes from the member list.
        for (byte slot = 0; slot < 36; slot++)
        {
            var body = WireLengthSupport.Written(
                w => RoomSessionBlob.WriteRoomPlayerInfo(w, new RoomPlayerIdentity(9, "n", slot)));
            Assert.NotEqual(0, body[0]);
            Assert.NotEqual(0, body[1]);
        }
    }

    [Fact]
    public void GotoRoomSeatListAgreesWithThePerSeatRecordWriter()
    {
        // The S2C 117 seat list and the per-seat writer must never drift, or
        // the two paths would describe the same seat with different gate
        // values. This pins the whole seat list against the single-record form.
        var identities = new[]
        {
            new RoomPlayerIdentity(9, "a", 0),
            new RoomPlayerIdentity(10, "b", 1),
        };
        var body = RoomSessionBlob.GotoRoom(EmptySetting(), identities);

        var r = new PacketReader(body);
        _ = SessionSettingBlob.Read(ref r);
        Assert.Equal(0, r.ReadInt32());          // unknown544
        Assert.Equal(0, r.ReadInt32());          // unknown548
        Assert.Equal(2u, r.ReadUInt32());        // player count

        foreach (var identity in identities)
        {
            var expected = WireLengthSupport.Written(
                w => RoomSessionBlob.WriteRoomPlayerInfo(w, identity));
            var actual = r.ReadBytes(expected.Length).ToArray();
            Assert.Equal(expected, actual);
        }

        Assert.Equal(0u, r.ReadUInt32());        // map564 count
        Assert.Equal(0u, r.ReadUInt32());        // map588 count
    }

    [Fact]
    public void SeatRecordPinsTheFeatureGateThatMakesTheAlertChromeUnreachable()
    {
        // kr-1256 sub_10E3420(45) tests client feature key 45, which sub_10F2E20 never inserts, so the alert
        // block in sub_97FEA0 is skipped and the ping glyphs are drawn instead. This test documents that
        // finding as an executable pin on the one value the emulator controls that could otherwise look like a
        // fix: nothing in the seat record can re-arm that block, so no seat field may be repurposed as a "clear
        // the mark" lever.
        var record = RoomPlayerInfo.ForSeat(9, "nick", 0);

        // The alert panels live at slot+1720 / slot+1724 and are reached only
        // through sub_981810, whose outer test is the same key-45 lookup. The
        // emulator writes nothing into either panel, and the seat record has no
        // field that feeds them.
        Assert.Equal(-1, record.Unknown108);
        Assert.Equal(0, record.Unknown140);
        Assert.Equal(0, record.Unknown188);

        // These two compatibility fields are pinned at their current values so that a change requires explicit
        // review.
        Assert.Equal(-1, record.Unknown88);
        Assert.Equal(1, record.Unknown180);
    }
}
