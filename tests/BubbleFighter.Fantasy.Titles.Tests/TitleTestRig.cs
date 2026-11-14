using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace BubbleFighter.Fantasy.Titles.Tests;

internal sealed class TitleTestRig : IDisposable
{
    internal const string Protocol = "titles-test/game";
    internal static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    internal static readonly InventoryWearGroup[] BaseGroups =
    [
        new(300001, 300031, 300001, RequireEquipped: true),
        new(201000, 201999, 201001, RequireEquipped: true),
        new(202000, 204999, 202001, RequireEquipped: true),
        new(207000, 207999, 207001, RequireEquipped: true),
    ];

    private readonly List<IDisposable> _subscriptions = [];
    internal MemoryInventoryRepository Store { get; } = new(new(8));
    internal InventoryLoadoutScope Scope { get; } = new("titles-test", [300001, 201001, 202001, 207001,
        300100, 301001, 400901, 400905, 400906, 400907, 400909]);
    internal TitleTestCatalog CatalogData { get; }
    internal TitlesOptions Options { get; }
    internal EventBus Events { get; } = new(NullLogger.Instance);
    internal TitleTestSession Session { get; } = new(1);
    internal List<string> Order { get; } = [];
    internal List<InventoryReplaced> Inventories { get; } = [];
    internal List<TitleMakeReplied> Made { get; } = [];
    internal List<TitleSetReplied> Equipped { get; } = [];
    internal List<MedalInfoReplied> Balances { get; } = [];
    internal List<EquipmentSelectionCommitted> Committed { get; } = [];
    internal List<IReadOnlyList<InventorySlotRecord>> Views { get; } = [];
    internal TitleInventoryProbe Inventory { get; }
    internal TitleMedalProbe Medals { get; }
    internal TitleSelection Selection { get; }
    internal IEquippedViewPush View { get; }
    internal IMessageSender<InventoryReplaced> InventorySender { get; }
    internal IMessageSender<TitleMakeReplied> MakeSender { get; }
    internal IMessageSender<TitleSetReplied> SetSender { get; }
    internal IMessageSender<MedalInfoReplied> MedalSender { get; }
    internal Func<CancellationToken, ValueTask>? BeforeInventory { get; set; }
    internal Action<string>? AfterPublication { get; set; }
    internal string? FailAt { get; set; }

    private TitleTestRig(bool modern)
    {
        CatalogData = new(modern);
        Options = new() { Enabled = true, Protocol = Protocol, BuildKey = Scope.Key,
            CatalogSchema = CatalogData.Schema.ToString(), MaxInFlightAccounts = 2 };
        Inventory = new(Store);
        Medals = new(Store);
        Selection = new(this);
        View = new ViewRecorder(this);
        InventorySender = new TitleTestSender<InventoryReplaced>(Protocol, async (message, ct) =>
        {
            if (BeforeInventory is { } before) await before(ct);
            ThrowAt("inventory");
            Order.Add("inventory");
            Inventories.Add(message);
            AfterPublication?.Invoke("inventory");
        });
        MakeSender = new TitleTestSender<TitleMakeReplied>(Protocol, (message, _) =>
        {
            ThrowAt("make"); Order.Add("make"); Made.Add(message); return ValueTask.CompletedTask;
        });
        SetSender = new TitleTestSender<TitleSetReplied>(Protocol, (message, _) =>
        {
            ThrowAt("set"); Order.Add("set"); Equipped.Add(message); return ValueTask.CompletedTask;
        });
        MedalSender = new TitleTestSender<MedalInfoReplied>(Protocol, (message, _) =>
        {
            ThrowAt("medals"); Order.Add("medals"); Balances.Add(message);
            AfterPublication?.Invoke("medals");
            return ValueTask.CompletedTask;
        });
        _subscriptions.Add(Events.OnAsync<EquipmentSelectionStarting>((_, _) =>
        { Order.Add("starting"); ThrowAt("starting"); return ValueTask.CompletedTask; }));
        _subscriptions.Add(Events.OnAsync<EquipmentSelectionCommitted>((message, _) =>
        { ThrowAt("committed"); Order.Add("committed"); Committed.Add(message); return ValueTask.CompletedTask; }));
        _subscriptions.Add(Events.OnAsync<EquipmentSelectionSettled>((_, ct) =>
        { ct.ThrowIfCancellationRequested(); Order.Add("settled"); return ValueTask.CompletedTask; }));
    }

    internal static async Task<TitleTestRig> CreateAsync(bool modern = false)
    {
        var rig = new TitleTestRig(modern);
        await rig.SeedUserAsync(1);
        return rig;
    }

    internal async Task SeedUserAsync(int owner)
    {
        var rows = new[] { 300001, 201001, 202001, 207001, 300100, 301001, 400905 }
            .Select((id, index) => new InventorySlotRecord(owner, InventoryBag.CharacterBag, index + 1, id, 1,
                index < 4 ? InventorySlotRecord.WornFlagBit : (byte)0x40) { Lifetime = InventoryLifetime.Permanent }).ToArray();
        await Store.ReplaceSlotsAsync(owner, InventoryBag.CharacterBag, rows, default);
        await Store.ReplaceSlotsAsync(owner, InventoryBag.EquippedBag,
            rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        await Store.GrantAsync(new(owner, Scope.Key, "tests", "initial", [new(30007, 30), new(30008, 20), new(99999, 7)]), default);
    }

    internal async Task AddTitleAsync(int itemId = 400901, byte flags = 0x40, InventoryLifetime? lifetime = null,
        int quantity = 1)
    {
        var row = new InventorySlotRecord(1, InventoryBag.CharacterBag, itemId, itemId, quantity, flags)
            { Lifetime = lifetime ?? InventoryLifetime.Permanent };
        await Store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, [row], default);
        await Store.AppendMissingItemsAsync(1, InventoryBag.EquippedBag, [row with { Bag = InventoryBag.EquippedBag }], default);
    }

    internal ValueTask<IReadOnlyList<InventorySlotRecord>> BagAsync() =>
        Store.ListLoadoutSlotsAsync(1, Scope, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);

    internal TitleRuntime Runtime(IPetEntitlements? pets = null, IEquipmentArticleResolver? articles = null,
        IFavoriteWeaponProgress? favorite = null, IMessageSender<TitleProgressNotified>? progress = null) =>
        new(Options, CatalogData.Load(Options.BonusMedalQuantity, Options.FavoriteTitles), Medals, Inventory, Scope, Selection, View, Events,
            InventorySender, MakeSender, SetSender, MedalSender, new FixedTime(), pets, articles, favorite, progress);

    internal void ThrowAt(string stage)
    {
        if (FailAt == stage) throw new IOException($"The {stage} publication failed.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        for (var index = _subscriptions.Count - 1; index >= 0; index--) _subscriptions[index].Dispose();
    }

    private sealed class FixedTime : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ViewRecorder(TitleTestRig rig) : IEquippedViewPush
    {
        /// <inheritdoc />
        public ValueTask SendSnapshotAsync(ISession session, int userNum, int characterId,
            IReadOnlyList<InventorySlotRecord> characterBag, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            rig.ThrowAt("view"); rig.Order.Add("view"); rig.Views.Add(characterBag);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask SendAsync(ISession session, int userNum, int characterId,
            IReadOnlyList<int> equippedIds, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class TitleSelection(TitleTestRig rig) : IEquipmentSelection
    {
        internal List<(long Owner, int ItemId, bool Worn)> Calls { get; } = [];
        internal List<InventorySlotRecord> Targets { get; } = [];
        internal bool Refuse { get; set; }
        internal Func<CancellationToken, ValueTask>? BeforeApply { get; set; }
        internal bool IncludeTitleGroup { get; set; } = true;

        /// <inheritdoc />
        public async ValueTask<PetInventorySnapshot?> ApplyOwnedSnapshotAsync(long userNum, InventorySlotRecord expectedTarget, bool worn, CancellationToken ct)
        {
            var itemId = expectedTarget.ItemId;
            Calls.Add((userNum, itemId, worn));
            Targets.Add(expectedTarget);
            if (BeforeApply is { } before) await before(ct);
            if (Refuse) return null;
            var initial = IncludeTitleGroup
                ? BaseGroups.Append(new(400901, 400909, 400901) { ItemIds = [400901, 400909] }).ToArray()
                : BaseGroups;
            var groups = await rig.Events.WaterfallAsync<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>(
                new(Protocol), _ => ValueTask.FromResult<IReadOnlyList<InventoryWearGroup>>(initial), ct);
            return await rig.Store.UpdateLoadoutSelectionSnapshotAsync(userNum, rig.Scope, groups, itemId, worn, ct,
                initialization: null, articleInitialization: null, target: new(expectedTarget, Now));
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

internal sealed class TitleTestSession(int owner) : ISessionWithFeatures
{
    /// <inheritdoc />
    public long Id => owner;
    /// <inheritdoc />
    public IConnection Connection => throw new NotSupportedException();
    /// <inheritdoc />
    public ClientProfile Profile { get; } = new("titles-test", "kr", 812, 0, 0, "Synthetic title tests");
    /// <inheritdoc />
    public ConcurrentDictionary<string, object> Items { get; } = new();
    /// <inheritdoc />
    public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(owner);
    /// <inheritdoc />
    public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class TitleTestSender<T>(string protocol, Func<T, CancellationToken, ValueTask> send) : IMessageSender<T> where T : IMessage
{
    /// <inheritdoc />
    public string Protocol => protocol;
    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return send(message, ct); }
}

internal sealed class TitleMedalProbe(ITitleMedalStore inner) : ITitleMedalStore
{
    public bool SupportsAcquisitionBonuses { get; set; } = inner.SupportsAcquisitionBonuses;

    internal List<TitleClaimCommand> Claims { get; } = [];
    internal List<long> ReadOwners { get; } = [];
    internal Func<TitleClaimCommand, CancellationToken, ValueTask>? BeforeClaim { get; set; }
    internal Func<TitleClaimResult?, CancellationToken, ValueTask>? AfterClaim { get; set; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TitleMedalCount>> ReadAsync(long userNum, string catalogScope, CancellationToken ct)
    { ReadOwners.Add(userNum); return inner.ReadAsync(userNum, catalogScope, ct); }

    /// <inheritdoc />
    public ValueTask<TitleMedalGrantResult> GrantAsync(TitleMedalGrantCommand command, CancellationToken ct) =>
        inner.GrantAsync(command, ct);

    /// <inheritdoc />
    public async ValueTask<TitleClaimResult?> TryClaimAsync(TitleClaimCommand command, CancellationToken ct)
    {
        Claims.Add(command);
        if (BeforeClaim is { } before) await before(command, ct);
        var result = await inner.TryClaimAsync(command, ct);
        if (AfterClaim is { } after) await after(result, ct);
        return result;
    }
}

internal sealed class TitleInventoryProbe(IInventoryLoadoutStore inner) : IInventoryLoadoutStore
{
    internal Func<CancellationToken, ValueTask>? AfterRead { get; set; }
    internal int Reads { get; private set; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
        InventoryBag bag, int maxCount, CancellationToken ct)
    {
        Reads++;
        var rows = await inner.ListLoadoutSlotsAsync(userNum, scope, bag, maxCount, ct);
        if (AfterRead is { } after) await after(ct);
        return rows;
    }

    /// <inheritdoc />
    public ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
        inner.GetLoadoutAsync(userNum, scope, ct);
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) =>
        inner.UpdateLoadoutSelectionAsync(userNum, scope, groups, itemId, worn, ct);
    /// <inheritdoc />
    public ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
        byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct) =>
        inner.UpdateLoadoutPresetsAsync(userNum, scope, activeSlot, pages, initializePages, ct);
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct) =>
        inner.ListSlotsAsync(userNum, bag, maxCount, ct);
    /// <inheritdoc />
    public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
        inner.ReplaceSlotsAsync(userNum, bag, slots, ct);
    /// <inheritdoc />
    public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
        inner.SetSlotWornAsync(userNum, itemId, worn, ct);
}
