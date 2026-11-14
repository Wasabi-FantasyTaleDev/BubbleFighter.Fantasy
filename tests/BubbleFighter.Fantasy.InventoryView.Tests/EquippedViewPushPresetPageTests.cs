using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Pins the <c>persist_preset_pages</c> behaviour of the view publication: off (the default) never touches
/// the page store and publishes no pages; on, the first publication materializes five pages from the worn
/// primary and special (the derived page's own convention) and persists them; afterwards the persisted pages
/// win over every later worn change, because the swap chain has no client-to-server save packet
/// (<c>kr-1256 sub_808F10</c> and <c>kr-812 sub_97A8D0</c> only request a slot switch).
/// </summary>
public sealed class EquippedViewPushPresetPageTests
{
    private static readonly int[] StarterWorn = [300001, 202001, 201001, 205001, 207001];

    [Fact]
    public async Task DefaultOffNeverReadsOrWritesThePageStore()
    {
        var state = new RecordingPresetState();
        var (local, remote) = (new RecordingSender(), new RecordingSender());
        var push = new EquippedViewPush(NullLogger.Instance, local, remote, state, selection: null);

        await push.SendSelectionAsync(new RecordingSession(1), 42, 300001, StarterWorn, default);

        Assert.Equal(0, state.PageReads);
        Assert.Equal(0, state.PageWrites);
        Assert.Null(Assert.Single(local.Sent).WeaponPresetPages);
    }

    [Fact]
    public async Task FirstPublicationSeedsFivePagesFromTheWornPairAndPersistsThem()
    {
        var state = new RecordingPresetState();
        var (local, remote) = (new RecordingSender(), new RecordingSender());
        var push = new EquippedViewPush(NullLogger.Instance, local, remote, state, selection: null,
            persistPresetPages: true);

        await push.SendSelectionAsync(new RecordingSession(2), 42, 300001, StarterWorn, default);

        Assert.Equal(1, state.PageReads);
        Assert.Equal(1, state.PageWrites);
        var seeded = Assert.IsAssignableFrom<IReadOnlyList<WeaponPresetPage>>(Assert.Single(local.Sent).WeaponPresetPages);
        Assert.Equal(WeaponPresetSlots.Count, seeded.Count);
        // The seed is the derived page the shipped bar carries: worn primary, worn special, zero word.
        Assert.All(seeded, page => Assert.Equal(new WeaponPresetPage(202001, 205001, 0), page));
    }

    [Fact]
    public async Task ACommittedSelectionRewritesOnlyTheActivePage()
    {
        WeaponPresetPage[] before = [new(204001, 0, 0), new(203001, 0, 7), new(202001, 0, 0), new(202003, 0, 0), new(204003, 0, 0)];
        var state = new RecordingPresetState { Pages = [.. before], ActiveSlot = 1 };
        var (local, remote) = (new RecordingSender(), new RecordingSender());
        var push = new EquippedViewPush(NullLogger.Instance, local, remote, state, selection: null,
            persistPresetPages: true);

        await push.SendSelectionAsync(new RecordingSession(3), 42, 300001, StarterWorn, default);

        Assert.Equal(1, state.PageWrites);
        // Page 1 now holds the committed worn primary and special and keeps its third word; every other page keeps its weapon.
        WeaponPresetPage[] expected = [before[0], new(202001, 205001, 7), before[2], before[3], before[4]];
        Assert.Equal(expected, state.Pages);
        Assert.Equal(expected, Assert.IsAssignableFrom<IReadOnlyList<WeaponPresetPage>>(Assert.Single(local.Sent).WeaponPresetPages));
    }

    [Fact]
    public async Task ARemovedPrimaryKeepsThePageWeaponAndALoginReplayNeverWrites()
    {
        WeaponPresetPage[] before = [new(204001, 0, 0), new(203001, 0, 0), new(202001, 0, 0), new(202003, 0, 0), new(204003, 0, 0)];
        var state = new RecordingPresetState { Pages = [.. before] };
        var (local, remote) = (new RecordingSender(), new RecordingSender());
        var push = new EquippedViewPush(NullLogger.Instance, local, remote, state, selection: null,
            persistPresetPages: true);

        // No worn primary: recording a zero would unbind the page, so nothing is written.
        await push.SendSelectionAsync(new RecordingSession(3), 42, 300001, [300001, 205001], default);
        // A plain (non-selection) publication such as a login replay reads pages but never records them.
        await push.SendAsync(new RecordingSession(3), 42, 300001, StarterWorn, default);

        Assert.Equal(0, state.PageWrites);
        Assert.Equal(before, state.Pages);
    }

    [Fact]
    public async Task APresetSwitchPublicationNeverRecordsTheWornWeaponIntoTheNewPage()
    {
        // Page 1 names a weapon the switch could not wear; republishing the previous page's weapon must not overwrite it.
        WeaponPresetPage[] before = [new(202001, 205001, 0), new(203001, 0, 0), new(202001, 0, 0), new(202003, 0, 0), new(204003, 0, 0)];
        var state = new RecordingPresetState { Pages = [.. before], ActiveSlot = 1 };
        var (local, remote) = (new RecordingSender(), new RecordingSender());
        var push = new EquippedViewPush(NullLogger.Instance, local, remote, state, selection: null,
            persistPresetPages: true);
        InventorySlotRecord[] bag = [.. StarterWorn.Select((id, index) =>
            new InventorySlotRecord(42, InventoryBag.CharacterBag, index + 1, id, 1, InventorySlotRecord.WornFlagBit))];

        await push.SendPresetSwitchAsync(new RecordingSession(5), 42, 300001, bag, default);

        Assert.Equal(0, state.PageWrites);
        Assert.Equal(before, Assert.IsAssignableFrom<IReadOnlyList<WeaponPresetPage>>(Assert.Single(local.Sent).WeaponPresetPages));
    }

    [Fact]
    public async Task WithoutAStoreTheSwitchNeverMaterializesPages()
    {
        // An ack-only row (no persistence row) keeps the derived bar even with the switch held on; the
        // plugin's activation check is what refuses this combination loud.
        var (local, remote) = (new RecordingSender(), new RecordingSender());
        var push = new EquippedViewPush(NullLogger.Instance, local, remote, playerState: null, selection: null,
            persistPresetPages: true);

        await push.SendSelectionAsync(new RecordingSession(4), 42, 300001, StarterWorn, default);

        Assert.Null(Assert.Single(local.Sent).WeaponPresetPages);
    }

    /// <summary>A completed-view event retains the sent snapshot and cannot precede the dialect's final frame.</summary>
    [Fact]
    public async Task CompletedViewEventFollowsSuccessfulSendAndOwnsItsSnapshot()
    {
        var events = new EventBus(NullLogger.Instance);
        var published = new List<EquipmentViewPublished>();
        using var subscription = events.OnAsync<EquipmentViewPublished>((message, _) =>
        {
            published.Add(message);
            return ValueTask.CompletedTask;
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new RecordingSender
        {
            BeforeSend = async ct =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            },
        };
        var push = new EquippedViewPush(NullLogger.Instance, local, new RecordingSender(), events: events);
        var session = new RecordingSession(8);
        var worn = StarterWorn.ToArray();
        var send = push.SendSelectionAsync(session, 42, 300001, worn, default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(published);
            worn[1] = 204099;
        }
        finally
        {
            release.TrySetResult();
            await send;
        }
        var result = Assert.Single(published);
        Assert.Same(session, result.Session);
        Assert.Equal(local.Protocol, result.Protocol);
        Assert.True(result.SelectionChange);
        Assert.False(result.IsSceneReplay);
        Assert.Same(Assert.Single(local.Sent), result.View);
        Assert.Equal(StarterWorn, result.View.EquippedItemIds);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)result.View.EquippedItemIds)[1] = 204099);
    }

    /// <summary>A failed local view has no completed publication event for scene owners to consume.</summary>
    [Fact]
    public async Task FailedViewDoesNotPublishACompletion()
    {
        var events = new EventBus(NullLogger.Instance);
        var notifications = 0;
        using var subscription = events.OnAsync<EquipmentViewPublished>((_, _) =>
        {
            notifications++;
            return ValueTask.CompletedTask;
        });
        var local = new RecordingSender { BeforeSend = _ => ValueTask.FromException(new IOException("View send failed.")) };
        var push = new EquippedViewPush(NullLogger.Instance, local, new RecordingSender(), events: events);
        await Assert.ThrowsAsync<IOException>(() =>
            push.SendSelectionAsync(new RecordingSession(9), 42, 300001, StarterWorn, default).AsTask());
        Assert.Equal(0, notifications);
    }

    /// <summary>The completed snapshot owns the preset table as well as the worn list, even while its sender is suspended.</summary>
    [Fact]
    public async Task CompletedViewOwnsThePersistedPresetTable()
    {
        var events = new EventBus(NullLogger.Instance);
        EquipmentViewPublished? published = null;
        using var subscription = events.OnAsync<EquipmentViewPublished>((message, _) =>
        {
            published = message;
            return ValueTask.CompletedTask;
        });
        var state = new RecordingPresetState { Pages = WeaponPresetPages.SeedFromWorn(202001, 205001) };
        var local = new RecordingSender
        {
            BeforeSend = _ =>
            {
                state.Pages![0] = new(204099, 0, 0);
                return ValueTask.CompletedTask;
            },
        };
        var push = new EquippedViewPush(NullLogger.Instance, local, new RecordingSender(), state,
            persistPresetPages: true, events: events);
        await push.SendSelectionAsync(new RecordingSession(10), 42, 300001, StarterWorn, default);
        var pages = Assert.IsAssignableFrom<IReadOnlyList<WeaponPresetPage>>(published!.View.WeaponPresetPages);
        Assert.All(pages, page => Assert.Equal(new WeaponPresetPage(202001, 205001, 0), page));
        Assert.Throws<NotSupportedException>(() => ((IList<WeaponPresetPage>)pages)[0] = new(204099, 0, 0));
    }

    private sealed class RecordingPresetState : IPlayerInventoryState
    {
        internal int PageReads { get; private set; }
        internal int PageWrites { get; private set; }
        internal WeaponPresetPage[]? Pages { get; set; }
        internal byte ActiveSlot { get; init; }
        public ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(ActiveSlot);
        public ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct) =>
            throw new InvalidOperationException("View publications never change the active slot.");
        public ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct)
        {
            PageReads++;
            return ValueTask.FromResult(Pages?.ToArray());
        }
        public ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct)
        {
            PageReads++;
            if (Pages is null)
            {
                Pages = WeaponPresetPages.CopyTable(initialPages);
                PageWrites++;
            }
            return ValueTask.FromResult(Pages.ToArray());
        }
        public ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct)
        {
            PageWrites++;
            Pages = WeaponPresetPages.CopyTable(pages);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSender : IMessageSender<LocalEquipmentViewUpdated>, IMessageSender<RemoteEquipmentViewUpdated>
    {
        internal List<LocalEquipmentViewUpdated> Sent { get; } = [];
        internal Func<CancellationToken, ValueTask>? BeforeSend { get; init; }
        public string Protocol => "test/game";
        public async ValueTask SendAsync(ISession session, LocalEquipmentViewUpdated message, CancellationToken ct)
        {
            if (BeforeSend is not null) await BeforeSend(ct);
            Sent.Add(message);
        }
        public ValueTask SendAsync(ISession session, RemoteEquipmentViewUpdated message, CancellationToken ct) =>
            throw new InvalidOperationException("These publications are local-only.");
    }
}
