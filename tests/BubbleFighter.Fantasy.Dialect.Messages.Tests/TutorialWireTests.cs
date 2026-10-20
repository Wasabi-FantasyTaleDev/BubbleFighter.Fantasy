using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Byte-length pins for the legacy tutorial select list and start packets.</summary>
public sealed class TutorialWireTests
{
    [Theory]
    [InlineData("kr-812", 50)]
    [InlineData("cn-717", 50)]
    [InlineData("id-400", 50)]
    [InlineData("id-1809", 50)]
    [InlineData("cn-706", 50)]
    [InlineData("cn-324", 50)]
    [InlineData("cn-279", 0)]
    public void TutorialMoveIntoPreservesNativeDefaultsAcrossLegacyBuilds(string build, int channel)
    {
        var body = RoomBinding.WriteMoveInto(TestBuilds.Packets, build);
        Assert.Equal(12, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body));
        Assert.Equal(channel, BitConverter.ToInt32(body, 4));
        Assert.Equal(0, BitConverter.ToInt32(body, 8));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void NativeEndStagePinsOneCompletionWord(int value, bool completed)
    {
        var body = new byte[4];
        BitConverter.TryWriteBytes(body, value);
        Assert.Equal(completed, TutorialWire.ReadEndStage(TestBuilds.Packets, "kr-812", body));
        Assert.Throws<PacketFormatException>(() => TutorialWire.ReadEndStage(TestBuilds.Packets, "kr-812", body[..3]));
        Assert.Throws<PacketFormatException>(() => TutorialWire.ReadEndStage(TestBuilds.Packets, "kr-812", new byte[8]));
        BitConverter.TryWriteBytes(body, 2);
        Assert.Throws<PacketFormatException>(() => TutorialWire.ReadEndStage(TestBuilds.Packets, "kr-812", body));
    }
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void EmptySingleStageInfoIsFourBytes(string build)
    {
        var body = TutorialWire.WriteSingleStageInfo(TestBuilds.Packets, build, []);
        Assert.Equal(4, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void SingleStageInfoPinsTwelveBytesPerClearedRow(string build)
    {
        var body = TutorialWire.WriteSingleStageInfo(TestBuilds.Packets, build,
        [
            new SingleStageClearInfo(1, 2, 3),
            new SingleStageClearInfo(11, 0, 0),
        ]);
        Assert.Equal(4 + 12 + 12, body.Length);
        Assert.Equal(2, BitConverter.ToInt32(body, 0));
        Assert.Equal(1, BitConverter.ToInt32(body, 4));
        Assert.Equal(2, BitConverter.ToInt32(body, 8));
        Assert.Equal(3, BitConverter.ToInt32(body, 12));
        Assert.Equal(11, BitConverter.ToInt32(body, 16));
    }

    [Fact]
    public void UnreviewedEarlyStageListRejectsNonemptyRows()
    {
        Assert.Throws<InvalidOperationException>(() => TutorialWire.WriteSingleStageInfo(
            TestBuilds.Packets, "cn-279", [new SingleStageClearInfo(1, 2, 3)]));
    }

    [Fact]
    public void StageListPreservesSignedBitsAndBoundsItsBody()
    {
        var body = TutorialWire.WriteSingleStageInfo(TestBuilds.Packets, "kr-812",
            [new SingleStageClearInfo(-1, int.MinValue, int.MaxValue)]);
        Assert.Equal(new[] { 1, -1, int.MinValue, int.MaxValue },
            Enumerable.Range(0, 4).Select(i => BitConverter.ToInt32(body, i * 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TutorialWire.WriteSingleStageInfo(
            TestBuilds.Packets, "kr-812", Enumerable.Repeat(new SingleStageClearInfo(), 65536).ToArray()));
    }

    [Fact]
    public void TutorialSelectRoomUsesKindFiveNotMainMenuKind()
    {
        var room = new MainMenuRoomOpened(11, "test812", 300001, [300001]);
        var menu = RoomCodec.WriteMainMenu(room, TestBuilds.Of("kr-812").Schema);
        var select = RoomCodec.WriteTutorialSelect(room, TestBuilds.Of("kr-812").Schema);
        Assert.Equal(menu.Length, select.Length);
        Assert.Equal(RoomCodec.MainMenuKind, BitConverter.ToInt32(menu, 24));
        Assert.Equal(RoomCodec.TutorialSelectKind, BitConverter.ToInt32(select, 24));
        // GameSetting mode at +28 stays 0 on kind 5 (PlayTutorial mode 103 is kind 2 only).
        Assert.Equal(0, BitConverter.ToInt32(select, 28));
    }

    [Fact]
    public void TutorialSelectIgnoresPlayTutorialModeAndMapKey()
    {
        // WriteRoom kind 5 must strip mode 103 / map strings even if a caller passes them; that
        // leakage is what crashes TutorialRoomStage on arrival after the mode/map WriteRoom change.
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        foreach (var schema in new[] { TestBuilds.Of("cn-717").Schema, TestBuilds.Of("kr-812").Schema })
        {
            var poisoned = RoomCodec.WriteRoom(
                room, schema, RoomCodec.TutorialSelectKind, mode: 103, mapName: "rel_prologue_01",
                mapDirectory: "rel_prologue_01", currentMap: "rel_prologue_01");
            var clean = RoomCodec.WriteTutorialSelect(room, schema);
            Assert.Equal(clean, poisoned);
            Assert.Equal(RoomCodec.TutorialSelectKind, BitConverter.ToInt32(poisoned, 24));
            Assert.Equal(0, BitConverter.ToInt32(poisoned, 28));
            Assert.Equal(0, BitConverter.ToInt32(poisoned, 28 + 4 + 4 + 12));
        }

        var idSelect = RoomCodec.WriteRoom(
            room, TestBuilds.Of("id-400").Schema, RoomCodec.TutorialSelectKind, mode: 103, mapName: "x");
        var idClean = RoomCodec.WriteTutorialSelect(room, TestBuilds.Of("id-400").Schema);
        Assert.Equal(idClean, idSelect);
        Assert.Equal(RoomCodec.TutorialSelectKind, BitConverter.ToInt32(idSelect, 22));
        Assert.Equal(
            RoomCodec.WriteMainMenu(room, TestBuilds.Of("id-400").Schema).Length,
            idSelect.Length);
    }

    [Fact]
    public void PlayRoomUsesKindTwo()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        var play = RoomCodec.WriteRoom(room, TestBuilds.Of("id-400").Schema, RoomCodec.PlayRoomKind);
        Assert.Equal(RoomCodec.PlayRoomKind, BitConverter.ToInt32(play, 22));
    }

    [Fact]
    public void Kr812PlayTutorialRoundTripsExactByteLength()
    {
        // 13 u32 + 6 bool32 + 3 empty wstr + u32 = 52 + 24 + 12 + 4 = 92.
        var buffer = new byte[92];
        var parsed = TutorialWire.ReadPlayTutorial(TestBuilds.Packets, "kr-812", buffer);
        Assert.Equal("", parsed.MapKey);
        Assert.Equal(0, parsed.Difficulty);
        Assert.Equal(0, parsed.TimeAttackTimeLimitMs);
    }

    [Fact]
    public void Id400PlayTutorialUsesFiveMapMetaBools()
    {
        // 13 u32 + 5 bool32 + 3 empty wstr + u32 = 52 + 20 + 12 + 4 = 88.
        var buffer = new byte[88];
        var parsed = TutorialWire.ReadPlayTutorial(TestBuilds.Packets, "id-400", buffer);
        Assert.Equal("", parsed.MapKey);
    }

    [Fact]
    public void Id1809PlayTutorialUsesSixMapMetaBools()
    {
        // id-1809 MapMetaData sub_416B70 / sub_416C60: 13 u32 + 6 bool32 (reader sub_7BD7C0).
        // 13 u32 + 6 bool32 + 3 empty wstr + u32 = 52 + 24 + 12 + 4 = 92, the kr-812 width.
        var buffer = new byte[92];
        var parsed = TutorialWire.ReadPlayTutorial(TestBuilds.Packets, "id-1809", buffer);
        Assert.Equal("", parsed.MapKey);
        Assert.Equal(0, parsed.Difficulty);
        Assert.Equal(0, parsed.TimeAttackTimeLimitMs);
    }

    [Fact]
    public void Id1809PlayTutorialRejectsTheFiveBoolId400Body()
    {
        // The id-400 88-byte body leaves the sixth id-1809 bool32 unread; the strict
        // reader must not silently accept the narrower layout.
        var buffer = new byte[88];
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadPlayTutorial(TestBuilds.Packets, "id-1809", buffer));
    }

    [Fact]
    public void PlayTutorialRejectsTrailingBytes()
    {
        var buffer = new byte[93];
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadPlayTutorial(TestBuilds.Packets, "kr-812", buffer));
    }

    [Fact]
    public void PlayTutorialReturnsThirdWstrAsMapKey()
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        var w = new PacketWriter(buffer);
        for (var i = 0; i < 13; i++)
            w.WriteInt32(i == TutorialWire.MapMetaTimeAttackTimeLimitIndex ? 99_999_000 : 0);
        for (var i = 0; i < 6; i++)
            w.WriteBool32(false);
        w.WriteWideStringPrefixed("dir");
        w.WriteWideStringPrefixed("display");
        w.WriteWideStringPrefixed("rel_prologue_01");
        w.WriteInt32(1);
        var parsed = TutorialWire.ReadPlayTutorial(TestBuilds.Packets, "kr-812", buffer.WrittenSpan);
        Assert.Equal("dir", parsed.Unknown0);
        Assert.Equal("display", parsed.Unknown1);
        Assert.Equal("rel_prologue_01", parsed.MapKey);
        Assert.Equal(1, parsed.Difficulty);
        Assert.Equal(99_999_000, parsed.TimeAttackTimeLimitMs);
    }

    [Fact]
    public void TutorialPlayRoomCarriesDifficultyAtFifthPostBoolU32()
    {
        // Cn-717 GameSetting: after mode/u32/4 wstr/5 u32/2 bool, seven u32; fifth is +164.
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        const string key = "rel_prologue_01";
        var body = RoomCodec.WriteRoom(
            room, TestBuilds.Of("cn-717").Schema, RoomCodec.PlayRoomKind, mode: 103, mapName: key,
            mapDirectory: key, currentMap: key, difficulty: 1);
        var dirOff = 28 + 4 + 4;
        var curOff = dirOff + 4 + key.Length * 2;
        var thirdOff = curOff + 4 + key.Length * 2;
        var mapOff = thirdOff + 4;
        var afterMap = mapOff + 4 + key.Length * 2;
        // 5 u32 + 2 bool32 + 4 u32 precede Difficulty.
        var difficultyOff = afterMap + 5 * 4 + 2 * 4 + 4 * 4;
        Assert.Equal(1, BitConverter.ToInt32(body, difficultyOff));
    }

    [Fact]
    public void RepAutoStartTrueIsFourByteBool32One()
    {
        var body = AutoStartCodec.Encode(TestBuilds.Packets, "id-400", enabled: true);
        Assert.Equal(4, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body, 0));
    }

    [Fact]
    public void RepAutoStartFalseIsFourByteBool32Zero()
    {
        var body = AutoStartCodec.Encode(TestBuilds.Packets, "id-400", enabled: false);
        Assert.Equal(4, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
    }

    [Fact]
    public void UserHostStartResultZeroIsFourByteI32()
    {
        var body = TutorialWire.WriteUserHostStart(TestBuilds.Packets, "cn-717", unknown0: 0);
        Assert.Equal(4, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
    }

    [Fact]
    public void Cn717RoomEnterGamePinsEmptyMapKeyWidth()
    {
        // u32 + empty wstr (u32 len) + u32 x3 + bool32 x3 = 4+4+12+12 = 32.
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "cn-717", 0, "");
        Assert.Equal(32, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
        Assert.Equal(0, BitConverter.ToInt32(body, 4));
    }

    [Fact]
    public void Kr812RoomEnterGameUsesTwoTrailingBools()
    {
        // u32 + empty wstr + u32 x3 + bool32 x2 = 4+4+12+8 = 28.
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "kr-812", 0, "");
        Assert.Equal(28, body.Length);
    }

    [Fact]
    public void RoomEnterGameEncodesMapKeyWideString()
    {
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "cn-717", 0, "ab");
        // u32 + wstr len2 + "a\0b\0" + u32 x3 + bool x3 = 4 + 4 + 4 + 12 + 12 = 36.
        Assert.Equal(36, body.Length);
        Assert.Equal(2, BitConverter.ToInt32(body, 4));
        Assert.Equal((byte)'a', body[8]);
        Assert.Equal((byte)'b', body[10]);
    }

    [Fact]
    public void RoomEnterGameEncodesRoomStateInUnknown4()
    {
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "cn-717", unknown0: 0, unknown1: "m", unknown4: 4);
        // u32 + wstr "m" (len1 + 2 bytes) + u32 x3 + bool x3 = 4+4+2+12+12 = 34.
        Assert.Equal(34, body.Length);
        Assert.Equal(4, BitConverter.ToInt32(body, 4 + 4 + 2 + 8));
    }

    [Fact]
    public void TutorialPlayRoomCarriesModeAndMapKey()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        var body = RoomCodec.WriteRoom(
            room, TestBuilds.Of("cn-717").Schema, RoomCodec.PlayRoomKind, mode: 103, mapName: "rel_prologue_01",
            mapDirectory: "rel_prologue_01", currentMap: "rel_prologue_01");
        Assert.Equal(RoomCodec.PlayRoomKind, BitConverter.ToInt32(body, 24));
        // GameSetting is nested inside SessionSetting; mode 103 (wgtHordeTutor) and the map key
        // must appear as little-endian u32 103 and the UTF-16LE key somewhere in the body.
        Assert.True(IndexOf(body, BitConverter.GetBytes(103)) >= 0);
        Assert.True(IndexOf(body, System.Text.Encoding.Unicode.GetBytes("rel_prologue_01")) >= 0);
    }

    [Fact]
    public void TutorialPlayRoomCarriesDirectoryAndCurrentMapBeforeMapKey()
    {
        // Cn-717 GameSetting wire after mode/u32: Directory, CurrentMap, empty third, map key.
        // Modes 101/102/103 require CurrentMap (room+132) non-empty (sub_6EB170).
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        const string key = "rel_prologue_01";
        var body = RoomCodec.WriteRoom(
            room, TestBuilds.Of("cn-717").Schema, RoomCodec.PlayRoomKind, mode: 103, mapName: key,
            mapDirectory: key, currentMap: key);
        Assert.Equal(103, BitConverter.ToInt32(body, 28));
        var dirOff = 28 + 4 + 4;
        Assert.Equal(key.Length, BitConverter.ToInt32(body, dirOff));
        Assert.Equal(key, System.Text.Encoding.Unicode.GetString(body, dirOff + 4, key.Length * 2));
        var curOff = dirOff + 4 + key.Length * 2;
        Assert.Equal(key.Length, BitConverter.ToInt32(body, curOff));
        Assert.Equal(key, System.Text.Encoding.Unicode.GetString(body, curOff + 4, key.Length * 2));
        var thirdOff = curOff + 4 + key.Length * 2;
        Assert.Equal(0, BitConverter.ToInt32(body, thirdOff));
        var mapOff = thirdOff + 4;
        Assert.Equal(key.Length, BitConverter.ToInt32(body, mapOff));
        Assert.Equal(key, System.Text.Encoding.Unicode.GetString(body, mapOff + 4, key.Length * 2));
    }

    [Fact]
    public void MapLoadingCompleteReportPinsSixteenBytes()
    {
        // Seat-log preview 000000001F2700000000000000000000.
        var payload = Convert.FromHexString("000000001F2700000000000000000000");
        var report = TutorialWire.ReadMapLoadingComplete(TestBuilds.Packets, "kr-812", payload);
        Assert.Equal(0, report.Unknown0);
        Assert.Equal(0x271F, report.Unknown1);
        Assert.Equal(0, report.Unknown2);
        Assert.Equal(0, report.Unknown3);
    }

    [Fact]
    public void MapLoadingCompleteReportRejectsTrailingBytes()
    {
        var payload = new byte[17];
        Assert.Throws<PacketFormatException>(() => TutorialWire.ReadMapLoadingComplete(TestBuilds.Packets, "kr-812", payload));
    }

    [Fact]
    public void MapLoadingCompleteSeatIsFourByteU32()
    {
        var body = TutorialWire.WriteMapLoadingComplete(TestBuilds.Packets, "kr-812", seat: 0);
        Assert.Equal(4, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
    }

    [Fact]
    public void EverybodyLoadingCompletePinsTwelveBytes()
    {
        var body = TutorialWire.WriteEverybodyLoadingComplete(TestBuilds.Packets, "kr-812", true, false, false);
        Assert.Equal(12, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body, 0));
        Assert.Equal(0, BitConverter.ToInt32(body, 4));
        Assert.Equal(0, BitConverter.ToInt32(body, 8));
    }

    [Fact]
    public void GameCountDownPinsFourByteMilliseconds()
    {
        var body = TutorialWire.WriteGameCountDown(TestBuilds.Packets, "kr-812", 4000);
        Assert.Equal(4, body.Length);
        Assert.Equal(4000, BitConverter.ToInt32(body, 0));
    }

    [Fact]
    public void GameStartGamePinsTwelveBytes()
    {
        var body = TutorialWire.WriteGameStartGame(TestBuilds.Packets, "kr-812", 0, 0, 0);
        Assert.Equal(12, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
        Assert.Equal(0, BitConverter.ToInt32(body, 4));
        Assert.Equal(0, BitConverter.ToInt32(body, 8));
    }

    [Fact]
    public void GameStartGameEncodesThreeU32Fields()
    {
        // Prologue StartGame uses MapMeta TimeAttackTimeLimit ms on field0/field2.
        var body = TutorialWire.WriteGameStartGame(TestBuilds.Packets, "kr-812", 99_999_000, 0, 99_999_000);
        Assert.Equal(12, body.Length);
        Assert.Equal(99_999_000, BitConverter.ToInt32(body, 0));
        Assert.Equal(0, BitConverter.ToInt32(body, 4));
        Assert.Equal(99_999_000, BitConverter.ToInt32(body, 8));
    }

    [Fact]
    public void CreateMobsFromSpawnGroupPinsFourByteGroup()
    {
        var body = TutorialWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, "kr-812", 1);
        Assert.Equal(4, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body, 0));
        Assert.Equal(1, TutorialWire.ReadCreateMobsFromSpawnGroup(TestBuilds.Packets, "kr-812", body));
    }

    [Fact]
    public void CreateMobsFromSpawnGroupRejectsTrailingBytes()
    {
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadCreateMobsFromSpawnGroup(TestBuilds.Packets, "kr-812", new byte[5]));
    }

    [Fact]
    public void StartNewWavePinsFourByteWave()
    {
        var body = TutorialWire.WriteStartNewWave(TestBuilds.Packets, "cn-717", 1);
        Assert.Equal(4, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body, 0));
        Assert.Equal(1, TutorialWire.ReadStartNewWave(TestBuilds.Packets, "kr-812", body));
    }

    [Fact]
    public void StartNewWaveRejectsTrailingBytes()
    {
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadStartNewWave(TestBuilds.Packets, "kr-812", new byte[5]));
    }

    [Fact]
    public void WaveAllSpawnMobsRejectsEmptyAndOversized()
    {
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.CheckWaveAllSpawnMobs(ReadOnlySpan<byte>.Empty));
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.CheckWaveAllSpawnMobs(new byte[TutorialWire.MaxWaveAllSpawnBytes + 1]));
        TutorialWire.CheckWaveAllSpawnMobs(new byte[1]);
    }

    [Fact]
    public void MobCreateBodyRejectsEmptyAndOversized()
    {
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.CheckMobCreateBody(ReadOnlySpan<byte>.Empty, "gppCreateAIObject"));
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.CheckMobCreateBody(
                new byte[TutorialWire.MaxMobCreateBytes + 1], "gppCreateAIObject"));
        TutorialWire.CheckMobCreateBody(new byte[1], "gppCreateAIObject");
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
                return i;
        }
        return -1;
    }
}
