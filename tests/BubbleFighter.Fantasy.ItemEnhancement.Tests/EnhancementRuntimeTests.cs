using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.ItemEnhancement.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

public sealed class EnhancementRuntimeTests
{
    [Theory]
    [InlineData(0, 1000, 10000, true, 1)]
    [InlineData(0, 9000, 10000, false, 1)]
    [InlineData(1, 9000, 10000, true, 0)]
    // The additive applies its own configured chance rather than an assumed certainty.
    [InlineData(1, 9000, 2000, false, 1)]
    [InlineData(1, 1000, 2000, true, 1)]
    public async Task OneAtomicDebitPublishesInventoryBeforeOutcome(int additive, int sample, int additiveChance, bool success, int rolls)
    {
        var rig = await Rig.CreateAsync(sample, additiveChance: additiveChance);
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], additive), default);
        Assert.Equal(["inventory", "result"], rig.Order);
        Assert.Equal(success, Assert.Single(rig.Completed).Succeeded);
        Assert.Equal(rolls, rig.Rolls);
        var snapshot = Assert.Single(rig.Published);
        Assert.True(snapshot.IsCompleteSnapshot);
        var target = snapshot.CharacterBag.Single(row => row.ItemId == 202020);
        Assert.Equal(success ? ItemEnhancementKind.Weapon : ItemEnhancementKind.None, target.Enhancements!.Find(rig.Scope.Key)!.Kind);
        Assert.Equal(1, snapshot.CharacterBag.Single(row => row.ItemId == 190111).Quantity);
        Assert.All(snapshot.CharacterBag.Where(row => row.ItemId is >= 190106 and <= 190109), row => Assert.Equal(2, row.Quantity));
        Assert.Equal(2 - additive, snapshot.CharacterBag.Single(row => row.ItemId == 101745).Quantity);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("unknown")]
    [InlineData("argument")]
    [InlineData("phase")]
    [InlineData("worn")]
    [InlineData("insufficient")]
    [InlineData("operation")]
    public async Task UnsupportedOrUnavailableRequestCannotSpendOrRoll(string reason)
    {
        var rig = await Rig.CreateAsync(1);
        if (reason == "phase") rig.Session.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Room);
        if (reason is "worn" or "insufficient")
        {
            var rows = (await rig.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 100, default)).Select(row =>
                reason == "worn" && row.ItemId == 202020 ? row with { Flags = InventorySlotRecord.WornFlagBit }
                : reason == "insufficient" && row.ItemId == 190106 ? row with { Quantity = 1 } : row).ToArray();
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
            if (reason == "worn") await rig.Store.UpdateLoadoutSelectionAsync(1, rig.Scope, [], 202020, true, default);
        }
        var before = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(reason == "operation" ? LabOperation.Compound : LabOperation.Enchant,
            reason == "set" ? [rig.Key, rig.Key + 1] : [reason == "unknown" ? rig.Key + 99 : rig.Key], reason == "argument" ? 2 : 0), default);
        Assert.Single(rig.Refused);
        Assert.Empty(rig.Published);
        Assert.Empty(rig.Completed);
        Assert.Equal(0, rig.Rolls);
        Assert.Equal(before.CharacterBag, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag);
    }

    [Fact]
    public async Task FailedPublicationDoesNotSendSuccessOrRepeatCommittedDebit()
    {
        var rig = await Rig.CreateAsync(1);
        rig.FailPublication = true;
        using var runtime = rig.Runtime();
        await Assert.ThrowsAsync<IOException>(async () => await runtime.HandleAsync(rig.Session,
            new(LabOperation.Enchant, [rig.Key], 0), default));
        Assert.Empty(rig.Completed);
        var snapshot = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        Assert.Equal(1, snapshot.CharacterBag.Single(row => row.ItemId == 190111).Quantity);
        rig.FailPublication = false;
        await runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default);
        Assert.Single(rig.Refused);
        Assert.Equal(1, rig.Rolls);
    }

    [Fact]
    public async Task UnknownArticleLookupDoesNotInitializeOtherNewEquipment()
    {
        var rig = await Rig.CreateAsync(1);
        await rig.Store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
            [new(1, InventoryBag.CharacterBag, 99, 202021, 1, 0) { Lifetime = InventoryLifetime.Permanent }], default);
        Assert.Null(await rig.Articles.ResolveAsync(1, checked((ulong)(rig.Key + 100)), default));
        var rows = await rig.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 100, default);
        Assert.Null(rows.Single(row => row.ItemId == 202021).ArticleIdentity);
    }

    [Fact]
    public void DisabledConfigurationNeedsNoInventedEconomyDefaults()
    {
        var options = new ItemEnhancementOptions();
        Validator.ValidateObject(options, new(options), true);
        Assert.Empty(options.Validate());
        options.Enabled = true;
        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SceneOrGenerationChangedDuringResolutionCannotSpend(bool returnToMenu)
    {
        var rig = await Rig.CreateAsync(1);
        var resolver = new HeldResolver(rig.Articles);
        using var runtime = rig.Runtime(resolver);
        var pending = runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var player = rig.Session.Features.Get<IPlayerSession>();
        player.MoveTo(SessionPhase.Plaza);
        if (returnToMenu) player.MoveTo(SessionPhase.MainMenu);
        resolver.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(rig.Refused);
        Assert.Empty(rig.Completed);
        Assert.Equal(0, rig.Rolls);
        Assert.Equal(2, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag
            .Single(row => row.ItemId == 190111).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveAccountAndGlobalCapacityRefuseAnotherRequestWithoutQueuing(bool anotherAccount)
    {
        var rig = await Rig.CreateAsync(1, anotherAccount ? 1 : 128);
        var resolver = new HeldResolver(rig.Articles);
        using var runtime = rig.Runtime(resolver);
        var pending = runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.HandleAsync(anotherAccount ? new Session(2) : rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default);
        Assert.Single(rig.Refused);
        Assert.Equal(1, resolver.Resolutions);
        resolver.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(rig.Completed);
        Assert.Equal(1, rig.Rolls);
        Assert.Equal(1, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag
            .Single(row => row.ItemId == 190111).Quantity);
    }

    [Fact]
    public async Task CancelledResolutionReleasesTheAccountBudgetForTheNextAttempt()
    {
        var rig = await Rig.CreateAsync(1, 1);
        var resolver = new HeldResolver(rig.Articles);
        using var runtime = rig.Runtime(resolver);
        using var cancellation = new CancellationTokenSource();
        var pending = runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), cancellation.Token).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        resolver.Release.TrySetResult();
        await runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default);
        Assert.Empty(rig.Refused);
        Assert.Single(rig.Completed);
        Assert.Equal(1, rig.Rolls);
    }

    [Fact]
    public async Task ChangedTargetBetweenResolutionAndCommitCannotConsumeMaterials()
    {
        var rig = await Rig.CreateAsync(1);
        var resolver = new HeldResolver(rig.Articles);
        using var runtime = rig.Runtime(resolver);
        var pending = runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rows = await rig.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, 100, default);
        await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
            rows.Select(row => row.ItemId == 202020 ? row with { SlotKey = row.SlotKey + 100 } : row).ToArray(), default);
        var before = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        resolver.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(rig.Refused);
        Assert.Empty(rig.Published);
        Assert.Empty(rig.Completed);
        Assert.Equal(before.CharacterBag, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public async Task OutOfRangeRandomSamplesFailBeforeCommittingAnyDebit(int sample)
    {
        var rig = await Rig.CreateAsync(sample);
        var before = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        using var runtime = rig.Runtime();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.HandleAsync(rig.Session,
            new(LabOperation.Enchant, [rig.Key], 0), default).AsTask());
        Assert.Empty(rig.Published);
        Assert.Empty(rig.Completed);
        Assert.Equal(before.CharacterBag, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedEnhancementCannotPublishIntoAnotherScene(bool returnToMenu)
    {
        var rig = await Rig.CreateAsync(1);
        var store = new CommittedStore(rig.Store);
        using var runtime = rig.Runtime(inventoryStore: store);
        var pending = runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default).AsTask();
        await store.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var player = rig.Session.Features.Get<IPlayerSession>();
        player.MoveTo(SessionPhase.Room);
        if (returnToMenu)
        {
            player.MoveTo(SessionPhase.Plaza);
            player.MoveTo(SessionPhase.MainMenu);
        }
        store.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(rig.Order);
        var snapshot = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        Assert.Equal(1, snapshot.CharacterBag.Single(row => row.ItemId == 190111).Quantity);
        Assert.Equal(ItemEnhancementKind.Weapon, snapshot.CharacterBag.Single(row => row.ItemId == 202020).Enhancements!.Find(rig.Scope.Key)!.Kind);
        Assert.Null(await rig.Store.TryEnhanceAsync(store.Command!, default));
        Assert.Equal(snapshot.CharacterBag, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag);
    }

    [Fact]
    public async Task SceneChangeDuringInventoryPublicationSuppressesEnhancementCompletion()
    {
        var rig = await Rig.CreateAsync(1);
        rig.Publication = () =>
        {
            var player = rig.Session.Features.Get<IPlayerSession>();
            player.MoveTo(SessionPhase.Room);
            player.MoveTo(SessionPhase.Plaza);
            player.MoveTo(SessionPhase.MainMenu);
        };
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.Enchant, [rig.Key], 0), default);
        Assert.Equal(["inventory"], rig.Order);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Refused);
        Assert.Equal(1, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag.Single(row => row.ItemId == 190111).Quantity);
    }

    private sealed class CommittedStore(IItemEnhancementStore inner) : IItemEnhancementStore
    {
        internal TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ItemEnhancementCommand? Command { get; private set; }
        public ValueTask<InventorySnapshot> InitializeArticlesAsync(ArticleInitializationCommand command, CancellationToken ct) =>
            inner.InitializeArticlesAsync(command, ct);
        public ValueTask<InventorySnapshot> ReadEnhancementInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
            inner.ReadEnhancementInventoryAsync(userNum, scope, ct);
        public ValueTask<InventorySnapshot?> SelectArticleAsync(ArticleSelectionCommand command, CancellationToken ct) =>
            inner.SelectArticleAsync(command, ct);
        public async ValueTask<InventorySnapshot?> TryEnhanceAsync(ItemEnhancementCommand command, CancellationToken ct)
        {
            Command = command;
            var result = await inner.TryEnhanceAsync(command, ct);
            Assert.NotNull(result);
            Committed.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return result;
        }
    }

    private sealed class HeldResolver(IEquipmentArticleResolver inner) : IEquipmentArticleResolver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Resolutions { get; private set; }
        public ArticleInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) => inner.CreateInitialization(userNum, now);
        public async ValueTask<InventorySlotRecord?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct)
        {
            Resolutions++;
            var result = await inner.ResolveAsync(userNum, articleKey, ct);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return result;
        }
    }

    private sealed class Rig
    {
        internal MemoryInventoryRepository Store { get; } = new(new(2));
        internal InventoryLoadoutScope Scope { get; } = new("test-catalog", [202020, 202021, 190111, 190106, 190107, 190108, 190109, 101745]);
        internal Session Session { get; } = new();
        internal List<string> Order { get; } = [];
        internal List<InventoryReplaced> Published { get; } = [];
        internal List<LabEnchantCompleted> Completed { get; } = [];
        internal List<LabOperationRefused> Refused { get; } = [];
        internal EnhancementArticles Articles { get; private set; } = null!;
        internal EnhancementCatalog Catalog { get; private set; } = null!;
        internal long Key { get; private set; }
        internal int Rolls { get; private set; }
        internal bool FailPublication { get; set; }
        internal Action? Publication { get; set; }
        private int _sample;
        private readonly ItemEnhancementOptions _options = new()
        {
            Enabled = true, Protocol = "test/game", BuildKey = "test-catalog", CardItemId = 190111,
            KitItemIds = [190106, 190107, 190108, 190109], AdditiveItemId = 101745,
            CardQuantity = 1, KitQuantity = 2, AdditiveQuantity = 1, SuccessBasisPoints = 5000, AdditiveSuccessBasisPoints = 10000,
        };

        internal static async Task<Rig> CreateAsync(int sample, int capacity = 128, int additiveChance = 10000)
        {
            var rig = new Rig { _sample = sample };
            rig._options.MaxInFlightAccounts = capacity;
            rig._options.AdditiveSuccessBasisPoints = additiveChance;
            rig.Catalog = EnhancementCatalog.Load(new CatalogData(), "catalog", 20000, 100, 100, 100);
            var rows = rig.Scope.SupportedItemIds.Where(id => id != 202021).Select((id, index) =>
                new InventorySlotRecord(1, InventoryBag.CharacterBag, index + 1, id, id == 202020 ? 1 : id is >= 190106 and <= 190109 ? 4 : 2, 0)
                { Lifetime = InventoryLifetime.Permanent }).ToArray();
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
            rig.Articles = new(rig.Catalog, rig.Store, rig.Scope, TimeProvider.System);
            var snapshot = await rig.Articles.PrepareAsync(1, default);
            rig.Key = checked((long)snapshot.CharacterBag.Single(row => row.ItemId == 202020).ArticleIdentity!.ArticleKey);
            return rig;
        }

        internal EnhancementRuntime Runtime(IEquipmentArticleResolver? resolver = null, IItemEnhancementStore? inventoryStore = null) => new(_options, Catalog, inventoryStore ?? Store, resolver ?? Articles, Scope,
            new Sender<LabOperationRefused>(message => { Order.Add("refused"); Refused.Add(message); }),
            new Sender<LabEnchantCompleted>(message => { Order.Add("result"); Completed.Add(message); }),
            new Sender<InventoryReplaced>(message =>
            {
                if (FailPublication) throw new IOException("Inventory transport failed.");
                Order.Add("inventory"); Published.Add(message); Publication?.Invoke();
            }), TimeProvider.System, () => { Rolls++; return _sample; });
    }

    private sealed class Sender<T>(Action<T> send) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); send(message); return ValueTask.CompletedTask; }
    }

    private sealed class Session(long user = 1) : ISessionWithFeatures
    {
        public long Id => user;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new("test", "kr", 1, 0, 0, "Synthetic test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(user);
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The catalog bounds still hold now that the workbook comes from the shared parse.</summary>
    [Theory]
    [InlineData(5, 100, 100, "max_catalog_rows")]
    [InlineData(100, 2, 100, "max_catalog_columns")]
    [InlineData(100, 100, 3, "max_cell_characters")]
    public void CatalogBoundsStillApply(int rows, int columns, int cell, string option)
    {
        var error = Assert.Throws<InvalidDataException>(() => EnhancementCatalog.Load(new CatalogData(), "catalog", 20000, rows, columns, cell));
        Assert.Contains(option, error.Message);
    }

    private sealed class CatalogData : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path)
        {
            XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
            XElement Sheet(string name, params string[][] rows) => new(ns + "Worksheet", new XAttribute(ns + "Name", name),
                new XElement(ns + "Table", rows.Select(row => new XElement(ns + "Row", row.Select(cell =>
                    new XElement(ns + "Cell", new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), cell)))))));
            var items = new List<string[]> { new[] { "itemId", "mainCategory", "subCategory", "durationType", "isEnchant" },
                new[] { "202020", "Weapons", "Test", "day", "1" }, new[] { "202021", "Weapons", "Test", "day", "1" } };
            items.AddRange(new[] { 190111, 190106, 190107, 190108, 190109, 101745 }.Select(id => new[] { id.ToString(), "", "", "count", "0" }));
            return Encoding.UTF8.GetBytes(new XElement(ns + "Workbook",
                Sheet("MainCategory", ["id", "name"], ["1", "Weapons"], ["2", "Clothes"]),
                Sheet("SubCategory", ["id", "name"], ["1", "Test"]), Sheet("ShopItem", items.ToArray()),
                Sheet("ItemAttr", ["index", "value", "category", "grade", "weight", "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4"]),
                Sheet("RandomTable", ["buffIndex", "buffName", "min", "max", "conv"])).ToString());
        }
    }
}
