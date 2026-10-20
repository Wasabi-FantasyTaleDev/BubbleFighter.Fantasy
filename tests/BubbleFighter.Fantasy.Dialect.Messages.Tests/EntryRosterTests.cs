using System.Collections.Concurrent;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks native room entry with sparse occupied rosters, repeat entry and unchanged scalar-only builds.</summary>
public sealed class EntryRosterTests
{
    [Theory]
    [InlineData("cn-279", 184, 149, 1408, 0, 4)]
    [InlineData("cn-324", 196, 221, 1996, 0, 8)]
    [InlineData("id-400", 292, 241, 2252, 12, 8)]
    [InlineData("cn-406", 276, 221, 2076, 4, 8)]
    public async Task EntryPublishesCurrentMembersBeforeAnyScoreReport(string build, int rowsOffset,
        int emptyRowBytes, int emptyScoreBytes, int tailBytes, int nicknameOffset)
    {
        var session = new RecordingSession(build);
        var (schema, play) = TestBuilds.Of(build);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(), NullLogger.Instance, TestBuilds.Packets);
        var first = Room([Member(2, "AB", 1, 17), Member(5, "玩家", 2, 23)]);
        await codec.SendAsync(session, new RoomMatchStarting(first, "ab"), default);
        Assert.Equal(new ushort[] { 503, 503, 510 }, session.Sent.Select(packet => packet.Opcode));
        CheckEntry(session.Sent[^1].Payload, first.Seats);

        session.Sent.Clear();
        var second = Room([Member(5, "New", 0, 29)]);
        await codec.SendAsync(session, new RoomMatchStarting(second, "ab"), default);
        Assert.Equal(new ushort[] { 503, 510 }, session.Sent.Select(packet => packet.Opcode));
        CheckEntry(session.Sent[^1].Payload, second.Seats);

        void CheckEntry(byte[] body, IReadOnlyList<RoomMemberView> members)
        {
            Assert.Equal(new byte[] { 0, 0, 0, 0, 2, 0, 0, 0, 97, 0, 98, 0 }, body[..12]);
            Assert.Equal(12 + emptyScoreBytes + tailBytes + members.Sum(member => member.Nickname.Length * 2), body.Length);
            var position = 12 + rowsOffset;
            for (var slot = 0; slot < 8; slot++)
            {
                var member = members.SingleOrDefault(value => value.Slot == slot);
                Assert.Equal(member?.Slot ?? -1, BitConverter.ToInt32(body, position));
                var characters = BitConverter.ToInt32(body, position + nicknameOffset);
                Assert.Equal(member?.Nickname ?? "", Encoding.Unicode.GetString(body, position + nicknameOffset + 4, characters * 2));
                if (member is not null && nicknameOffset == 8)
                {
                    Assert.Equal(member.Team == 0 ? 2 : member.Team,
                        BitConverter.ToInt32(body, position + nicknameOffset + 4 + characters * 2));
                    Assert.Equal(member.Level, BitConverter.ToInt32(body, position + nicknameOffset + 8 + characters * 2));
                    Assert.Equal(0, BitConverter.ToInt32(body, position + nicknameOffset + 12 + characters * 2));
                }
                position += emptyRowBytes + characters * 2;
            }
            // The native eight-word score tail precedes the build's entry suffix.
            Assert.Equal(body.Length - tailBytes - 32, position);
            Assert.Equal(new byte[32], body[position..(position + 32)]);
            if (tailBytes == 12)
                Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0 }, body[^12..]);
            else if (tailBytes == 4)
                Assert.Equal(new byte[4], body[^4..]);
        }
    }

    [Theory]
    [InlineData("kr-812", 2)]
    [InlineData("cn-717", 3)]
    [InlineData("id-1809", 1)]
    [InlineData("cn-706", 1)]
    public async Task ScalarEntryLeavesNativeSlotDerivedRosterInitializationInPlace(string build, int flags)
    {
        var session = new RecordingSession(build);
        var (schema, play) = TestBuilds.Of(build);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(), NullLogger.Instance, TestBuilds.Packets);
        var room = Room([Member(2, "AB", 1, 17), Member(5, "玩家", 2, 23)]);
        Assert.Null(PlayRoomCodec.EntryScoreboardValues(TestBuilds.Packets, build, room));
        await codec.SendAsync(session, new RoomMatchStarting(room, "ab"), default);
        Assert.Equal(new ushort[] { 503, 503, 510 }, session.Sent.Select(packet => packet.Opcode));
        var expected = new byte[24 + flags * 4];
        new byte[] { 0, 0, 0, 0, 2, 0, 0, 0, 97, 0, 98, 0 }.CopyTo(expected, 0);
        BitConverter.GetBytes(3).CopyTo(expected, 20);
        Assert.Equal(expected, session.Sent[^1].Payload);
    }

    /// <summary>
    /// A pirate room's own key names the mission's NPC table; a round the server starts on it enters the stage
    /// the settings select, as the client's own start does (kr-812 sub_816300). A key the client resolved stays.
    /// </summary>
    [Theory]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    public async Task ServerStartedPirateRoundEntersTheStageNotTheNpcTable(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(), NullLogger.Instance, TestBuilds.Packets);
        var values = new Dictionary<string, object?>
        {
            ["current_map"] = "rel_Village07_Easy02",
            ["map_key"] = "npc_Village07_Easy01",
            ["mode"] = 101,
        };
        foreach (var field in TestBuilds.Packets.Resolve(build, "RoomGameSettingChangeRequest").Where(field => field.Type == "wstr"))
            values.TryAdd(field.Name, field.Name == "map_directory" ? "Pirates" : "All");
        var change = PacketCodec.Write(TestBuilds.Packets, build, "RoomGameSettingChangeRequest", values);
        var room = new RoomView(7, "Room", 101, "npc_Village07_Easy01", 8, 2, false, [], [Member(2, "AB", 1, 17)],
            new RoomBuildSettings(build, default, change));

        var session = new RecordingSession(build);
        await codec.SendAsync(session, new RoomMatchStarting(room, room.MapKey), default);
        Assert.Equal("rel_Village07_Easy02", EnterKey(session.Sent[^1].Payload));

        session.Sent.Clear();
        await codec.SendAsync(session, new RoomMatchStarting(room, "rel_Village07_Easy03"), default);
        Assert.Equal("rel_Village07_Easy03", EnterKey(session.Sent[^1].Payload));

        static string EnterKey(byte[] body) =>
            Encoding.Unicode.GetString(body, 8, BitConverter.ToInt32(body, 4) * 2);
    }

    private static RoomView Room(IReadOnlyList<RoomMemberView> members) =>
        new(7, "Room", 1, "ab", 8, 2, false, [], members);

    private static RoomMemberView Member(int slot, string name, int team, int level) =>
        new(slot, 100 + slot, name, level, team, true, 300001, [300001]);

    private sealed class RecordingSession(string build) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new(build, build.Split('-')[0], int.Parse(build.Split('-')[1]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Tables : IOpcodeTableRegistry
    {
        private readonly IOpcodeTable _table = new EntryTable();
        public IReadOnlyList<IOpcodeTable> All => [_table];
        public IDisposable Register(IOpcodeTable table) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => _table;
        public bool TryGet(string profileId, out IOpcodeTable? table) { table = _table; return true; }
    }

    private sealed class EntryTable : IOpcodeTable
    {
        public ClientProfile Profile => throw new NotSupportedException();
        public OpcodeConnection Connection => OpcodeConnection.Game;
        public IEnumerable<OpcodeEntry> All =>
        [
            new(503, PacketDirection.ServerToClient, "gspRoomSetSlot", Evidence.Verified, null, null),
            new(510, PacketDirection.ServerToClient, "gspRoomEnterGame", Evidence.Verified, null, null),
        ];
        public IOpcodeTable ForConnection(OpcodeConnection connection) => this;
        public bool TryGet(PacketDirection direction, ushort value, out OpcodeEntry? entry) => throw new NotSupportedException();
        public string Describe(PacketDirection direction, ushort value) => throw new NotSupportedException();
    }
}
