using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    /// <summary>A profile lookup begun before breakup cannot finish after its newer refresh and resurrect coupling.</summary>
    [Fact]
    public async Task SlowProfileRefreshCannotOverwriteACommittedBreakup()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var repository = new InMemoryPlayerRepository();
        var ann = await repository.CreateAsync("ann", "Ann", ct);
        var bob = await repository.CreateAsync("bob", "Bob", ct);
        var players = new PausedCouplePartnerLookup(repository, bob.UserNum);
        var online = new PlayerSessionScope();
        var session = new FeatureSession(21);
        online.Attach(session, new(checked((int)ann.UserNum), "ann", "Ann", 1)).MoveTo(SessionPhase.MainMenu);
        var store = new MemoryCoupleStore(64, 64, 4, 64, 512, 64);
        var clock = new TestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var first = new CoupleAccount("players", ann.UserNum);
        var second = new CoupleAccount("players", bob.UserNum);
        var invitation = new CoupleInvitationDraft(Guid.NewGuid(), second, "", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(3))
            { RingItemId = Ring };
        await store.CreateInvitationAsync(first, invitation, ct);
        await store.AnswerInvitationAsync(second, invitation.Id, true, clock.GetUtcNow(), ct);
        var profiles = new Sent<CoupleProfilePublished>();
        using var flow = new CoupleFlow(store, online, new Inventory(), players,
            new(new Sent<CoupleProposalDelivered>(), new Sent<CoupleProposalReplied>(), new Sent<CoupleAnswerPublished>(),
                new Sent<CoupleBreakUpReplied>(), new Sent<CouplePartnerPublished>(), new Sent<CoupleRingChangeReplied>(), profiles),
            new LobbyOptions(), clock, new TestCoupleRingCatalog());

        var oldRefresh = flow.RestoreAsync(session, ann.UserNum, ct).AsTask();
        await players.Started.Task.WaitAsync(ct);
        Task newerRefresh;
        try
        {
            Assert.Equal(CoupleStoreResult.BrokenUp, (await store.BreakUpAsync(first, invitation.Id, ct)).Result);
            // Memory stores and senders complete synchronously: without refresh serialization this newer clear
            // finishes while the first lookup is paused, allowing the older snapshot to win after release.
            newerRefresh = flow.RestoreAsync(session, ann.UserNum, ct).AsTask();
        }
        finally { players.Release.TrySetResult(true); }
        await Task.WhenAll(oldRefresh, newerRefresh).WaitAsync(ct);

        Assert.Null(session.Feature<ICoupleProfile>().Current);
        Assert.Null(profiles.To(session)[^1].Profile);
        Assert.Null(await store.FindRelationshipAsync(first, ct));
    }

    private sealed class PausedCouplePartnerLookup(IPlayerRepository inner, long partner) : IPlayerRepository
    {
        private int _armed = 1;
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PlayerRecord?> FindByUserNumAsync(long userNum, CancellationToken ct)
        {
            var found = await inner.FindByUserNumAsync(userNum, ct).ConfigureAwait(false);
            if (userNum == partner && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Started.TrySetResult(true);
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return found;
        }

        public ValueTask<PlayerRecord?> FindByUsernameAsync(string username, CancellationToken ct) => inner.FindByUsernameAsync(username, ct);
        public ValueTask<bool> HasCreatedCharacterAsync(long userNum, CancellationToken ct) => inner.HasCreatedCharacterAsync(userNum, ct);
        public ValueTask<PlayerRecord> CreateAsync(string username, string nickname, CancellationToken ct) => inner.CreateAsync(username, nickname, ct);
        public ValueTask<bool> NicknameExistsAsync(string nickname, CancellationToken ct, string? exceptUsername = null) =>
            inner.NicknameExistsAsync(nickname, ct, exceptUsername);
        public ValueTask<PlayerRecord?> CreateCharacterAsync(string username, string nickname, StarterKit kit, CancellationToken ct) =>
            inner.CreateCharacterAsync(username, nickname, kit, ct);
        public ValueTask<bool> NeedsTutorialAsync(long userNum, CancellationToken ct) => inner.NeedsTutorialAsync(userNum, ct);
        public ValueTask CompleteTutorialAsync(long userNum, CancellationToken ct) => inner.CompleteTutorialAsync(userNum, ct);
    }
}
