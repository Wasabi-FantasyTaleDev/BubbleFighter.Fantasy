using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Verifies pair ownership, atomic transitions, generation fences and bounded process-local retention.</summary>
public sealed class MemoryCoupleStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly CoupleAccount A = new("alpha", 1);
    private static readonly CoupleAccount B = new("alpha", 2);
    private static readonly CoupleAccount C = new("alpha", 3);

    private static MemoryCoupleStore Store(int maxPairs = 4, int maxPending = 8, int perAccount = 4, int completed = 4) =>
        new(maxPairs, maxPending, perAccount, completed, 128, 128);

    private static CoupleInvitationDraft Draft(CoupleAccount target, Guid? id = null, DateTimeOffset? created = null) =>
        new(id ?? Guid.NewGuid(), target, "Hello", created ?? Now, (created ?? Now).AddMinutes(1));

    /// <summary>Accepting publishes the same pair to both accounts and replaying cannot change its creation time.</summary>
    [Fact]
    public async Task AcceptedPairIsMutualAndRepeatedAnswerKeepsOriginalPair()
    {
        var store = Store();
        var draft = Draft(B);
        Assert.Equal(CoupleStoreResult.Pending, (await store.CreateInvitationAsync(A, draft, default)).Result);
        var accepted = await store.AnswerInvitationAsync(B, draft.Id, true, Now.AddSeconds(1), default);
        Assert.Equal(CoupleStoreResult.Accepted, accepted.Result);
        Assert.Same(accepted.Relationship, await store.FindRelationshipAsync(A, default));
        Assert.Same(accepted.Relationship, await store.FindRelationshipAsync(B, default));
        Assert.Equal(accepted, await store.AnswerInvitationAsync(B, draft.Id, true, Now.AddSeconds(2), default));
        Assert.Empty(await store.ListIncomingAsync(B, Now, default));
        Assert.False(store.PersistsState);
    }

    /// <summary>An invitation's recipient and sender independently own answer and cancellation, including after completion.</summary>
    [Fact]
    public async Task ForgedAnswersAndCancellationsCannotChangeEitherAccount()
    {
        var store = Store();
        var draft = Draft(B);
        await store.CreateInvitationAsync(A, draft, default);
        foreach (var actor in new[] { A, C, new CoupleAccount("beta", B.UserNum) })
        {
            var outcome = await store.AnswerInvitationAsync(actor, draft.Id, true, Now, default);
            Assert.Equal(new CoupleStoreOutcome(CoupleStoreResult.Forbidden), outcome);
        }
        Assert.Equal(CoupleStoreResult.Forbidden, (await store.CancelInvitationAsync(B, draft.Id, Now, default)).Result);
        Assert.Null(await store.FindRelationshipAsync(A, default));
        Assert.Single(await store.ListIncomingAsync(B, Now, default));
        await store.AnswerInvitationAsync(B, draft.Id, false, Now, default);
        Assert.Equal(CoupleStoreResult.Forbidden, (await store.AnswerInvitationAsync(C, draft.Id, true, Now, default)).Result);
    }

    /// <summary>Self-invitations and same-number identities from another authority cannot create relations.</summary>
    [Fact]
    public async Task DataAuthoritiesAreIsolated()
    {
        var store = Store();
        Assert.Equal(CoupleStoreResult.Forbidden, (await store.CreateInvitationAsync(A, Draft(A), default)).Result);
        Assert.Equal(CoupleStoreResult.Forbidden,
            (await store.CreateInvitationAsync(A, Draft(new("beta", 2)), default)).Result);
        var draft = Draft(B);
        await store.CreateInvitationAsync(A, draft, default);
        await store.AnswerInvitationAsync(B, draft.Id, true, Now, default);
        Assert.Null(await store.FindRelationshipAsync(new("beta", 1), default));
    }

    /// <summary>Two concurrent acceptances involving the same account commit at most one mutually consistent pair.</summary>
    [Fact]
    public async Task CompetingAcceptancesCannotPairAnAccountTwice()
    {
        var store = Store();
        var ab = Draft(B);
        var ac = Draft(C);
        await store.CreateInvitationAsync(A, ab, default);
        await store.CreateInvitationAsync(A, ac, default);
        var results = await Task.WhenAll(
            Task.Run(async () => await store.AnswerInvitationAsync(B, ab.Id, true, Now, default)),
            Task.Run(async () => await store.AnswerInvitationAsync(C, ac.Id, true, Now, default)));
        var winner = Assert.Single(results, result => result.Result == CoupleStoreResult.Accepted);
        Assert.Single(results, result => result.Result == CoupleStoreResult.AlreadyPaired);
        Assert.Equal(winner.Relationship, await store.FindRelationshipAsync(A, default));
        Assert.Equal(winner.Relationship, await store.FindRelationshipAsync(winner.Relationship!.Second, default));
        Assert.Null(await store.FindRelationshipAsync(winner.Relationship.Second == B ? C : B, default));
    }

    /// <summary>Cancellation and acceptance race on the same generation and cannot both succeed.</summary>
    [Fact]
    public async Task CancellationRacingAcceptanceHasOneOutcome()
    {
        var store = Store();
        var draft = Draft(B);
        await store.CreateInvitationAsync(A, draft, default);
        var outcomes = await Task.WhenAll(
            Task.Run(async () => await store.CancelInvitationAsync(A, draft.Id, Now, default)),
            Task.Run(async () => await store.AnswerInvitationAsync(B, draft.Id, true, Now, default)));
        Assert.Equal(outcomes[0].Result, outcomes[1].Result);
        var pair = await store.FindRelationshipAsync(A, default);
        Assert.Equal(outcomes[0].Result == CoupleStoreResult.Accepted, pair is not null);
        Assert.Equal(pair, await store.FindRelationshipAsync(B, default));
    }

    /// <summary>Expiry is exclusive and an old generation never accepts or cancels a later pending invitation.</summary>
    [Fact]
    public async Task ExpiredAndCancelledGenerationsCannotAffectLaterInvitations()
    {
        var store = Store();
        var first = Draft(B);
        await store.CreateInvitationAsync(A, first, default);
        Assert.Equal(CoupleStoreResult.Expired,
            (await store.AnswerInvitationAsync(B, first.Id, true, first.ExpiresAt, default)).Result);
        var second = Draft(B, created: first.ExpiresAt);
        await store.CreateInvitationAsync(A, second, default);
        Assert.Equal(CoupleStoreResult.Expired,
            (await store.CancelInvitationAsync(A, first.Id, second.CreatedAt, default)).Result);
        Assert.Equal(second.Id, Assert.Single(await store.ListIncomingAsync(B, second.CreatedAt, default)).Id);
        await store.CancelInvitationAsync(A, second.Id, second.CreatedAt, default);
        Assert.Equal(CoupleStoreResult.Cancelled,
            (await store.AnswerInvitationAsync(B, second.Id, true, second.CreatedAt, default)).Result);
    }

    /// <summary>Breakup checks the exact relationship and removes both accounts before a new pair can form.</summary>
    [Fact]
    public async Task StaleBreakupCannotRemoveAReplacementPair()
    {
        var store = Store();
        var first = Draft(B);
        await store.CreateInvitationAsync(A, first, default);
        await store.AnswerInvitationAsync(B, first.Id, true, Now, default);
        Assert.Equal(CoupleStoreResult.NotFound, (await store.BreakUpAsync(C, first.Id, default)).Result);
        Assert.Equal(CoupleStoreResult.BrokenUp, (await store.BreakUpAsync(B, first.Id, default)).Result);
        Assert.Null(await store.FindRelationshipAsync(A, default));
        Assert.Null(await store.FindRelationshipAsync(B, default));
        Assert.Equal(CoupleStoreResult.BrokenUp,
            (await store.AnswerInvitationAsync(B, first.Id, true, Now, default)).Result);
        var second = Draft(C);
        await store.CreateInvitationAsync(A, second, default);
        var current = await store.AnswerInvitationAsync(C, second.Id, true, Now, default);
        Assert.Equal(CoupleStoreResult.Conflict, (await store.BreakUpAsync(A, first.Id, default)).Result);
        Assert.Equal(current.Relationship, await store.FindRelationshipAsync(A, default));
    }

    /// <summary>Pending, per-account and pair bounds reject whole operations, and completed receipt eviction cannot resurrect pairs.</summary>
    [Fact]
    public async Task CapacityAndReceiptEvictionPreserveActiveRelationships()
    {
        var store = Store(maxPairs: 1, maxPending: 2, perAccount: 1, completed: 1);
        var ab = Draft(B);
        await store.CreateInvitationAsync(A, ab, default);
        Assert.Equal(CoupleStoreResult.CapacityReached, (await store.CreateInvitationAsync(A, Draft(C), default)).Result);
        var accepted = await store.AnswerInvitationAsync(B, ab.Id, true, Now, default);
        var d = new CoupleAccount("alpha", 4);
        var cd = Draft(d);
        await store.CreateInvitationAsync(C, cd, default);
        Assert.Equal(CoupleStoreResult.CapacityReached,
            (await store.AnswerInvitationAsync(d, cd.Id, true, Now, default)).Result);
        await store.AnswerInvitationAsync(d, cd.Id, false, Now, default);
        Assert.Equal(CoupleStoreResult.NotFound, (await store.AnswerInvitationAsync(B, ab.Id, true, Now, default)).Result);
        Assert.Equal(CoupleStoreResult.Conflict,
            (await store.CreateInvitationAsync(C, Draft(d, ab.Id), default)).Result);
        Assert.Equal(accepted.Relationship, await store.FindRelationshipAsync(A, default));
        Assert.Equal(CoupleStoreResult.BrokenUp, (await store.BreakUpAsync(A, ab.Id, default)).Result);
        Assert.Null(await store.FindRelationshipAsync(B, default));
    }

    /// <summary>Generation reuse with changed contents and cancelled calls produce no partial mutation.</summary>
    [Fact]
    public async Task InvalidReplaysAndCancelledCallsLeavePendingStateIntact()
    {
        var store = Store();
        var draft = Draft(B);
        var pending = await store.CreateInvitationAsync(A, draft, default);
        Assert.Equal(pending, await store.CreateInvitationAsync(A, draft, default));
        Assert.Equal(CoupleStoreResult.Conflict,
            (await store.CreateInvitationAsync(A, draft with { Recipient = C }, default)).Result);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.AnswerInvitationAsync(B, draft.Id, true, Now, cancellation.Token));
        Assert.Null(await store.FindRelationshipAsync(A, default));
        Assert.Single(await store.ListIncomingAsync(B, Now, default));
    }

    /// <summary>Future invitations are invisible and cannot be answered or cancelled before their creation time.</summary>
    [Fact]
    public async Task InvitationsAreVisibleOnlyDuringTheirAuthoritativeInterval()
    {
        var store = Store();
        var draft = Draft(B, created: Now.AddMinutes(1));
        await store.CreateInvitationAsync(A, draft, default);
        Assert.Empty(await store.ListIncomingAsync(B, Now, default));
        Assert.Equal(CoupleStoreResult.Conflict,
            (await store.AnswerInvitationAsync(B, draft.Id, true, Now, default)).Result);
        Assert.Equal(CoupleStoreResult.Conflict,
            (await store.CancelInvitationAsync(A, draft.Id, Now, default)).Result);
        Assert.Single(await store.ListIncomingAsync(B, draft.CreatedAt, default));
        Assert.Empty(await store.ListIncomingAsync(B, draft.ExpiresAt, default));
    }

    /// <summary>Invalid identities, generations, text and intervals fail before retaining an invitation.</summary>
    [Fact]
    public async Task InvalidInputsCannotAllocatePendingState()
    {
        var store = Store();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CreateInvitationAsync(new(new string('x', 129), 1), Draft(B), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.CreateInvitationAsync(new("alpha", 0), Draft(B), default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CreateInvitationAsync(A, Draft(B, Guid.Empty), default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CreateInvitationAsync(A, Draft(B) with { Message = new string('x', 129) }, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CreateInvitationAsync(A, Draft(B) with { ExpiresAt = Now }, default));
        Assert.Empty(await store.ListIncomingAsync(B, Now, default));
    }
}
