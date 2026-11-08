using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>Completed equipment changes use the published items, retain installed weapon reports and preserve an active deadline.</summary>
    [Fact]
    public async Task CompletedEquipmentPublicationUpdatesOnlyItsOwningRoundSnapshot()
    {
        var authority = new CriticalAuthority { Duration = 60000 };
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var seat = rig.Room.Seats[0];
        var actor = RoomRegistry.SceneEntity(seat.Slot);
        await rig.Round.HandleFeedAsync(rig.Host, new(32800, new byte[] { 7 })
        {
            CriticalWeaponState = new(actor, 0, 3103), ReportedActors = [actor],
        }, default);
        await rig.Round.HandleFeedAsync(rig.Host, new(32795, new byte[] { 8 })
        {
            CriticalEffectActor = actor, ReportedActors = [actor],
        }, default);
        var deadline = seat.Critical!.ActiveUntil;
        var items = new[] { 300001, 202001, 201001, 100051 };
        var view = new LocalEquipmentViewUpdated(11, 300001, Array.AsReadOnly(items), 4, 202001, 0,
            Array.AsReadOnly(Enumerable.Repeat(new WeaponPresetPage(204099, 0, 0), WeaponPresetSlots.Count).ToArray()));
        var publication = new EquipmentViewPublished("cn-324/game", rig.Host, view, true);
        await rig.Round.OnEquipmentViewPublishedAsync(publication, default);
        var updated = authority.Sent[^1];
        Assert.Equal(seat.Relay!.Id, updated.Session);
        Assert.Equal(3, updated.Message.Revision);
        Assert.Equal(3103, updated.Message.EffectivePrimarySpecification);
        Assert.True(updated.Message.TimedEffectActive);
        Assert.Equal(deadline, seat.Critical.ActiveUntil);
        Assert.Equal(items.Order(), updated.Message.Equipment.WornItemIds);
        items[3] = 100099;
        Assert.Contains(100051, updated.Message.Equipment.WornItemIds);
        Assert.Equal(new[] { 201001, 202001, 300001 }, rig.Room.Seats[1].Critical!.Current.Equipment.WornItemIds);
        await rig.Round.OnEquipmentViewPublishedAsync(publication with
        {
            View = view with { EquippedItemIds = updated.Message.Equipment.WornItemIds, ActiveWeaponPresetSlot = 1 },
        }, default);
        Assert.Equal(3, authority.Sent.Count);
    }

    /// <summary>Another protocol, a replaced game session, a wrong player or a completed round cannot revise a running seat.</summary>
    [Theory]
    [InlineData("protocol")]
    [InlineData("session")]
    [InlineData("player")]
    [InlineData("ended")]
    public async Task UnownedEquipmentPublicationsCannotChangeCriticalState(string fault)
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var view = new LocalEquipmentViewUpdated(11, 300001, new[] { 300001, 202001, 201001, 100051 }, 0, 202001, 0);
        var publication = new EquipmentViewPublished("cn-324/game", rig.Host, view, true);
        publication = fault switch
        {
            "protocol" => publication with { Protocol = "cn-406/game" },
            "session" => publication with { Session = new Session(rig.Host.Id, "cn-324") },
            "player" => publication with { View = view with { UserNum = 22 } },
            _ => publication,
        };
        if (fault == "ended") { lock (rig.Room.Gate) rig.Room.MarkResult(); }
        await rig.Round.OnEquipmentViewPublishedAsync(publication, default);
        Assert.Empty(authority.Sent);
    }

    /// <summary>A completed refresh has the same native effect as a selected view and participates in live reconciliation.</summary>
    [Fact]
    public async Task CompletedRefreshUsesItsPublishedSnapshot()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var view = new LocalEquipmentViewUpdated(11, 300001, new[] { 300001, 202001, 201001, 100051 }, 0, 202001, 0);
        await rig.Round.OnEquipmentViewPublishedAsync(new("cn-324/game", rig.Host, view, false), default);
        Assert.Contains(100051, Assert.Single(authority.Sent).Message.Equipment.WornItemIds);
    }

    /// <summary>A failed equipment revision retains its failure and cannot be concealed by another completed view.</summary>
    [Fact]
    public async Task EquipmentPublicationFailureClosesLaterCriticalRevisions()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var view = new LocalEquipmentViewUpdated(11, 300001, new[] { 300001, 202001, 201001, 100051 }, 0, 202001, 0);
        var publication = new EquipmentViewPublished("cn-324/game", rig.Host, view, true);
        authority.Fail = true;
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            rig.Round.OnEquipmentViewPublishedAsync(publication, default).AsTask());
        Assert.Same(failure, rig.Room.Seats[0].Critical!.Failure);
        authority.Fail = false;
        await rig.Round.OnEquipmentViewPublishedAsync(publication with
        {
            View = view with { EquippedItemIds = new[] { 300001, 202001, 201001, 100099 } },
        }, default);
        Assert.Empty(authority.Sent);
        Assert.Equal(1, rig.Room.Seats[0].Critical!.Current.Revision);
    }

    /// <summary>A view queued behind a round transition cannot revise the retired opening owner.</summary>
    [Fact]
    public async Task QueuedEquipmentViewCannotCrossItsRoundGeneration()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = rig.Room.Executor.ExecuteAsync(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            lock (rig.Room.Gate) rig.Room.MarkResult();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var view = new LocalEquipmentViewUpdated(11, 300001, new[] { 300001, 202001, 201001, 100051 }, 0, 202001, 0);
        var queued = rig.Round.OnEquipmentViewPublishedAsync(new("cn-324/game", rig.Host, view, true), default).AsTask();
        release.TrySetResult();
        await Task.WhenAll(boundary, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(authority.Sent);
        Assert.Null(rig.Room.Seats[0].Critical);
    }

    /// <summary>Only an owned native slot report revises its opening inputs, after the original bytes reach the peer.</summary>
    [Fact]
    public async Task InstalledWeaponReportUpdatesOnlyItsOwnedSlotAfterForwarding()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        authority.Inspect = _ => Assert.Single(rig.Feed.Sent);
        var actor = RoomRegistry.SceneEntity(rig.Room.Seats[0].Slot);
        var report = new RoundFeedSent(32800, new byte[] { 7 })
        {
            CriticalWeaponState = new(actor, 0, 3103), ReportedActors = [actor], OnRelay = true,
        };
        await rig.Round.HandleFeedAsync(rig.Host, report, default);
        var first = Assert.Single(authority.Sent);
        Assert.Equal(rig.Room.Seats[0].Relay!.Id, first.Session);
        Assert.Equal(3103, first.Message.EffectivePrimarySpecification);
        Assert.Null(first.Message.EffectiveSidearmSpecification);
        Assert.Equal(1, first.Message.Revision);
        authority.Inspect = null;
        await rig.Round.HandleFeedAsync(rig.Host, report, default);
        Assert.Single(authority.Sent);
        await rig.Round.HandleFeedAsync(rig.Guest, report, default);
        Assert.Equal(2, rig.Feed.Sent.Count);
        Assert.Single(authority.Sent);
        await rig.Round.HandleFeedAsync(rig.Host, report with { CriticalWeaponState = new(actor, 1, 1003) }, default);
        Assert.Equal(3103, authority.Sent[1].Message.EffectivePrimarySpecification);
        Assert.Equal(1003, authority.Sent[1].Message.EffectiveSidearmSpecification);
        Assert.Equal(2, authority.Sent[1].Message.Revision);
    }

    /// <summary>Expiry and reactivation keep one bounded timer owner, which is canceled when the seat's round ends.</summary>
    [Fact]
    public async Task TimedExpiryCanReactivateOnTheSameOwnedLoopAndRoundEndCancelsIt()
    {
        var authority = new CriticalAuthority { Duration = 60000 };
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var reporter = RoomRegistry.SceneEntity(rig.Room.Seats[0].Slot);
        var beneficiary = rig.Room.Seats[1];
        var trigger = new RoundFeedSent(32795, new byte[] { 8 })
        {
            CriticalEffectActor = RoomRegistry.SceneEntity(beneficiary.Slot), ReportedActors = [reporter], OnRelay = true,
        };
        authority.Inspect = update =>
        {
            if (update.TimedEffectActive) Assert.Equal(rig.Feed.Sent.Count, (update.Revision + 1) / 2);
        };
        await rig.Round.HandleFeedAsync(rig.Host, trigger, default);
        var state = beneficiary.Critical!;
        var timer = state.TimerTask;
        Assert.True(state.Current.TimedEffectActive);
        Assert.Equal(beneficiary.Relay!.Id, authority.Sent[0].Session);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            authority.Inspect = update => { if (!update.TimedEffectActive) expired.TrySetResult(); };
            lock (rig.Room.Gate)
            {
                state.ActiveUntil = Environment.TickCount64;
                state.WakeTimer();
            }
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Queueing the next trigger also waits for the expiry publication to leave the room executor.
            await rig.Round.HandleFeedAsync(rig.Host, trigger, default);
            Assert.Same(timer, state.TimerTask);
            Assert.False(timer.IsCompleted);
            Assert.True(state.Current.TimedEffectActive);
            Assert.Equal(3 + cycle * 2, state.Current.Revision);
        }
        lock (rig.Room.Gate) rig.Room.MarkResult();
        await timer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(beneficiary.Critical);
        Assert.Equal(5, authority.Sent.Count);
    }

    /// <summary>A canceled owner rejects new inputs even while the seat remains playing, and late wakes cannot touch its released timer.</summary>
    [Fact]
    public async Task CanceledCriticalLifetimeCannotPublishOrWakeAReleasedTimer()
    {
        var authority = new CriticalAuthority { Duration = 60000 };
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var seat = rig.Room.Seats[0];
        var actor = RoomRegistry.SceneEntity(seat.Slot);
        var trigger = new RoundFeedSent(32795, new byte[] { 8 }) { CriticalEffectActor = actor, ReportedActors = [actor] };
        await rig.Round.HandleFeedAsync(rig.Host, trigger, default);
        var state = seat.Critical!;
        await state.Cancellation.CancelAsync();
        await state.TimerTask.WaitAsync(TimeSpan.FromSeconds(5));
        state.WakeTimer();
        var view = new LocalEquipmentViewUpdated(11, 300001, new[] { 300001, 202001, 201001, 100051 }, 0, 202001, 0);
        await rig.Round.OnEquipmentViewPublishedAsync(new("cn-324/game", rig.Host, view, true), default);
        await rig.Round.HandleFeedAsync(rig.Host, trigger, default);
        Assert.Single(authority.Sent);
        Assert.Equal(1, state.Current.Revision);
        Assert.Null(state.Failure);
    }

    /// <summary>A catalog duration refresh wakes the existing timer even when its previous deadline is much later.</summary>
    [Fact]
    public async Task ShorterTimedRefreshReplacesTheDeadlineWithoutReplacingTheLoop()
    {
        var authority = new CriticalAuthority { Duration = 60000 };
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var seat = rig.Room.Seats[0];
        var actor = RoomRegistry.SceneEntity(seat.Slot);
        var trigger = new RoundFeedSent(32795, new byte[] { 8 }) { CriticalEffectActor = actor, ReportedActors = [actor] };
        await rig.Round.HandleFeedAsync(rig.Host, trigger, default);
        var state = seat.Critical!;
        var timer = state.TimerTask;
        var oldDeadline = state.ActiveUntil;
        authority.Duration = 1;
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        authority.Inspect = update => { if (!update.TimedEffectActive) expired.TrySetResult(); };
        await rig.Round.HandleFeedAsync(rig.Host, trigger, default);
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(timer, state.TimerTask);
        Assert.True(oldDeadline > Environment.TickCount64);
        Assert.False(state.Current.TimedEffectActive);
        Assert.Equal(3, state.Current.Revision);
    }

    /// <summary>Authority registration alone cannot grant pickups before this seat has a successful opening installation.</summary>
    [Fact]
    public async Task PickupRequiresAReadyOpeningAndAHealthyLiveRevisionOwner()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), weaponObjects: true,
            criticalAuthority: authority);
        await rig.StartAsync(16);
        lock (rig.Room.Gate) rig.Room.MarkPlaying();
        var seat = rig.Room.Seats[0];
        await rig.Round.HandleWeaponDropAsync(rig.Host, new(1, 2, 3, 0, 0, 0, 2001, 0, 0, true), default);
        var request = new RoundWeaponPickupRequested(rig.WeaponDrops.Sent[0].Message.ObjectId,
            RoomRegistry.SceneEntity(seat.Slot), seat.Slot);
        await rig.Round.HandleWeaponPickupAsync(rig.Host, request, default);
        Assert.Empty(rig.WeaponPickups.Sent);
        Assert.Contains(request.ObjectId, rig.Room.Weapons!.Objects);
    }

    /// <summary>Successful openings allow the optional pickup path; a failed update closes that path before object mutation.</summary>
    [Fact]
    public async Task ReadyPickupWorksAndFailedRevisionCannotBeSupersededByAnotherPickup()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), weaponObjects: true,
            criticalAuthority: authority);
        await StartCriticalRoundAsync(rig, 16);
        var seat = rig.Room.Seats[0];
        var actor = RoomRegistry.SceneEntity(seat.Slot);
        async Task<RoundWeaponPickupRequested> DropAsync()
        {
            await rig.Round.HandleWeaponDropAsync(rig.Host, new(1, 2, 3, 0, 0, 0, 2001, 0, 0, true), default);
            return new(rig.WeaponDrops.Sent[^1].Message.ObjectId, actor, seat.Slot);
        }
        var first = await DropAsync();
        await rig.Round.HandleWeaponPickupAsync(rig.Host, first, default);
        Assert.Equal(2, rig.WeaponPickups.Sent.Count);
        authority.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Round.HandleFeedAsync(rig.Host,
            new(32800, new byte[] { 7 }) { CriticalWeaponState = new(actor, 0, 3103), ReportedActors = [actor] }, default).AsTask());
        var second = await DropAsync();
        await rig.Round.HandleWeaponPickupAsync(rig.Host, second, default);
        Assert.Contains(second.ObjectId, rig.Room.Weapons!.Objects);
        Assert.Equal(2, rig.WeaponPickups.Sent.Count);
        Assert.IsType<IOException>(seat.Critical!.Failure);
    }

    /// <summary>A failed native trigger forward poisons the transition before any table can claim its inputs were installed.</summary>
    [Fact]
    public async Task FailedNativeForwardRetainsFailureAndPreventsLaterRevisions()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        var seat = rig.Room.Seats[0];
        var actor = RoomRegistry.SceneEntity(seat.Slot);
        var report = new RoundFeedSent(32800, new byte[] { 7 })
        {
            CriticalWeaponState = new(actor, 0, 3103), ReportedActors = [actor], OnRelay = true,
        };
        rig.Feed.Fail = true;
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            rig.Round.HandleFeedAsync(rig.Host, report, default).AsTask());
        Assert.Same(failure, seat.Critical!.Failure);
        Assert.Empty(authority.Sent);
        Assert.Equal(0, seat.Critical.Current.Revision);
        rig.Feed.Fail = false;
        await rig.Round.HandleFeedAsync(rig.Host, report, default);
        Assert.Empty(authority.Sent);
        Assert.Same(failure, seat.Critical.Failure);
    }

    /// <summary>An opening publication failure is retained on its seat and cannot be replaced by a later weapon report.</summary>
    [Fact]
    public async Task FailedOpeningRetainsItsSeatFailureAndCannotStartLiveRevisions()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await rig.StartAsync();
        foreach (var member in rig.Room.Seats) member.EquippedItemIds = [300001, 202001, 201001];
        rig.Seeds.Fail = true;
        await ReadySeedRelaysAsync(rig);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        lock (rig.Room.Gate) rig.Room.MarkPlaying();
        var seat = rig.Room.Seats[0];
        Assert.False(seat.Critical!.OpeningCompleted);
        Assert.IsType<IOException>(seat.Critical.Failure);
        var actor = RoomRegistry.SceneEntity(seat.Slot);
        await rig.Round.HandleFeedAsync(rig.Host, new(32800, new byte[] { 7 })
        {
            CriticalWeaponState = new(actor, 0, 3103), ReportedActors = [actor],
        }, default);
        Assert.Empty(authority.Sent);
    }

    private static async Task StartCriticalRoundAsync(Rig rig, int mode = 3)
    {
        await rig.StartAsync(mode);
        foreach (var seat in rig.Room.Seats) seat.EquippedItemIds = [300001, 202001, 201001];
        await ReadySeedRelaysAsync(rig);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        lock (rig.Room.Gate) rig.Room.MarkPlaying();
        Assert.All(rig.Room.Seats, seat => Assert.True(seat.Critical!.OpeningCompleted));
    }

    private sealed class CriticalAuthority : IMatchCriticalSeedAuthority
    {
        public string Protocol => "cn-324/relay";
        internal int Duration { get; set; }
        internal bool Fail { get; set; }
        internal Action<MatchCriticalSeedUpdate>? Inspect { get; set; }
        internal List<(long Session, MatchCriticalSeedUpdate Message)> Sent { get; } = [];
        public int TimedEffectDuration(MatchSeedEquipment equipment) => Duration;
        public MatchCriticalSeedUpdate WithPublishedEquipment(MatchCriticalSeedUpdate current, MatchSeedEquipment equipment) =>
            current with { Equipment = equipment };
        public void Validate(MatchCriticalSeedUpdate update) { }
        public ValueTask SendAsync(ISession session, MatchCriticalSeedUpdate message, CancellationToken ct)
        {
            if (Fail) throw new IOException("Critical publication failed.");
            Sent.Add((session.Id, message));
            Inspect?.Invoke(message);
            return ValueTask.CompletedTask;
        }
    }
}
