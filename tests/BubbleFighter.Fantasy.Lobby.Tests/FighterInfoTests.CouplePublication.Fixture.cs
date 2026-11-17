using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    private sealed class CouplePublicationRig : IDisposable
    {
        private readonly Dictionary<string, (FeatureSession Session, long User)> _players = [];
        private readonly List<(ISession Session, IMessage Message)> _sent = [];
        internal CoupleFlow Flow { get; private set; } = null!;
        internal MemoryCoupleStore Store { get; } = new(64, 64, 4, 64, 512, 64);
        internal PlayerSessionScope Online { get; } = new();
        internal TestClock Clock { get; } = new(new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        internal PublicationLogger Logger { get; } = new();
        internal Action<ISession, IMessage>? BeforeSend { get; set; }
        internal Action<ISession, IMessage>? AfterSend { get; set; }
        internal Func<ISession, IMessage, bool>? Fail { get; set; }
        internal Func<CoupleAccount, bool>? FailRelationshipRead { get; set; }
        internal FeatureSession this[string name] => _players[name].Session;
        internal long User(string name) => _players[name].User;
        internal CoupleAccount Account(string name) => new("players", User(name));

        internal static async Task<CouplePublicationRig> CreateAsync()
        {
            var result = new CouplePublicationRig();
            var players = new InMemoryPlayerRepository();
            foreach (var name in new[] { "Ann", "Bob", "Cat", "Dan" })
            {
                var record = await players.CreateAsync(name.ToLowerInvariant(), name, default);
                var session = new FeatureSession(result._players.Count + 1);
                result.Online.Attach(session, new(checked((int)record.UserNum), name.ToLowerInvariant(), name, 1)).MoveTo(SessionPhase.MainMenu);
                result._players[name] = (session, record.UserNum);
            }
            var inventory = new Inventory
            {
                Read = user => ValueTask.FromResult<IReadOnlyList<BubbleFighter.Fantasy.Contracts.Inventory.InventorySlotRecord>>(
                    [Row(user, 300007), Row(user, Ring, 0), Row(user, Ring + 1, 0)]),
            };
            result.Flow = new(new PublicationStore(result), result.Online, inventory, players,
                new(result.Sender<CoupleProposalDelivered>(), result.Sender<CoupleProposalReplied>(), result.Sender<CoupleAnswerPublished>(),
                    result.Sender<CoupleBreakUpReplied>(), result.Sender<CouplePartnerPublished>(), result.Sender<CoupleRingChangeReplied>(),
                    result.Sender<CoupleProfilePublished>()), new LobbyOptions(), result.Clock,
                new PublicationRings(), logger: result.Logger);
            return result;
        }

        internal async Task FormAsync(string first = "Ann", string second = "Bob")
        {
            await Flow.ProposeAsync(this[first], new(true, second, Ring, ""), default);
            await Flow.AnswerAsync(this[second], new(true, first, second), default);
        }

        internal T[] Messages<T>(string name) where T : IMessage
        {
            lock (_sent) return _sent.Where(item => ReferenceEquals(item.Session, this[name])).Select(item => item.Message).OfType<T>().ToArray();
        }

        internal Type[] Order(string name)
        {
            lock (_sent) return _sent.Where(item => ReferenceEquals(item.Session, this[name])).Select(item => item.Message.GetType()).ToArray();
        }

        internal void Clear() { lock (_sent) _sent.Clear(); }
        private IMessageSender<T> Sender<T>() where T : IMessage => new PublicationSender<T>(this);

        private sealed class PublicationStore(CouplePublicationRig owner) : ICoupleStore
        {
            public bool PersistsState => owner.Store.PersistsState;
            public ValueTask<CoupleRelationship?> FindRelationshipAsync(CoupleAccount member, CancellationToken ct)
            {
                if (owner.FailRelationshipRead?.Invoke(member) == true)
                    throw new IOException("Injected relationship read failure.");
                return owner.Store.FindRelationshipAsync(member, ct);
            }
            public ValueTask<IReadOnlyList<CoupleInvitation>> ListIncomingAsync(CoupleAccount recipient, DateTimeOffset now, CancellationToken ct) =>
                owner.Store.ListIncomingAsync(recipient, now, ct);
            public ValueTask<CoupleStoreOutcome> CreateInvitationAsync(CoupleAccount sender, CoupleInvitationDraft invitation, CancellationToken ct) =>
                owner.Store.CreateInvitationAsync(sender, invitation, ct);
            public ValueTask<CoupleStoreOutcome> AnswerInvitationAsync(CoupleAccount recipient, Guid invitationId, bool accept, DateTimeOffset now, CancellationToken ct) =>
                owner.Store.AnswerInvitationAsync(recipient, invitationId, accept, now, ct);
            public ValueTask<CoupleStoreOutcome> CancelInvitationAsync(CoupleAccount sender, Guid invitationId, DateTimeOffset now, CancellationToken ct) =>
                owner.Store.CancelInvitationAsync(sender, invitationId, now, ct);
            public ValueTask<CoupleStoreOutcome> BreakUpAsync(CoupleAccount member, Guid relationshipId, CancellationToken ct) =>
                owner.Store.BreakUpAsync(member, relationshipId, ct);
            public ValueTask<CoupleStoreOutcome> ChangeRingAsync(CoupleAccount member, Guid relationshipId, int ringItemId, CancellationToken ct) =>
                owner.Store.ChangeRingAsync(member, relationshipId, ringItemId, ct);
        }

        private sealed class PublicationSender<T>(CouplePublicationRig owner) : IMessageSender<T> where T : IMessage
        {
            public string Protocol => "kr-1256/game";
            public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
            {
                owner.BeforeSend?.Invoke(session, message);
                ct.ThrowIfCancellationRequested();
                if (owner.Fail?.Invoke(session, message) == true) throw new IOException("Injected participant publication failure.");
                lock (owner._sent) owner._sent.Add((session, message));
                owner.AfterSend?.Invoke(session, message);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class PublicationRings : ICoupleRingCatalog
        {
            public bool TryGetDisplayRing(int invitationItemId, out int displayRingItemId)
            {
                displayRingItemId = invitationItemId == Ring || invitationItemId == Ring + 1 ? invitationItemId + 20 : 0;
                return displayRingItemId != 0;
            }
        }

        internal sealed class PublicationLogger : ILogger
        {
            internal List<Exception?> Errors { get; } = [];
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error) lock (Errors) Errors.Add(exception);
            }
        }

        public void Dispose()
        {
            Flow.Dispose();
            Online.Dispose();
        }
    }
}
