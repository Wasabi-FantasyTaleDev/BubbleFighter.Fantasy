using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class CoupleSceneFlowTests
{
    private static ISession Player(long id, string name, CoupleSceneState? couple = null)
    {
        var session = RoomFlowTests.Rig.Player(id, name);
        session.RegisterFeature<ICoupleProfile>(new MutableProfile { Scene = couple });
        return session;
    }

    private static MutableProfile Profile(ISession session) => (MutableProfile)session.Feature<ICoupleProfile>();

    private sealed class MutableProfile : ICoupleProfile
    {
        public CoupleProfileState? Current => null;
        public CoupleSceneState? Scene { get; set; }
    }

    private sealed class Probe<T> : IMessageSender<T> where T : IMessage
    {
        private readonly List<(ISession Session, T Message)> _sent = [];
        internal long? FailSession { get; set; }
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (FailSession == session.Id) throw new IOException("Simulated incomplete scene installation.");
            lock (_sent) _sent.Add((session, message));
            return ValueTask.CompletedTask;
        }
        internal T[] To(ISession session)
        {
            lock (_sent) return [.. _sent.Where(entry => ReferenceEquals(entry.Session, session)).Select(entry => entry.Message)];
        }
        internal (ISession Session, T Message)[] All()
        {
            lock (_sent) return [.. _sent];
        }
        internal void Clear()
        {
            lock (_sent) _sent.Clear();
        }
    }

    private sealed class Link : IPlazaFederationLink
    {
        internal List<PlazaFederationEvent> Changes { get; } = [];
        public void Publish(PlazaFederationEvent change) => Changes.Add(change);
    }

    private sealed class OnlineDirectory : IOnlinePlayers
    {
        internal Dictionary<int, ISession> Sessions { get; } = [];
        public ISession? FindByUserNum(int userNum) => Sessions.GetValueOrDefault(userNum);
        public ISession? FindByNickname(string nickname) => null;
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal RoomRegistry Registry { get; }
        internal RoomFlow Flow { get; }
        internal Probe<RoomEntered> Entered { get; } = new();
        internal Probe<RoomSeatUpdated> SeatUpdated { get; } = new();
        internal Probe<RoomCoupleUpdated> RoomCouple { get; } = new();
        internal Probe<PlazaCoupleUpdated> PlazaCouple { get; } = new();
        internal Probe<PlazaEntered> PlazaEntered { get; } = new();
        internal Probe<PlazaMemberEntered> MemberEntered { get; } = new();
        internal Probe<PlazaPositionReceived> Position { get; } = new();

        internal Rig(bool couples = true, IOnlinePlayers? online = null, int queueCapacity = 64)
        {
            Registry = new(new InGameOptions { MaxPlayersPerMatch = 16, RoomQueueCapacity = queueCapacity });
            Flow = new(Registry, new RoomSenders(Entered, SeatUpdated, new Probe<RoomSeatVacated>(), new Probe<RoomMasterChanged>(),
                Left: new Probe<RoomLeft>(), PlazaEntered: PlazaEntered, PlazaMemberEntered: MemberEntered,
                PlazaMemberLeft: new Probe<PlazaMemberLeft>(), PlazaPositionReceived: Position,
                MenuReturn: new Probe<PlazaMenuReturnRequested>(), MainMenu: new Probe<MainMenuRoomOpened>(),
                RoomCouple: couples ? RoomCouple : null, PlazaCouple: couples ? PlazaCouple : null), NullLogger.Instance, online: online);
        }

        internal async Task<int> CreateAsync(ISession session)
        {
            await Flow.HandleCreateAsync(session, new("room", "", 4, "rel_Village07_Easy02"), default);
            return Entered.To(session)[^1].Room.RoomId;
        }

        internal async Task ChangeAsync(ISession session, CoupleSceneState? couple)
        {
            Profile(session).Scene = couple;
            Flow.OnCoupleSceneChanged(new(session, couple));
            await Flow.FlushCoupleChangesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            await Flow.StopScenesAsync();
            await Registry.StopAllAsync();
        }
    }
}
