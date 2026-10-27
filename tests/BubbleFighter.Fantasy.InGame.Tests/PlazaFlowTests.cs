using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The typed plaza flow: who hears about an entrant, a leaver and a position, and where a chat
/// line goes — asserted as messages, the wire being the dialect's.
/// </summary>
public sealed class PlazaFlowTests
{
    private sealed class Sink<T> : IMessageSender<T> where T : IMessage
    {
        public List<(long Session, T Message)> Sent { get; } = [];
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add((session.Id, message));
            return ValueTask.CompletedTask;
        }
        public T[] To(ISession session)
        {
            lock (Sent)
                return [.. Sent.Where(row => row.Session == session.Id).Select(row => row.Message)];
        }
    }

    /// <summary>Recoloring retains the plaza entry identity, current position and saved menu appearance.</summary>
    [Fact]
    public async Task RecoloringKeepsPositionAndMenuAppearance()
    {
        var rig = new Rig();
        var owner = Rig.Player(11, "owner");
        var observer = Rig.Player(22, "observer");
        owner.Features.Get<IPlayerSession>().ChangeColorIndex(8);
        try
        {
            await rig.Flow.HandlePlazaEnterAsync(owner, new(), default);
            await rig.Flow.HandlePlazaEnterAsync(observer, new(), default);
            var before = rig.PlazaEntered.To(observer).Single().Members.Single(member => member.UserNumber == 11);
            Assert.Equal((ushort)8, before.ColorIndex);
            await rig.Flow.HandlePlazaPositionAsync(owner, new(Upload), default);
            var position = rig.Position.To(observer).Last();
            rig.MemberEntered.Sent.Clear();
            rig.Position.Sent.Clear();
            owner.Features.Get<IPlayerSession>().ChangeColorIndex(14);
            await rig.Flow.OnColorChangedAsync(new PlayerColorChanged(11, 14), default);
            Assert.Equal(before with { ColorIndex = 14 }, Assert.Single(rig.MemberEntered.To(observer)).Member);
            Assert.Equal(position, Assert.Single(rig.Position.To(observer)));
            Assert.Empty(rig.MemberEntered.To(owner));
            await rig.Flow.HandlePlazaMenuReturnAsync(owner, new(), default);
            Assert.Equal((ushort)14, Assert.Single(rig.MenuReturn.To(owner)).You!.ColorIndex);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private sealed class Rig
    {
        public RoomRegistry Registry { get; }
        public RoomFlow Flow { get; }
        public Sink<RoomEntered> Entered { get; } = new();
        public Sink<RoomSeatUpdated> SeatUpdated { get; } = new();
        public Sink<RoomSeatVacated> SeatVacated { get; } = new();
        public Sink<RoomMasterChanged> MasterChanged { get; } = new();
        public Sink<RoomLeft> Left { get; } = new();
        public Sink<RoomChatReceived> Chat { get; } = new();
        public Sink<PlazaEntered> PlazaEntered { get; } = new();
        public Sink<PlazaMemberEntered> MemberEntered { get; } = new();
        public Sink<PlazaMemberLeft> MemberLeft { get; } = new();
        public Sink<PlazaPositionReceived> Position { get; } = new();
        public Sink<PlazaMenuReturnRequested> MenuReturn { get; } = new();
        public Sink<MainMenuRoomOpened> MainMenu { get; } = new();

        public Rig(InGameOptions? options = null)
        {
            Registry = new RoomRegistry(new InGameOptions { MaxPlayersPerMatch = 16 });
            Flow = new RoomFlow(Registry, new RoomSenders(Entered, SeatUpdated, SeatVacated, MasterChanged,
                Left: Left, Chat: Chat, PlazaEntered: PlazaEntered, PlazaMemberEntered: MemberEntered,
                PlazaMemberLeft: MemberLeft, PlazaPositionReceived: Position, MenuReturn: MenuReturn, MainMenu: MainMenu), NullLogger.Instance, options: options);
        }

        public static RecordingSession Player(long id, string nickname)
        {
            var session = new RecordingSession(id);
            session.SetLobbyPlayer(id, nickname, level: 3);
            return session;
        }
    }

    // A position upload, verbatim as the client sends it (22 bytes).
    private static readonly ReadOnlyMemory<byte> Upload = Convert.FromHexString(
        "06000000D8523144585C3343E4FC04440000157A43C0");

    /// <summary>Builds with complete stage installs may omit synthetic positions and forward reports only to peers.</summary>
    [Fact]
    public async Task NativePlazaPolicyRelaysOnlyRealReportsToPeers()
    {
        var rig = new Rig(new InGameOptions { PlazaReplayPositionsOnEnter = false, PlazaEchoPositions = false });
        var first = Rig.Player(11, "first");
        var second = Rig.Player(22, "second");
        try
        {
            await EnterAsync(rig, first);
            await EnterAsync(rig, second);
            Assert.Empty(rig.Position.Sent);
            await rig.Flow.HandlePlazaPositionAsync(first, new(Upload), default);
            Assert.Empty(rig.Position.To(first));
            Assert.Equal(Upload, Assert.Single(rig.Position.To(second)).Body);
            var secondEntry = Assert.Single(rig.PlazaEntered.To(second));
            Assert.Equal(new[] { 0, 1 }, secondEntry.MemberOrder);
            await EnterAsync(rig, first);
            Assert.Equal(new[] { 0, 1 }, rig.PlazaEntered.To(first)[^1].MemberOrder);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A complete menu reinstall receives the current own appearance and needs no redundant peer vacates.</summary>
    [Fact]
    public async Task MenuReturnCarriesTheAuthoritativeOwnAppearance()
    {
        var rig = new Rig(new InGameOptions { PlazaVacatePeersOnMenuReturn = false });
        var first = Rig.Player(11, "first");
        var second = Rig.Player(22, "second");
        try
        {
            await EnterAsync(rig, first);
            await EnterAsync(rig, second);
            await rig.Flow.HandlePlazaMenuReturnAsync(first, new(), default);
            Assert.Empty(rig.MemberLeft.To(first));
            Assert.Equal(11, Assert.Single(rig.MemberLeft.To(second)).Member.UserNumber);
            var own = Assert.IsType<PlazaMember>(Assert.Single(rig.MenuReturn.To(first)).You);
            Assert.Equal((0, 11, "first", 3, 300001), (own.Slot, own.UserNumber, own.Nickname, own.Level, own.CharacterItemId));
            Assert.Equal(new[] { 300001 }, own.EquippedItemIds);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static async Task EnterAsync(Rig rig, ISession session)
    {
        await rig.Flow.HandlePlazaEnterAsync(session, new PlazaEnterRequested(), CancellationToken.None);
        _ = rig.Position.To(session); // touch nothing; the sinks record on their own
    }

    [Fact]
    public async Task EnteringSendsTheEntrantThePresentMembersAndTellsThePresentAboutTheEntrant()
    {
        var rig = new Rig();
        var first = Rig.Player(11, "first");
        var second = Rig.Player(22, "second");

        await EnterAsync(rig, first);
        var own = Assert.Single(rig.PlazaEntered.To(first));
        Assert.Equal((11, 0), (own.You.UserNumber, own.You.Slot));
        Assert.Empty(own.Members);
        Assert.Empty(rig.MemberEntered.Sent);

        await EnterAsync(rig, second);
        var other = Assert.Single(rig.PlazaEntered.To(second));
        Assert.Equal(1, other.You.Slot);
        var present = Assert.Single(other.Members);
        Assert.Equal((11, 0), (present.UserNumber, present.Slot));
        var told = Assert.Single(rig.MemberEntered.Sent, row => row.Message.Member.UserNumber == 22);
        Assert.Equal((first.Id, 22), (told.Session, told.Message.Member.UserNumber));
        Assert.Equal(SessionPhase.Plaza, second.Features.Get<IPlayerSession>().Phase);
    }

    [Fact]
    public async Task APositionIsStampedPerMemberAndBroadcastToEveryPresentPlayer()
    {
        var rig = new Rig();
        var first = Rig.Player(11, "first");
        var second = Rig.Player(22, "second");
        await EnterAsync(rig, first);
        await EnterAsync(rig, second);

        await rig.Flow.HandlePlazaPositionAsync(first, new PlazaPositionReported(Upload), CancellationToken.None);
        await rig.Flow.HandlePlazaPositionAsync(first, new PlazaPositionReported(Upload), CancellationToken.None);
        await rig.Flow.HandlePlazaPositionAsync(second, new PlazaPositionReported(Upload), CancellationToken.None);

        var firsts = rig.Position.To(first);
        Assert.Equal(3, firsts.Length);
        Assert.Equal([2, 3, 2], firsts.Select(line => line.Sequence));
        Assert.Equal([11, 11, 22], firsts.Select(line => line.UserNumber));
        Assert.All(firsts, line => Assert.Equal(Upload.Span.ToArray(), line.Body.ToArray()));
        // The second player was also shown where the first one stood on entry: an empty position.
        var seconds = rig.Position.To(second).Where(line => !line.Body.IsEmpty).ToArray();
        Assert.Equal(3, seconds.Length);
        Assert.Equal([2, 3, 2], seconds.Select(line => line.Sequence));
    }

    [Fact]
    public async Task EnteringARoomVacatesThePlazaSeatAndLeavingTheRoomPutsThePlayerBack()
    {
        var rig = new Rig();
        var first = Rig.Player(11, "first");
        var second = Rig.Player(22, "second");
        await EnterAsync(rig, first);
        await EnterAsync(rig, second);

        await rig.Flow.HandleCreateAsync(second, new RoomCreateRequested("room", "", 4, "rel_Village07_Easy02"),
            CancellationToken.None);

        // The plaza hears the seat vacate; the room is not listed as a plaza member any more.
        var left = Assert.Single(rig.MemberLeft.Sent);
        Assert.Equal((first.Id, 22), (left.Session, left.Message.Member.UserNumber));
        Assert.Empty(rig.PlazaEntered.To(first).Last().Members);
        await EnterAsync(rig, first); // a re-enter while the other sits in a room sees nobody
        Assert.Empty(rig.PlazaEntered.To(first).Last().Members);

        await rig.Flow.HandleLeaveAsync(second, new RoomLeaveRequested(), CancellationToken.None);

        // The leaver is pushed back into the plaza without a new request, and the member present
        // hears the return after having heard the original entry.
        Assert.Equal(2, rig.PlazaEntered.To(second).Length);
        Assert.Equal(22, rig.PlazaEntered.To(second).Last().You.UserNumber);
        var returns = rig.MemberEntered.To(first);
        Assert.Equal(2, returns.Length);
        Assert.All(returns, row => Assert.Equal(22, row.Member.UserNumber));
        Assert.Equal(SessionPhase.Plaza, second.Features.Get<IPlayerSession>().Phase);
    }

    [Fact]
    public async Task AClosedConnectionVacatesItsVisibleSeatBeforeItCanBeReused()
    {
        var rig = new Rig();
        var observer = Rig.Player(11, "observer");
        var departed = Rig.Player(22, "departed");
        var replacement = Rig.Player(33, "replacement");
        await EnterAsync(rig, observer);
        await EnterAsync(rig, departed);
        var originalSlot = rig.PlazaEntered.To(departed).Single().You.Slot;

        await departed.Connection.CloseAsync("test disconnect");
        rig.Flow.OnPlazaConnectionClosed(new(departed.Connection, "test disconnect"));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (rig.MemberLeft.To(observer).Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        var left = Assert.Single(rig.MemberLeft.To(observer));
        Assert.Equal(22, left.Member.UserNumber);
        Assert.Equal(originalSlot, left.Member.Slot);

        // Repeated close events cannot remove the replacement that reuses this slot.
        await EnterAsync(rig, replacement);
        rig.Flow.OnPlazaConnectionClosed(new(departed.Connection, "duplicate disconnect"));
        await rig.Flow.HandlePlazaPositionAsync(observer, new(Upload), default);
        Assert.Single(rig.MemberLeft.To(observer));
        Assert.Equal(originalSlot, rig.PlazaEntered.To(replacement).Single().You.Slot);
        Assert.DoesNotContain(rig.PlazaEntered.To(replacement).Single().Members, member => member.UserNumber == 22);
        Assert.Contains(rig.Position.To(replacement), position => position.UserNumber == 11 && !position.Body.IsEmpty);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task SelectionReturnRestoresTheLocalSessionWithoutVacatingItsNewSeat()
    {
        var rig = new Rig();
        var observer = Rig.Player(11, "observer");
        var player = Rig.Player(22, "player");
        await EnterAsync(rig, observer);
        await EnterAsync(rig, player);
        player.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Room);
        Assert.Null(rig.Registry.TryGet(player.Id));

        await rig.Flow.HandlePlazaMenuReturnAsync(player, new(), default);

        var restored = Assert.Single(rig.MainMenu.To(player));
        Assert.Equal(22, restored.UserNumber);
        Assert.Equal("player", restored.Nickname);
        Assert.Contains(restored.CharacterItemId, restored.EquippedItemIds);
        Assert.False(restored.OfferTutorial);
        Assert.Equal(SessionPhase.MainMenu, player.Features.Get<IPlayerSession>().Phase);
        Assert.Single(rig.MemberLeft.To(observer));
        Assert.Empty(rig.MemberLeft.To(player));
        Assert.Empty(rig.MenuReturn.To(player));
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task ChatGoesToTheRoomThePlazaOrTheSpeakerAlone()
    {
        var rig = new Rig();
        var first = Rig.Player(11, "first");
        var second = Rig.Player(22, "second");
        await EnterAsync(rig, first);
        await EnterAsync(rig, second);

        // In the plaza: both hear it.
        await rig.Flow.HandleChatAsync(first, new RoomChatSent(0, "plaza"), CancellationToken.None);
        Assert.Equal(2, rig.Chat.Sent.Count(row => row.Message.Text == "plaza"));

        // The second creates a room; the room chat reaches the room's master alone.
        await rig.Flow.HandleCreateAsync(second, new RoomCreateRequested("room", "", 4, "rel_Village07_Easy02"),
            CancellationToken.None);
        await rig.Flow.HandleChatAsync(second, new RoomChatSent(0, "inroom"), CancellationToken.None);
        Assert.Single(rig.Chat.Sent, row => row.Message.Text == "inroom");
        Assert.Equal(second.Id, rig.Chat.Sent.Last(row => row.Message.Text == "inroom").Session);

        // The first left the plaza back to nobody: from the main menu, a line is echoed to the speaker alone.
        await rig.Flow.HandlePlazaMenuReturnAsync(first, new PlazaMenuReturnRequested(), CancellationToken.None);
        await rig.Flow.HandleChatAsync(first, new RoomChatSent(0, "menu"), CancellationToken.None);
        var menu = rig.Chat.Sent.Last(row => row.Message.Text == "menu");
        Assert.Equal(first.Id, menu.Session);
        Assert.Equal(0, menu.Message.RoomId);
    }
}
