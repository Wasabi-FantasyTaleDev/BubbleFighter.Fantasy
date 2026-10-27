using System.Collections.Concurrent;
using BubbleFighter.Fantasy.RoomCore;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class RoomRegistryTests
{
    [Fact]
    public void CreateThenRequireIsIdempotentAndBounded()
    {
        var options = new InGameOptions { MaxMatches = 1, MaxPlayersPerMatch = 1 };
        var registry = new RoomRegistry(options);
        var session = new FakeSession(1);
        var first = registry.Create(session, RoomWireSupport.WaitingSetting(), 7, "host");
        var again = registry.RequireGame(session);
        Assert.Same(first.Room, again.Room);
        Assert.Equal(0, first.Seat.Slot);

        var second = new FakeSession(2);
        Assert.Throws<InvalidOperationException>(() => registry.Create(second, RoomWireSupport.WaitingSetting(), 8, "two"));
    }

    [Fact]
    public void UnlistedRoomIsHiddenFromTheListAndJoinsUntilPublished()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(21);
        var guest = new RecordingSession(22);
        var (room, _) = registry.CreateFromSetting(
            host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 7, "host", listed: false);

        Assert.Same(room, registry.Find(room.Id));
        Assert.Null(registry.FindJoinable(room.Id));
        Assert.Empty(registry.OpenRooms());
        Assert.Throws<InvalidOperationException>(() => registry.Join(guest, room.Id, 8, "guest"));

        registry.Publish(room);
        Assert.Same(room, registry.FindJoinable(room.Id));
        Assert.Same(room, Assert.Single(registry.OpenRooms()));
        registry.Join(guest, room.Id, 8, "guest");
    }

    [Fact]
    public void TrainingRoomIsNeverListedOrJoinable()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(23);
        var (room, _) = registry.CreateTrainingRoom(host, RoomWireSupport.SampleRules(), 7, "host");

        Assert.Null(registry.FindJoinable(room.Id));
        Assert.Empty(registry.OpenRooms());
        Assert.Same(room, registry.TryGet(host.Id));
    }

    [Fact]
    public void ClosingTheHostConnectionDetachesItsSeatAndOtherClosesDoNot()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(31);
        var stranger = new RecordingSession(32);
        var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 7, "host");

        registry.DetachConnection(stranger.Connection.Id);
        Assert.Same(room, registry.TryGet(host.Id));

        registry.DetachConnection(host.Connection.Id);
        Assert.Null(registry.TryGet(host.Id));
        Assert.Null(registry.Find(room.Id));
    }

    [Fact]
    public void BoundRelaySharesTheGamePlayerUntilItIsReleased()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(41);
        var player = new PlayerSession(new PlayerIdentity(7, "host", "host", 1), host.Connection.Id);
        player.MoveTo(SessionPhase.MainMenu);
        using var login = host.RegisterFeature<IPlayerSession>(player);
        var relay = new RecordingSession(42);
        var (_, seat) = registry.CreateFromSetting(host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 7, "host");

        registry.BindRelay(relay, seat.Key);
        Assert.True(relay.TryFeature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>(out var shared));
        Assert.Same(player, shared);
        Assert.Equal(host.Connection.Id, shared.LoginConnectionId);
        Assert.NotEqual(relay.Connection.Id, shared.LoginConnectionId);
        Assert.Equal(7, SessionPlayer.ResolveUserNum(relay));

        registry.Release(relay);
        Assert.False(relay.TryFeature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>(out _));
        Assert.Same(player, host.Feature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>());
    }

    [Fact]
    public async Task RepeatedDeparturesForgetConnectionIndexesWithoutWaitingForSocketClose()
    {
        var registry = new RoomRegistry(new InGameOptions { MaxMatches = 1 });
        var departed = new List<RecordingSession>();
        try
        {
            for (var cycle = 0; cycle < 32; cycle++)
            {
                var game = new RecordingSession(100 + cycle * 2);
                var relay = new RecordingSession(101 + cycle * 2);
                var (room, seat) = registry.Create(game, RoomWireSupport.WaitingSetting(), 7, "host");
                registry.BindRelay(relay, seat.Key);
                Assert.True(registry.TryGetSessionOfConnection(game.Connection.Id, out _));
                Assert.True(registry.TryGetSessionOfConnection(relay.Connection.Id, out _));

                if (cycle % 2 == 0) registry.Release(relay);
                else registry.Detach(relay.Id);
                Assert.False(registry.TryGetSessionOfConnection(relay.Connection.Id, out _));
                Assert.Same(room, registry.FindByConnection(game.Connection.Id));

                registry.Detach(game.Id);
                Assert.False(registry.TryGetSessionOfConnection(game.Connection.Id, out _));
                departed.AddRange([game, relay]);
                foreach (var old in departed)
                {
                    Assert.Null(registry.FindByConnection(old.Connection.Id));
                    registry.DetachConnection(old.Connection.Id);
                }
                Assert.Empty(registry.All());
            }
        }
        finally { await registry.StopAllAsync(); }
    }

    [Fact]
    public async Task GameDepartureForgetsBothIndexesAndLateRelayCloseKeepsTheRemainingSeat()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(41);
        var guest = new RecordingSession(42);
        var relay = new RecordingSession(43);
        host.SetLobbyPlayer(7, "host");
        var (room, seat) = registry.Create(host, RoomWireSupport.WaitingSetting(), 7, "host");
        registry.Join(guest, room.Id, 8, "guest");
        registry.BindRelay(relay, seat.Key);
        try
        {
            registry.Detach(host.Id);
            Assert.False(registry.TryGetSessionOfConnection(host.Connection.Id, out _));
            Assert.False(registry.TryGetSessionOfConnection(relay.Connection.Id, out _));
            Assert.False(relay.TryFeature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>(out _));
            registry.DetachConnection(relay.Connection.Id);
            Assert.Same(room, registry.FindByConnection(guest.Connection.Id));
            Assert.Same(guest, Assert.Single(room.Seats).Game);
        }
        finally { await registry.StopAllAsync(); }
    }

    [Fact]
    public void ReadyWithoutCreateFailsLoud()
    {
        var registry = new RoomRegistry(new InGameOptions());
        Assert.Throws<InvalidOperationException>(() => registry.RequireGame(new FakeSession(1)));
    }

    [Fact]
    public void TryGameReturnsNullInsteadOfThrowing()
    {
        // The relay-endpoint handler used to call RequireGame, and a live session
        // disconnected because of it: the client re-created its room after the
        // prepare wall dismissed, which left the session with no room, and the
        // throw closed the connection. TryGame is the form that treats "no room"
        // as an ordinary outcome.
        var registry = new RoomRegistry(new InGameOptions());
        Assert.Null(registry.TryGame(new FakeSession(1)));

        var session = new FakeSession(2);
        var (room, seat) = registry.Create(session, RoomWireSupport.WaitingSetting(), 7, "host");
        var found = registry.TryGame(session);
        Assert.NotNull(found);
        Assert.Same(room, found.Value.Room);
        Assert.Equal(seat.Slot, found.Value.Seat.Slot);
    }

    [Fact]
    public void JoinUnknownRoomFailsLoud()
    {
        var registry = new RoomRegistry(new InGameOptions());
        Assert.Throws<InvalidOperationException>(() =>
            registry.Join(new FakeSession(1), roomId: 99, userNum: 1, nickname: "x"));
    }

    [Fact]
    public void JoinExistingRoomSeatsSecondPlayer()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new FakeSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSetting(), 1, "host");
        var guest = new FakeSession(11);
        var joined = registry.Join(guest, room.Id, 2, "guest");
        Assert.Same(room, joined.Room);
        Assert.Equal(1, joined.Seat.Slot);
    }

    [Fact]
    public void BindRelayRequiresIssuedKey()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var game = new FakeSession(10);
        var (_, seat) = registry.Create(game, RoomWireSupport.WaitingSetting(), 1, "host");
        var relay = new FakeSession(11);
        var bound = registry.BindRelay(relay, seat.Key);
        Assert.Same(seat, bound);
        Assert.Same(relay, seat.Relay);
        Assert.Throws<InvalidOperationException>(() =>
            registry.BindRelay(new FakeSession(12), seat.Key with { Unknown0 = -1 }));
    }

    [Fact]
    public void DetachClosesEmptyRoom()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var session = new FakeSession(3);
        registry.Create(session, RoomWireSupport.WaitingSetting(), 1, "host");
        registry.Detach(session.Id);
        Assert.Null(registry.TryGet(session.Id));
    }

    [Fact]
    public void CreateFromRulesSeatsTheHostAndKeepsTheRecord()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, seat) = registry.CreateFromRules(
            new FakeSession(1), RoomWireSupport.SampleRules(), 10, "host");

        Assert.Equal(0, seat.Slot);
        Assert.Single(room.Seats);
        Assert.NotNull(room.CreateRules);
        Assert.Equal(180000, room.CreateRules!.EndGameTimeOver);
        Assert.Equal(20, room.CreateRules.DeathMatchKill);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
    }

    [Fact]
    public void JoinAfterCreateFromRulesSeatsTheSecondPlayer()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.CreateFromRules(new FakeSession(1), RoomWireSupport.SampleRules(), 10, "host");
        var joined = registry.Join(new FakeSession(2), room.Id, 11, "guest");

        Assert.Same(room, joined.Room);
        Assert.Equal(2, room.Seats.Count);
        Assert.Equal(1, joined.Seat.Slot);
    }

    [Fact]
    public void CreateFromRulesRoomSettingUsesTheRoomStageGateKind()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.CreateFromRules(new FakeSession(1), RoomWireSupport.SampleRules(), 10, "host");

        // kind 2 gates the RoomStage8vs8 apply in the client's snapshot path; the map stays empty because the
        // rule-record to map offset mapping is unverified.
        Assert.Equal(SessionSettingBlob.MainMenuKind, room.Setting.Kind);
        Assert.True(room.Setting.Map.IsEmpty);
    }

    [Fact]
    public void CreateFromSettingNormalizesTheRoomStageKindAndKeepsTheMap()
    {
        var registry = new RoomRegistry(new InGameOptions());
        // The live client sends kind 0 on create (2026-09-20 10:15Z); the room
        // snapshot handler applies the room stage only at kind 2.
        var clientSetting = RoomWireSupport.WaitingSetting() with { Kind = 0 };
        var (room, seat) = registry.CreateFromSetting(new FakeSession(1), clientSetting, 10, "host");

        Assert.Equal(2, room.Setting.Kind);
        Assert.False(room.Setting.Map.IsEmpty);
        Assert.Null(room.CreateRules);
        Assert.Equal(0, seat.Slot);
    }

    [Fact]
    public void CreateFromSettingNormalizesEveryClientKindToTheRoomStageGate()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.CreateFromSetting(
            new FakeSession(1), RoomWireSupport.WaitingSetting() with { Kind = 1 }, 10, "host");

        Assert.Equal(2, room.Setting.Kind);
    }

    [Fact]
    public void CreateFromSettingWritesTheWaitingStateTheStartClickRequires()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.CreateFromSetting(
            new FakeSession(1), RoomWireSupport.WaitingSetting() with { Kind = 0 }, 10, "host");

        // The kind-2 arm test at 0x9ACAB4 reads the state the 117 itself carried (the held session is destroyed
        // at 0x9AC6C2 before the check), BOTH arms converge on stage 4 for mode 1 with clear flags, and the
        // start click requires exactly 2 (kr-1256 sub_970590), so the install uses 2.
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, room.Setting.Unknown4);
        Assert.Equal(SessionSettingBlob.MainMenuKind, room.Setting.Kind);
        // The MapMeta gate fields the panel and spawner read stay clear.
        Assert.Equal(0, room.Setting.Map.Unknown0);
        Assert.Equal(0, room.Setting.Map.Unknown20);
    }

    [Fact]
    public void CreateFromRulesWritesTheWaitingStateTheStartClickRequires()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var (room, _) = registry.CreateFromRules(new FakeSession(1), RoomWireSupport.SampleRules(), 10, "host");

        // The rule-record create path builds its setting from scratch, so the
        // waiting state has to come from the same factory.
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, room.Setting.Unknown4);
        Assert.Equal(SessionSettingBlob.MainMenuKind, room.Setting.Kind);
    }

    [Fact]
    public void SeatFallsBackToTheLobbyCharacterType()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var session = new FakeSession(1);
        // The registry no longer resolves character ids itself; the seat keeps
        // its reference defaults until the handler fills it through the
        // resolver (see SeatCharacterResolverTests).
        session.SetLobbyPlayer(10, "host", pastLoginBurst: false);
        var (_, seat) = registry.CreateFromSetting(
            session, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 10, "host");

        Assert.Equal(300001, seat.LocalCharacterId);
        Assert.Equal(0, seat.CharacterId);
    }

    [Fact]
    public void SeatKeepsTheDaoReferenceUntilTheHandlerResolvesIt()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var session = new FakeSession(1);
        session.SetLobbyPlayer(10, "host", pastLoginBurst: false);
        var (_, seat) = registry.CreateFromSetting(
            session, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 10, "host");

        Assert.Equal(300001, seat.LocalCharacterId);
        Assert.Equal(0, seat.CharacterId);
    }

    [Fact]
    public void CreateFromSettingClearsTheMapMetaGateFieldsAndKeepsTheMode()
    {
        var registry = new RoomRegistry(new InGameOptions());
        // A client MapMeta carrying the flags word and the order-4 dword the
        // room-stage and member-panel gates reject, plus a live mode.
        var clientSetting = RoomWireSupport.WaitingSetting() with
        {
            Kind = 0,
            Map = RoomWireSupport.WaitingSetting().Map with { Unknown0 = 0x2, Unknown20 = 7 },
        };

        var (room, _) = registry.CreateFromSetting(
            new RoomRegistryTests.FakeSession(1), clientSetting, 10, "host");

        // The gates read MapMeta+0 (flags) and MapMeta+20 through the
        // self-pointers the MapMeta ctor plants, so both must be zero.
        Assert.Equal(0, room.Setting.Map.Unknown0);
        Assert.Equal(0, room.Setting.Map.Unknown20);
        // The client's mode selection survives.
        Assert.Equal(clientSetting.Map.Unknown4, room.Setting.Map.Unknown4);
        Assert.Equal(SessionSettingBlob.MainMenuKind, room.Setting.Kind);
    }

    /// <summary>In-memory game-protocol session stub. Shared by the registry tests.</summary>
    /// <param name="id">The session id.</param>
    internal sealed class FakeSession(long id) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        public long Id { get; } = id;
        public IConnection Connection { get; } = new FakeConnection();
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }

    /// <summary>Lobby player record stub read by property name through <c>SessionPlayer</c>.</summary>
    /// <param name="UserNum">The identity number.</param>
    /// <param name="Nickname">The display name.</param>
    internal sealed record FakePlayer(int UserNum, string Nickname)
    {
        /// <summary>Gets or sets the lobby character type (1 = Dao).</summary>
        public int CharacterType { get; init; }

        /// <summary>Gets or sets the resolved equipped character item id.</summary>
        public int EquippedItemId { get; init; }

        /// <summary>Gets or sets the player level the seat records carry.</summary>
        public int Level { get; init; } = 1;
    }

    /// <summary>In-memory connection stub; the pipe members are never touched by the registry.</summary>
    internal sealed class FakeConnection : IConnection
    {
        public long Id => 0;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
