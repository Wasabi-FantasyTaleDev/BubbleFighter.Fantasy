using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Wire round-trips and room/list projections for player-hosted prize rooms (<c>user_host</c>)
/// across all six audited builds and negative checks on unverified builds.
/// </summary>
public sealed class UserHostWireTests
{
    private static readonly string[] EnabledBuilds = ["kr-812", "cn-717", "id-1809", "kr-1256", "cn-774", "tw-454"];
    private static readonly string[] DisabledBuilds = ["cn-735", "cn-406", "cn-279", "id-400"];

    [Theory]
    [InlineData("kr-812", 16, 12)]
    [InlineData("cn-717", 16, 12)]
    [InlineData("id-1809", 16, 12)]
    [InlineData("kr-1256", 36, 12)]
    [InlineData("cn-774", 36, 12)]
    [InlineData("tw-454", 36, 12)]
    public async Task UserHostBindingExposesAuditedRefereePolicyAndRoundTripsAllMessages(
        string build, byte expectedTeam, int expectedSlot)
    {
        var opcodes = TestBuilds.Opcodes(build);
        var isModern = build is "kr-1256" or "cn-774" or "tw-454";
        var binding = new UserHostBinding($"{build}/game", opcodes, TestBuilds.Packets, build, NullLogger.Instance,
            bindModernStart: isModern);
        binding.Validate();

        Assert.Equal(expectedTeam, binding.RefereeTeam);
        Assert.Equal(expectedSlot, binding.RefereeSlotIndex);

        var settingBody = PacketCodec.Write(TestBuilds.Packets, build, "UserHostSettingRequest", new Dictionary<string, object?>
        {
            ["action"] = (int)UserHostSettingAction.ConfirmPrize,
            ["prize"] = 25_000,
        });
        var decodedSetting = binding.ReadSetting(settingBody);
        Assert.Equal((int)UserHostSettingAction.ConfirmPrize, decodedSetting.Action);
        Assert.Equal(25_000u, decodedSetting.Prize);

        if (isModern)
        {
            var startReq = UserHostBinding.ReadModernStart(ReadOnlySpan<byte>.Empty);
            Assert.True(startReq.FromUserHostPacket);
        }

        var session = new RecordingSession(build);
        await binding.SendAsync(session, new UserHostSettingUpdated((int)UserHostSettingAction.ConfirmPrize, 25_000u, 1_000u, 100_000u), default);
        await binding.SendAsync(session, new UserHostDefaultsPublished(1_000u, 100_000u), default);
        foreach (var status in new[] { 0, -1, -2, -3, -4, -5 })
            await binding.SendAsync(session, new UserHostStartReplied(status), default);

        Assert.Equal(8, session.Sent.Count);

        var updateFields = PacketCodec.Read(TestBuilds.Packets, build, "UserHostSettingUpdate", session.Sent[0].Body);
        Assert.Equal((int)UserHostSettingAction.ConfirmPrize, (int)updateFields["action"]!);
        Assert.Equal(25_000, (int)updateFields["prize"]!);
        Assert.Equal(1_000, (int)updateFields["min_prize"]!);
        Assert.Equal(100_000, (int)updateFields["max_prize"]!);

        var defaultFields = PacketCodec.Read(TestBuilds.Packets, build, "UserHostDefaultSetting", session.Sent[1].Body);
        Assert.Equal(1_000, (int)defaultFields["min_prize"]!);
        Assert.Equal(100_000, (int)defaultFields["max_prize"]!);

        var expectedStatuses = new[] { 0, -1, -2, -3, -4, -5 };
        for (var i = 0; i < expectedStatuses.Length; i++)
        {
            var startFields = PacketCodec.Read(TestBuilds.Packets, build, "UserHostStartReply", session.Sent[2 + i].Body);
            Assert.Equal(expectedStatuses[i], (int)startFields["status"]!);
        }
    }

    [Theory]
    [MemberData(nameof(DisabledBuildRows))]
    public void UserHostBindingRejectsUnverifiedBuilds(string build)
    {
        var opcodes = TestBuilds.Opcodes(build);
        var binding = new UserHostBinding($"{build}/game", opcodes, TestBuilds.Packets, build, NullLogger.Instance);
        Assert.Throws<ConfigException>(() => binding.Validate());
    }

    public static IEnumerable<object[]> DisabledBuildRows() => DisabledBuilds.Select(b => new object[] { b });

    [Theory]
    [InlineData("kr-812", 16)]
    [InlineData("cn-717", 16)]
    [InlineData("id-1809", 16)]
    public async Task LegacyRoomCodecPreservesIsUserHostAndPlayableOpenSeats(string build, byte refereeTeam)
    {
        var (schema, play) = TestBuilds.Of(build);
        var registry = new TestOpcodeRegistry();
        registry.Register(TestBuilds.Opcodes(build), build);
        var codec = new PlayRoomCodec($"{build}/game", build, schema, play, registry, NullLogger.Instance, TestBuilds.Packets)
        {
            UserHost = true,
        };

        var session = new RecordingSession(build);
        var refSeat = new RoomMemberView(12, 1001, "HostRef", 10, refereeTeam, false, 300001, [300001]);
        var playerSeat = new RoomMemberView(0, 1002, "Fighter", 5, 1, true, 300001, [300001]);
        var closed = Enumerable.Range(8, 4).Concat(Enumerable.Range(13, 3)).ToArray();
        var view = new RoomView(77, "Sponsor Cup", 3, "rel_Village01_Byway", 16, 12, false, closed, [playerSeat, refSeat], null)
        {
            PlayableMaxSeats = 12,
            IsUserHost = true,
        };
        await codec.SendAsync(session, new RoomEntered(view, 12), default);

        Assert.NotEmpty(session.Sent);
        var installOpcode = TestBuilds.Opcodes(build).S2C("gspGotoRoom");
        var installBody = session.Sent.Single(s => s.Opcode == installOpcode).Body;
        var reader = new PacketReader(installBody);
        var decoded = PlazaRoomWire.ReadPlaySessionSetting(ref reader, schema);
        var sessionFields = PacketCodec.Read(TestBuilds.Packets, build, "SessionSetting", installBody[..reader.Offset]);
        // Playable 12 minus closed [8..11] = 8 open seats (excluding non-playable 13..15).
        Assert.Equal((ushort)8, Convert.ToUInt16(sessionFields["open_seats"]));
        var gameSetting = installBody[decoded.GameSettingStart..decoded.GameSettingEnd];
        Assert.True(UserHostBinding.ReadIsUserHost(TestBuilds.Packets, build, gameSetting));
        var cleared = UserHostBinding.WriteIsUserHost(TestBuilds.Packets, build, gameSetting, false);
        Assert.False(UserHostBinding.ReadIsUserHost(TestBuilds.Packets, build, cleared));
    }

    [Theory]
    [InlineData("kr-1256", "mode")]
    [InlineData("cn-774", "unknown_4")]
    [InlineData("tw-454", "unknown_0")]
    public void ModernRoomProjectionAndListSetIsUserHostWhenEnabled(string build, string modeField)
    {
        var tables = TestBuilds.Packets;
        var createFields = new Dictionary<string, object?>
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "Prize Cup",
                ["game_setting"] = new Dictionary<string, object?>
                {
                    [modeField] = 3,
                    ["is_user_host"] = true,
                },
            },
            ["password"] = "pw",
        };
        if (tables.Resolve(build, "RoomCreateRequest").Any(field => field.Name == "unknown_n2"))
            createFields["unknown_n2"] = (byte)0xBA;
        var createBody = PacketCodec.Write(tables, build, "RoomCreateRequest", createFields);

        var createReq = RoomProjection.Create(tables, build, createBody, userHost: true);
        Assert.True(createReq.IsUserHost);
        Assert.Equal("Prize Cup", createReq.Name);
        Assert.Equal(3, createReq.ModeId);
        Assert.False(RoomProjection.Create(tables, build, createBody, userHost: false).IsUserHost);

        var refereeTeam = UserHostBinding.TryReadRefereeSeat(tables, build, out var team, out var slot)
            ? (team, slot)
            : throw new InvalidOperationException();
        var refMember = new RoomMemberView(refereeTeam.slot, 1001, "HostRef", 10, refereeTeam.team, false, 300001, [300001]);
        var view = new RoomView(55, "Prize Cup", 3, "", 16, refereeTeam.slot, false, [], [refMember], createReq.BuildSettings)
        {
            PlayableMaxSeats = 12,
            IsUserHost = true,
        };
        var sessionFields = RoomProjection.Session(tables, build, view, userHost: true);
        Assert.Equal((ushort)12, Convert.ToUInt16(sessionFields["open_seats"]));
        var projectedGame = Assert.IsType<Dictionary<string, object?>>(sessionFields["game_setting"]);
        Assert.Equal(true, projectedGame["is_user_host"]);

        var entry = new RoomListEntry(55, "Prize Cup", "HostRef", 3, 1, 12, false, null)
        {
            IsUserHost = true,
        };
        var rowBytes = RoomListCodec.Write(tables, build, new RoomListSnapshot(0, [entry]), pageSize: 10, rowSettings: null, userHost: true);
        var replyFields = PacketCodec.Read(tables, build, "RoomListReply", rowBytes);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<object?>>(replyFields["rooms"]);
        var rowMap = Assert.IsType<Dictionary<string, object?>>(Assert.Single(rows));
        var listGame = Assert.IsType<Dictionary<string, object?>>(rowMap["game_setting"]);
        Assert.Equal(true, listGame["is_user_host"]);
    }

    private sealed class TestOpcodeRegistry : IOpcodeTableRegistry
    {
        private readonly Dictionary<string, IOpcodeTable> _tables = new(StringComparer.Ordinal);
        public IReadOnlyList<IOpcodeTable> All => [.. _tables.Values];
        public void Register(IOpcodeTable table, string profileId) => _tables[profileId] = table;
        public IDisposable Register(IOpcodeTable table)
        {
            _tables[table.Profile.Id] = table;
            return DummyDisposable.Instance;
        }
        public IOpcodeTable Get(string profileId) => _tables[profileId];
        public bool TryGet(string profileId, out IOpcodeTable? table) => _tables.TryGetValue(profileId, out table);
        public IOpcodeTable ForProtocol(string protocol) => _tables[protocol.Split('/')[0]];

        private sealed class DummyDisposable : IDisposable
        {
            public static readonly DummyDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingSession(string build) : ISessionWithFeatures
    {
        public long Id => 1;
        public IConnection Connection { get; } = new FakeConnection();
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ClientProfile Profile { get; } = new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Body)> Sent { get; } = [];

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnection : IConnection
    {
        public long Id => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();
        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
