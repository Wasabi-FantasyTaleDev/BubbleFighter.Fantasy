using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InventoryView;
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
        var seeded = Assert.IsType<WeaponPresetPage[]>(Assert.Single(local.Sent).WeaponPresetPages);
        Assert.Equal(WeaponPresetSlots.Count, seeded.Length);
        // The seed is the derived page the shipped bar carries: worn primary, worn special, zero word.
        Assert.All(seeded, page => Assert.Equal(new WeaponPresetPage(202001, 205001, 0), page));
    }

    [Fact]
    public async Task MaterializedPagesWinOverALaterWornChange()
    {
        var state = new RecordingPresetState
        {
            Pages = [new(204001, 0, 0), new(203001, 0, 0), new(202001, 0, 0), new(202003, 0, 0), new(204003, 0, 0)],
        };
        var (local, remote) = (new RecordingSender(), new RecordingSender());
        var push = new EquippedViewPush(NullLogger.Instance, local, remote, state, selection: null,
            persistPresetPages: true);

        await push.SendSelectionAsync(new RecordingSession(3), 42, 300001, StarterWorn, default);

        Assert.Equal(1, state.PageReads);
        Assert.Equal(0, state.PageWrites);
        // The publication still carries the current worn snapshot for the view; only the pages stay put.
        Assert.Equal(202001, Assert.Single(local.Sent).PrimaryWeaponId);
        Assert.Equal(state.Pages, Assert.IsType<WeaponPresetPage[]>(Assert.Single(local.Sent).WeaponPresetPages));
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

    private sealed class RecordingPresetState : IPlayerInventoryState
    {
        internal int PageReads { get; private set; }
        internal int PageWrites { get; private set; }
        internal WeaponPresetPage[]? Pages { get; set; }
        public ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult((byte)0);
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
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSender : IMessageSender<LocalEquipmentViewUpdated>, IMessageSender<RemoteEquipmentViewUpdated>
    {
        internal List<LocalEquipmentViewUpdated> Sent { get; } = [];
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, LocalEquipmentViewUpdated message, CancellationToken ct)
        {
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
        public ValueTask SendAsync(ISession session, RemoteEquipmentViewUpdated message, CancellationToken ct) =>
            throw new InvalidOperationException("These publications are local-only.");
    }
}
