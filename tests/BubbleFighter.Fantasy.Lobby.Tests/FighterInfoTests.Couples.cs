using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>The couple flow: proposal checks, the invitation, acceptance, the breakup wait and the ring change.</summary>
public sealed partial class FighterInfoTests
{
    private const int Ring = 750001;

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Sent<T> : IMessageSender<T> where T : IMessage
    {
        public bool FailNext { get; set; }
        public List<(ISession To, T Message)> Messages { get; } = [];
        public string Protocol => "kr-1256/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Injected couple publication failure.");
            }
            Messages.Add((session, message));
            return ValueTask.CompletedTask;
        }
        public T[] To(ISession session) => Messages.Where(m => ReferenceEquals(m.To, session)).Select(m => m.Message).ToArray();
    }

    private sealed record CoupleRig(CoupleFlow Flow, FeatureSession Ann, FeatureSession Bob, long AnnNum, long BobNum,
        Sent<CoupleProposalDelivered> Delivered, Sent<CoupleProposalReplied> Replied, Sent<CoupleAnswerPublished> Answered,
        Sent<CoupleBreakUpReplied> BrokeUp, Sent<CouplePartnerPublished> Partner, Sent<CoupleRingChangeReplied> RingSent,
        TestClock Clock, PlayerSessionScope Online)
    {
        internal Sent<CoupleProfilePublished> Profile { get; } = new();
        internal Sent<CoupleInviterStatusPublished> InviterStatus { get; } = new();
        internal Sent<CoupleConfirmationReady> Confirmation { get; } = new();
    }

    private static async Task<CoupleRig> CouplesAsync(bool annHasRing = true, Func<bool>? ownsRing = null,
        Func<long, IReadOnlyList<InventorySlotRecord>>? read = null, Func<long, IInventoryLoadoutStore>? loadoutFactory = null,
        IInventoryBuildScope? buildScope = null, IEventBus? events = null, bool hasCatalog = true, bool ringChange = true)
    {
        var players = new InMemoryPlayerRepository();
        var ann = await players.CreateAsync("ann", "Ann", default);
        var bob = await players.CreateAsync("bob", "Bob", default);
        var online = new PlayerSessionScope();
        var annSession = new FeatureSession(1);
        var bobSession = new FeatureSession(2);
        online.Attach(annSession, new PlayerIdentity(checked((int)ann.UserNum), "ann", "Ann", 5)).MoveTo(SessionPhase.MainMenu);
        online.Attach(bobSession, new PlayerIdentity(checked((int)bob.UserNum), "bob", "Bob", 5)).MoveTo(SessionPhase.MainMenu);
        var inventory = new Inventory
        {
            Read = user => ValueTask.FromResult(read?.Invoke(user) ?? (user == ann.UserNum && annHasRing && (ownsRing?.Invoke() ?? true)
                ? [Row(user, 300007), Row(user, Ring, 0)] : new InventorySlotRecord[] { Row(user, 300007) })),
        };
        var clock = new TestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var store = new MemoryCoupleStore(64, 64, 4, 64, 512, 64);
        var rig = new CoupleRig(null!, annSession, bobSession, ann.UserNum, bob.UserNum, new(), new(), new(), new(), new(), new(),
            clock, online);
        var flow = new CoupleFlow(store, online, inventory, players, new(rig.Delivered, rig.Replied, rig.Answered, rig.BrokeUp,
            rig.Partner, ringChange ? rig.RingSent : null, rig.Profile, rig.InviterStatus, rig.Confirmation),
            new LobbyOptions(), clock, hasCatalog ? new TestCoupleRingCatalog() : null,
            loadoutFactory?.Invoke(ann.UserNum), buildScope, events);
        return rig with { Flow = flow };
    }

    private sealed class TestCoupleRingCatalog : ICoupleRingCatalog
    {
        /// <summary>Recognizes only the fixture invitation and maps it to a distinct display identity.</summary>
        public bool TryGetDisplayRing(int invitationItemId, out int displayRingItemId)
        {
            displayRingItemId = invitationItemId == Ring ? Ring + 20 : 0;
            return displayRingItemId != 0;
        }
    }

    /// <summary>Ownership alone cannot turn a character, a display ring, or another item into a proposal ring.</summary>
    [Fact]
    public async Task ProposalsAndChangesRequireNativeInvitationClassification()
    {
        var c = await CouplesAsync(read: user => [Row(user, 300007), Row(user, Ring, 0), Row(user, Ring + 20, 0)]);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", 300007, ""), default);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring + 20, ""), default);
        Assert.All(c.Replied.To(c.Ann), reply => Assert.Equal(CoupleProposalResult.NoRing, reply.Result));
        Assert.Empty(c.Delivered.Messages);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        await c.Flow.ChangeRingAsync(c.Ann, new(300007), default);
        Assert.False(Assert.Single(c.RingSent.To(c.Ann)).Changed);

        var noCatalog = await CouplesAsync(hasCatalog: false);
        await noCatalog.Flow.ProposeAsync(noCatalog.Ann, new(true, "Bob", Ring, ""), default);
        Assert.Equal(CoupleProposalResult.NoRing, Assert.Single(noCatalog.Replied.To(noCatalog.Ann)).Result);
    }

    /// <summary>A depleted, expired, or superseded inventory row cannot authorize an invitation.</summary>
    [Theory]
    [InlineData("depleted")]
    [InlineData("expired")]
    [InlineData("superseded")]
    public async Task ProposalOwnershipUsesTheLiveLastStoredRow(string state)
    {
        var c = await CouplesAsync(read: user =>
        {
            var ring = Row(user, Ring, 0);
            return state switch
            {
                "depleted" => [Row(user, 300007), ring with { Quantity = 0 }],
                "expired" => [Row(user, 300007), ring with { Lifetime = InventoryLifetime.Until(DateTimeOffset.UnixEpoch) }],
                _ => [Row(user, 300007), ring, ring with { Quantity = 0 }],
            };
        });
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        Assert.Equal(CoupleProposalResult.NoRing, Assert.Single(c.Replied.To(c.Ann)).Result);
        Assert.Empty(c.Delivered.Messages);
    }

    /// <summary>Foreign ownership, another bag, and unbounded snapshots fail before any invitation is sent.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("bag")]
    [InlineData("limit")]
    public async Task ProposalRejectsInvalidInventorySnapshots(string invalid)
    {
        var c = await CouplesAsync(read: user => invalid switch
        {
            "owner" => [Row(user + 999, 300007), Row(user, Ring, 0)],
            "bag" => [Row(user, 300007), Row(user, Ring, 0) with { Bag = InventoryBag.EquippedBag }],
            _ => Enumerable.Repeat(Row(user, Ring, 0), InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(),
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default).AsTask());
        Assert.Empty(c.Delivered.Messages);
    }

    /// <summary>Invitation appearance reads this build's selection once and retains its native item modifiers without repairing state.</summary>
    [Fact]
    public async Task ProposalAppearanceUsesOneScopedReadWithItemAttributes()
    {
        var scope = new CoupleBuildScope(new("kr-1256", [300007, 301001, Ring]));
        var events = new EventBus(NullLogger.Instance);
        uint[] values = [1, 37, 0, 0];
        using var projection = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
        {
            Assert.Equal("kr-1256/game", request.Protocol);
            return ValueTask.FromResult(request.Slot.ItemId == 301001
                ? new InventoryItemPresentation([new(301001, 7, values, 0, "")], null) : InventoryItemPresentation.Empty);
        });
        CoupleReadOnlyLoadouts? loadouts = null;
        var c = await CouplesAsync(read: _ => throw new InvalidOperationException("A global appearance read is not allowed."),
            loadoutFactory: user => loadouts = new(user, scope.Scope,
                [Row(user, 300007), Row(user, 301001), Row(user, Ring, 0), Row(user, 300029)]), buildScope: scope, events: events);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        values[1] = 999;
        Assert.Equal(1, loadouts!.Reads);
        var appearance = Assert.Single(c.Delivered.To(c.Bob)).Appearance;
        Assert.Equal(new[] { 300007, 301001 }, appearance.EquippedItemIds);
        Assert.Equal(new uint[] { 1, 37, 0, 0 }, Assert.Single(appearance.ItemAttributes).Values);
    }

    private sealed record CoupleBuildScope(InventoryLoadoutScope Scope) : IInventoryBuildScope;

    private sealed class CoupleReadOnlyLoadouts(long expectedUser, InventoryLoadoutScope expectedScope,
        InventorySlotRecord[] rows) : IInventoryLoadoutStore
    {
        internal int Reads { get; private set; }
        /// <summary>Returns the expected build's selected rows without initializing or normalizing a loadout.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
            InventoryBag bag, int maxCount, CancellationToken ct)
        {
            Assert.Equal(expectedUser, userNum);
            Assert.Same(expectedScope, scope);
            Assert.Equal(InventoryBag.CharacterBag, bag);
            Assert.Equal(InventoryLimits.MaxInventorySlotsPerBag, maxCount);
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(rows.Where(row => scope.Supports(row.ItemId)).ToArray());
        }
        /// <summary>Rejects an unscoped ownership read.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct) => throw new InvalidOperationException();
        /// <summary>Rejects initialization during invitation publication.</summary>
        public ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) => throw new InvalidOperationException();
        /// <summary>Rejects equipment normalization during invitation publication.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum, InventoryLoadoutScope scope,
            IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) => throw new InvalidOperationException();
        /// <summary>Rejects preset mutation during invitation publication.</summary>
        public ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
            byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct) => throw new InvalidOperationException();
        /// <summary>Rejects inventory replacement during invitation publication.</summary>
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) => throw new InvalidOperationException();
        /// <summary>Rejects equipment changes during invitation publication.</summary>
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) => throw new InvalidOperationException();
    }

    /// <summary>A proposal reaches the recipient with the proposer's record; accepting pairs both and names the partner.</summary>
    [Fact]
    public async Task AnAcceptedProposalPairsBothFighters()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, "be mine"), default);
        Assert.Equal(new CoupleProposalReplied(CoupleProposalResult.Sent, "Bob"), Assert.Single(c.Replied.To(c.Ann)));
        var invitation = Assert.Single(c.Delivered.To(c.Bob));
        Assert.Equal(("Ann", "Bob", "be mine", 300007), (invitation.Inviter.Nickname, invitation.Recipient, invitation.Message,
            invitation.Appearance.CharacterItemId));
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.Equal(new CoupleAnswerPublished(true, "Ann", "Bob"), Assert.Single(c.Answered.To(c.Ann)));
        Assert.Equal(new CoupleAnswerPublished(true, "Ann", "Bob"), Assert.Single(c.Answered.To(c.Bob)));
        Assert.Equal(new CouplePartnerPublished(true, c.BobNum, "Bob"), Assert.Single(c.Partner.To(c.Ann)));
        Assert.Equal(new CouplePartnerPublished(true, c.AnnNum, "Ann"), Assert.Single(c.Partner.To(c.Bob)));
        // A paired fighter cannot propose again; the restore on login names the partner.
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        Assert.Equal(CoupleProposalResult.SenderPaired, c.Replied.To(c.Ann)[^1].Result);
        await c.Flow.RestoreAsync(c.Bob, c.BobNum, default);
        Assert.Equal(new CouplePartnerPublished(true, c.AnnNum, "Ann"), c.Partner.To(c.Bob)[^1]);
    }

    /// <summary>Proposals are refused for oneself, an unknown fighter, a missing ring, bad text and a pending pair.</summary>
    [Fact]
    public async Task ProposalsAreRefusedWithTheirResult()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Ann", Ring, ""), default);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Nobody", Ring, ""), default);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring + 1, ""), default);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, "a\u0001b"), default);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        Assert.Equal(new[]
        {
            CoupleProposalResult.Self, CoupleProposalResult.NotFound, CoupleProposalResult.NoRing, CoupleProposalResult.BadMessage,
            CoupleProposalResult.Sent, CoupleProposalResult.Busy,
        }, c.Replied.To(c.Ann).Select(reply => reply.Result));
        // An expired proposal no longer blocks a new one.
        c.Clock.Advance(TimeSpan.FromSeconds(181));
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        Assert.Equal(CoupleProposalResult.Sent, c.Replied.To(c.Ann)[^1].Result);
    }

    /// <summary>A decline tells both; a withdrawn proposal can no longer be answered.</summary>
    [Fact]
    public async Task DeclinedAndWithdrawnProposalsFormNoCouple()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(false, "Ann", "Bob"), default);
        Assert.Equal(new CoupleAnswerPublished(false, "Ann", "Bob"), Assert.Single(c.Answered.To(c.Ann)));
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.ProposeAsync(c.Ann, new(false, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.Single(c.Answered.To(c.Bob));
        Assert.Empty(c.Partner.Messages);
    }

    /// <summary>A breakup within the wait is refused; after it both are told and both lose the partner.</summary>
    [Fact]
    public async Task BreakUpWaitsADayThenTellsBoth()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        await c.Flow.BreakUpAsync(c.Ann, new("Bob"), default);
        Assert.Equal(CoupleBreakUpStatus.Refused, Assert.Single(c.BrokeUp.To(c.Ann)).Status);
        c.Clock.Advance(TimeSpan.FromHours(24));
        await c.Flow.BreakUpAsync(c.Ann, new("Someone"), default);
        Assert.Equal(CoupleBreakUpStatus.Refused, c.BrokeUp.To(c.Ann)[^1].Status);
        await c.Flow.BreakUpAsync(c.Ann, new("Bob"), default);
        Assert.Equal(new CoupleBreakUpReplied(CoupleBreakUpStatus.Done, "Ann", "Bob"), c.BrokeUp.To(c.Ann)[^1]);
        Assert.Equal(new CoupleBreakUpReplied(CoupleBreakUpStatus.Left, "Ann", "Bob"), Assert.Single(c.BrokeUp.To(c.Bob)));
        Assert.Equal(new CouplePartnerPublished(false, 0, ""), c.Partner.To(c.Ann)[^1]);
        Assert.Equal(new CouplePartnerPublished(false, 0, ""), c.Partner.To(c.Bob)[^1]);
    }

    /// <summary>A ring change needs a partner and an owned ring; both partners see the new ring.</summary>
    [Fact]
    public async Task RingChangesNeedAnOwnedRingAndReachBoth()
    {
        var c = await CouplesAsync();
        await c.Flow.ChangeRingAsync(c.Ann, new(Ring), default);
        Assert.Equal(new CoupleRingChangeReplied(false, 0), Assert.Single(c.RingSent.To(c.Ann)));
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        await c.Flow.ChangeRingAsync(c.Bob, new(Ring), default);
        Assert.Equal(new CoupleRingChangeReplied(false, 0), Assert.Single(c.RingSent.To(c.Bob)));
        await c.Flow.ChangeRingAsync(c.Ann, new(Ring), default);
        Assert.Equal(new CoupleRingChangeReplied(true, Ring), c.RingSent.To(c.Ann)[^1]);
        Assert.Equal(new CoupleRingChangeReplied(true, Ring), c.RingSent.To(c.Bob)[^1]);
    }

    /// <summary>A stale dialog with different names cannot answer the current invitation.</summary>
    [Fact]
    public async Task AnAnswerMustNameTheCurrentInvitation()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Someone", "Bob"), default);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Someone"), default);
        Assert.Empty(c.Answered.Messages);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.True(Assert.Single(c.Answered.To(c.Bob)).Accepted);
    }

    /// <summary>Reconnecting the recipient does not transfer the old connection's pending consent.</summary>
    [Fact]
    public async Task AReplacementSessionCannotAnswerTheOldDialog()
    {
        var c = await CouplesAsync();
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        c.Online.Remove(c.Bob.Connection.Id);
        var replacement = new FeatureSession(3);
        c.Online.Attach(replacement, new(checked((int)c.BobNum), "bob", "Bob", 5)).MoveTo(SessionPhase.MainMenu);
        await c.Flow.AnswerAsync(replacement, new(true, "Ann", "Bob"), default);
        Assert.Empty(c.Partner.Messages);
        Assert.Empty(c.Answered.Messages);
    }

    /// <summary>Consent is rejected if the proposer leaves, starts playing, or no longer owns the ring.</summary>
    [Theory]
    [InlineData("offline")]
    [InlineData("playing")]
    [InlineData("ring")]
    [InlineData("scene")]
    public async Task AcceptanceRechecksTheInvitationsLiveConditions(string changed)
    {
        var owned = true;
        var c = await CouplesAsync(ownsRing: () => owned);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        if (changed == "offline") c.Online.Remove(c.Ann.Connection.Id);
        if (changed == "playing" && c.Ann.TryFeature<IPlayerSession>(out var playing))
        {
            playing.MoveTo(SessionPhase.Room);
            playing.MoveTo(SessionPhase.Match);
        }
        if (changed == "ring") owned = false;
        if (changed == "scene") c.Flow.SceneChanged(c.Ann);
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.False(Assert.Single(c.Answered.To(c.Bob)).Accepted);
        Assert.Empty(c.Partner.Messages);
    }

    /// <summary>Repeated scene changes release the bounded store invitations before admitting a replacement.</summary>
    [Fact]
    public async Task SceneChangesDoNotExhaustThePendingInvitationBudget()
    {
        var c = await CouplesAsync();
        for (var i = 0; i < 20; i++)
        {
            await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
            Assert.Equal(CoupleProposalResult.Sent, c.Replied.To(c.Ann)[^1].Result);
            c.Flow.SceneChanged(c.Ann);
        }
    }

    /// <summary>A failed invitation publication leaves neither fighter busy and cannot be accepted later.</summary>
    [Fact]
    public async Task DeliveryFailureAllowsANewProposal()
    {
        var c = await CouplesAsync();
        c.Delivered.FailNext = true;
        await Assert.ThrowsAsync<IOException>(async () => await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default));
        await c.Flow.AnswerAsync(c.Bob, new(true, "Ann", "Bob"), default);
        Assert.Empty(c.Partner.Messages);
        await c.Flow.ProposeAsync(c.Ann, new(true, "Bob", Ring, ""), default);
        Assert.Equal(CoupleProposalResult.Sent, Assert.Single(c.Replied.To(c.Ann)).Result);
    }
}
