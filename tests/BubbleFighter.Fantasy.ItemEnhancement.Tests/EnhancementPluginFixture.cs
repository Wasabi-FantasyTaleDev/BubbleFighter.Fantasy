using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

internal sealed class EnhancementPluginFixture : IAsyncDisposable
{
    internal const string Protocol = "enhancement-test/game";
    internal const int WeaponSelector = 701;
    internal const int ClothingSelector = 702;
    private readonly List<PluginContext> _contexts = [];
    internal ServiceRegistry Services { get; } = new();
    internal EventBus Events { get; } = new(NullLogger.Instance);
    internal MemoryInventoryRepository Store { get; } = new(new(4));
    internal InventoryLoadoutScope Scope { get; }
    internal MetadataProbe Metadata { get; }
    internal EnhancementProbe Enhancements { get; }
    internal ISessionWithFeatures Session { get; } = new TestSession(1);
    internal PluginContext Setup { get; private set; } = null!;
    internal IMessageRouter Router => Services.Get<IMessageRouter>();
    internal List<string> Order { get; } = [];
    internal List<InventoryReplaced> Inventories { get; } = [];
    internal List<LabEnchantCompleted> Completed { get; } = [];
    internal List<LabOperationRefused> Refused { get; } = [];
    internal List<ScienceLabConfigurationPublished> Configurations { get; } = [];
    internal List<LabConversionCompleted> Converted { get; } = [];
    internal List<FavoriteWeaponGradeChanged> GradeNotices { get; } = [];
    internal const int ConversionTarget = 300202;
    internal const int ConversionCard = 190151;
    internal const int SetMemberA = 300203;
    internal const int SetMemberB = 300204;
    internal const int ConversionSet = 2000900;
    internal const int PlainSet = 2000901;
    internal List<LabAdvancementCompleted> Advanced { get; } = [];
    // Three native clothing sets of two members: HC (class 1), advanced HC (class 2) and L4HC (class 3), and a card for each tier.
    internal const int HcSet = 2000910, AdvancedHcSet = 2000911, L4Set = 2000912, HcCard = 190247, AdvancedHcCard = 190558, L4Card = 190570;
    internal static readonly int[] AdvancementMembers = [300301, 300302, 300311, 300312, 300321, 300322];
    internal Func<CancellationToken, ValueTask>? BeforeInventory { get; set; }

    private EnhancementPluginFixture(bool equipment, bool conversion, bool advancement)
    {
        int[] items = [202020, 202021, 300201, 190111, 190106, 190107, 190108, 190109, 101745];
        if (conversion) items = [.. items, ConversionTarget, ConversionCard, SetMemberA, SetMemberB];
        if (advancement) items = [.. items, .. AdvancementMembers, HcCard, AdvancedHcCard, L4Card];
        Scope = new("enhancement-test", equipment ? [.. items, 300001, 202001, 201001, 207001] : items);
        Metadata = new(Store);
        Enhancements = new(Store);
    }

    internal static async Task<EnhancementPluginFixture> CreateAsync(bool messages = true,
        string? missing = null, string? duplicate = null, bool equipment = false, bool conversion = false, bool advancement = false)
    {
        var fixture = new EnhancementPluginFixture(equipment, conversion, advancement);
        fixture.Setup = fixture.Context("setup");
        if (missing != "client-data") fixture.Setup.Effect(fixture.Services.Register<IClientDataStore>(new CatalogData(advancement)));
        if (missing != "scope") fixture.Setup.Effect(fixture.Services.Register<IInventoryBuildScope>(new BuildScope(fixture.Scope)));
        if (missing != "enhancement-store") fixture.Setup.Effect(fixture.Services.Register<IItemEnhancementStore>(fixture.Enhancements));
        if (missing != "metadata-store") fixture.Setup.Effect(fixture.Services.Register<IInventoryMetadataStore>(fixture.Metadata));
        if (conversion) fixture.Setup.Effect(fixture.Services.Register<ILaboratoryInventoryStore>(fixture.Store));
        var rows = fixture.Scope.SupportedItemIds.Where(id => id != 202021).Select((id, index) =>
            new InventorySlotRecord(1, InventoryBag.CharacterBag, index + 1, id,
                id is 202020 or 300201 or 300001 or 202001 or 201001 or 207001 || AdvancementMembers.Contains(id) ? 1 : 8,
                id is 300001 or 202001 or 201001 or 207001 ? (byte)0x41 : (byte)0x40)
            { Lifetime = InventoryLifetime.Permanent }).ToArray();
        await fixture.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await fixture.Store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag,
            rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        if (!messages) return fixture;

        var router = fixture.Context("router");
        router.BindServices(typeof(MessageRouterPlugin).GetCustomAttribute<PluginAttribute>()!);
        await new MessageRouterPlugin().ActivateAsync(router, default);
        Binding<LabOperationRequested>("binding.operation");
        Binding<ScienceLabConfigurationRequested>("binding.configuration");
        Sender<InventoryReplaced>("sender.inventory", async (message, ct) =>
        {
            if (fixture.BeforeInventory is { } before) await before(ct);
            fixture.Order.Add("inventory"); fixture.Inventories.Add(message);
        });
        Sender<LabEnchantCompleted>("sender.completed", (message, _) =>
        { fixture.Order.Add("completed"); fixture.Completed.Add(message); return ValueTask.CompletedTask; });
        Sender<LabOperationRefused>("sender.refused", (message, _) =>
        { fixture.Order.Add("refused"); fixture.Refused.Add(message); return ValueTask.CompletedTask; });
        Sender<ScienceLabConfigurationPublished>("sender.configuration", (message, _) =>
        { fixture.Order.Add("configuration"); fixture.Configurations.Add(message); return ValueTask.CompletedTask; });
        Sender<FavoriteWeaponGradeChanged>("sender.favorite", (message, _) =>
        { fixture.Order.Add("grade"); fixture.GradeNotices.Add(message); return ValueTask.CompletedTask; });
        if (conversion)
            Sender<LabConversionCompleted>("sender.converted", (message, _) =>
            { fixture.Order.Add("converted"); fixture.Converted.Add(message); return ValueTask.CompletedTask; });
        if (advancement)
            Sender<LabAdvancementCompleted>("sender.advanced", (message, _) =>
            { fixture.Order.Add("advanced"); fixture.Advanced.Add(message); return ValueTask.CompletedTask; });
        return fixture;

        void Binding<T>(string key) where T : IMessage
        {
            fixture.Setup.Effect(fixture.Services.RegisterMany<IInboundMessageBinding<T>>(new Inbound<T>("other/game")));
            if (missing == key) return;
            fixture.Setup.Effect(fixture.Services.RegisterMany<IInboundMessageBinding<T>>(new Inbound<T>(Protocol)));
            if (duplicate == key)
                fixture.Setup.Effect(fixture.Services.RegisterMany<IInboundMessageBinding<T>>(new Inbound<T>(Protocol)));
        }

        void Sender<T>(string key, Func<T, CancellationToken, ValueTask> send) where T : IMessage
        {
            fixture.Setup.Effect(fixture.Services.RegisterMany<IMessageSender<T>>(new Outbound<T>("other/game",
                (_, _) => throw new InvalidOperationException("A foreign enhancement sender was used."))));
            if (missing == key) return;
            fixture.Setup.Effect(fixture.Services.RegisterMany<IMessageSender<T>>(new Outbound<T>(Protocol, send)));
            if (duplicate == key)
                fixture.Setup.Effect(fixture.Services.RegisterMany<IMessageSender<T>>(new Outbound<T>(Protocol, send)));
        }
    }

    internal PluginContext Articles(Dictionary<string, object?>? overrides = null) =>
        Feature("item-enhancement.articles", typeof(EnhancementArticlesPlugin), false, overrides);

    internal PluginContext Economy(Dictionary<string, object?>? overrides = null) =>
        Feature("item-enhancement", typeof(ItemEnhancementPlugin), true, overrides);

    internal ValueTask<PetInventorySnapshot?> PrepareAsync(string? buildKey = null) =>
        Events.WaterfallAsync<InventoryMetadataPreparing, PetInventorySnapshot?>(new(buildKey ?? Scope.Key, 1),
            _ => ValueTask.FromResult<PetInventorySnapshot?>(null), default);

    internal ValueTask<InventoryReplaced?> PublishAsync(InventoryPublicationPreparing request) =>
        Events.WaterfallAsync<InventoryPublicationPreparing, InventoryReplaced?>(request,
            _ => ValueTask.FromResult<InventoryReplaced?>(null), default);

    internal ValueTask<InventoryItemPresentation> PresentationAsync(InventorySlotRecord row,
        InventoryItemPresentation? previous = null, string? protocol = null) =>
        Events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(new(protocol ?? Protocol, row),
            _ => ValueTask.FromResult(previous ?? InventoryItemPresentation.Empty), default);

    private PluginContext Feature(string id, Type type, bool economy, Dictionary<string, object?>? overrides)
    {
        Dictionary<string, object?> config = new()
        {
            ["enabled"] = true, ["protocol"] = Protocol, ["build_key"] = Scope.Key,
            ["weapon_attribute_id"] = WeaponSelector, ["weapon_buff_id"] = 801,
            ["clothing_attribute_id"] = ClothingSelector, ["clothing_buff_id"] = 802,
        };
        if (!economy) config["initialize_articles"] = true;
        if (economy)
        {
            config["card_item_id"] = 190111;
            config["kit_item_ids"] = new[] { 190106, 190107, 190108, 190109 };
            config["additive_item_id"] = 101745;
            config["success_basis_points"] = 10000;
            config["additive_success_basis_points"] = 10000;
            config["card_quantity"] = 1;
            config["kit_quantity"] = 2;
            config["additive_quantity"] = 1;
        }
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        var context = Context(id, config);
        context.BindServices(type.GetCustomAttribute<PluginAttribute>()!);
        return context;
    }

    internal PluginContext Context(string id, Dictionary<string, object?>? config = null)
    {
        var context = new PluginContext(new() { Id = id, Plugin = id, Source = "enhancement-tests", Config = config },
            Services, Events, NullLogger.Instance);
        _contexts.Add(context);
        return context;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        for (var index = _contexts.Count - 1; index >= 0; index--) await _contexts[index].UnwindAsync();
    }

    internal sealed class MetadataProbe(IInventoryMetadataStore store) : IInventoryMetadataStore
    {
        internal List<InventoryMetadataPreparationCommand> Calls { get; } = [];
        /// <inheritdoc />
        public ValueTask<InventorySnapshot> PrepareInventorySnapshotAsync(InventoryMetadataPreparationCommand command, CancellationToken ct)
        { Calls.Add(command); return store.PrepareInventorySnapshotAsync(command, ct); }
    }

    internal sealed class EnhancementProbe(IItemEnhancementStore store) : IItemEnhancementStore
    {
        internal int Reads { get; private set; }
        internal int Initializations { get; private set; }
        /// <inheritdoc />
        public ValueTask<InventorySnapshot> ReadEnhancementInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
        { Reads++; return store.ReadEnhancementInventoryAsync(userNum, scope, ct); }
        /// <inheritdoc />
        public ValueTask<InventorySnapshot> InitializeArticlesAsync(ArticleInitializationCommand command, CancellationToken ct)
        { Initializations++; return store.InitializeArticlesAsync(command, ct); }
        /// <inheritdoc />
        public ValueTask<InventorySnapshot?> TryEnhanceAsync(ItemEnhancementCommand command, CancellationToken ct) =>
            store.TryEnhanceAsync(command, ct);
        /// <inheritdoc />
        public ValueTask<InventorySnapshot?> SelectArticleAsync(ArticleSelectionCommand command, CancellationToken ct) =>
            store.SelectArticleAsync(command, ct);
    }

    private sealed class BuildScope(InventoryLoadoutScope scope) : IInventoryBuildScope
    {
        /// <inheritdoc />
        public InventoryLoadoutScope Scope => scope;
    }

    private sealed class Inbound<T>(string protocol) : IInboundMessageBinding<T> where T : IMessage
    {
        /// <inheritdoc />
        public string Protocol => protocol;
    }

    private sealed class Outbound<T>(string protocol, Func<T, CancellationToken, ValueTask> send) : IMessageSender<T> where T : IMessage
    {
        /// <inheritdoc />
        public string Protocol => protocol;
        /// <inheritdoc />
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return send(message, ct); }
    }

    internal sealed class TestSession(int owner) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public long Id => owner;
        /// <inheritdoc />
        public IConnection Connection => throw new NotSupportedException();
        /// <inheritdoc />
        public ClientProfile Profile { get; } = new("enhancement-test", "kr", 1256, 0, 0, "Synthetic enhancement tests");
        /// <inheritdoc />
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(owner);
        /// <inheritdoc />
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CatalogData(bool advancement) : IClientDataStore
    {
        /// <inheritdoc />
        public bool Exists(string path) => path is "Config/ShopItem2.xml" or "Config/FavoriteWeaponSystem.xml";

        /// <inheritdoc />
        public byte[] Read(string path)
        {
            if (!Exists(path)) throw new FileNotFoundException("The synthetic enhancement workbook is missing.", path);
            if (path == "Config/FavoriteWeaponSystem.xml") return FavoriteWeaponCatalogTests.Workbook();
            XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
            XElement Sheet(string name, params string[][] rows) => new(ns + "Worksheet", new XAttribute(ns + "Name", name),
                new XElement(ns + "Table", rows.Select(row => new XElement(ns + "Row", row.Select(cell =>
                    new XElement(ns + "Cell", new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), cell)))))));
            var items = new List<string[]>
            {
                new[] { "itemId", "mainCategory", "subCategory", "durationType", "isEnchant", "isUnlimited", "isAdvancedable", "itemTier" },
                new[] { "202020", "Weapons", "Test", "day", "1", "0" },
                new[] { "202021", "Weapons", "Test", "day", "1", "0" },
                new[] { "300201", "Clothes", "Test", "day", "1", "0" },
                // A conversion-only clothing item: covered by neither Enchant nor Modification eligibility.
                new[] { ConversionTarget.ToString(System.Globalization.CultureInfo.InvariantCulture), "Clothes", "Test", "day", "0", "1" },
            };
            items.AddRange(new[] { SetMemberA, SetMemberB }.Select(id =>
                new[] { id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Clothes", "Test", "day", "0", "1" }));
            items.AddRange(new[] { 190111, 190106, 190107, 190108, 190109, 101745, ConversionCard }
                .Select(id => new[] { id.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", "count", "0", "0" }));
            items.AddRange(new[] { 300001, 202001, 201001, 207001 }
                .Select(id => new[] { id.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", "day", "0", "0" }));
            // Clothing members of the grade-up sets, with the client's own class and tier columns; the cards are counted materials.
            (int Class, string Tier)[] grades = [(1, "L3"), (1, "L3"), (2, "AdvL3"), (2, "AdvL3"), (3, "L4"), (3, "L4")];
            if (advancement) items.AddRange(AdvancementMembers.Select((id, index) => new[]
            {
                id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Clothes", "Test", "day", "1", "0",
                grades[index].Class.ToString(System.Globalization.CultureInfo.InvariantCulture), grades[index].Tier,
            }));
            if (advancement) items.AddRange(new[] { HcCard, AdvancedHcCard, L4Card }.Select(id =>
                new[] { id.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", "count", "0", "0", "0", "" }));
            var table = items.Select(row => row.Length == 8 ? row : row.Concat(new[] { "0", "" }).ToArray()).ToArray();
            return Encoding.UTF8.GetBytes(new XElement(ns + "Workbook",
                Sheet("MainCategory", ["id", "name"], ["1", "Weapons"], ["2", "Clothes"]),
                Sheet("SubCategory", ["id", "name"], ["1", "Test"]),
                Sheet("ShopItem", table),
                // A native set of two conversion-only members, and one whose member the unlimited flag does not cover.
                // The row of ConversionSet lists a third id past its setCnt, which the client ignores.
                Sheet("SetItem", ["setId", "setCnt", "ShopItemID01", "ShopItemID02", "ShopItemID03"],
                    [ConversionSet.ToString(System.Globalization.CultureInfo.InvariantCulture), "2", "300203", "300204", "300201"],
                    [PlainSet.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "300201", "", ""],
                    [HcSet.ToString(System.Globalization.CultureInfo.InvariantCulture), "2", "300301", "300302", ""],
                    [AdvancedHcSet.ToString(System.Globalization.CultureInfo.InvariantCulture), "2", "300311", "300312", ""],
                    [L4Set.ToString(System.Globalization.CultureInfo.InvariantCulture), "2", "300321", "300322", ""]),
                Sheet("ItemAttr", ["index", "value", "category", "grade", "weight", "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4"],
                    ["701", "2", "1", "1", "1", "801", "0", "0", "0"],
                    ["702", "2", "2", "1", "1", "802", "0", "0", "0"],
                    // The grade-up attributes: value 0, no weight, one fixed word per buff (kr-1256 ItemAttr 100, 157, 158).
                    ["100", "0", "2", "0", "0", "901", "902", "903", "0"],
                    ["157", "0", "2", "0", "0", "901", "902", "903", "904"],
                    ["158", "0", "2", "0", "0", "901", "902", "903", "904"]),
                Sheet("RandomTable", ["buffIndex", "buffName", "min", "max", "conv"],
                    ["801", "enchantWeapon1st", "0", "0", "0"], ["802", "enchantArmor", "0", "0", "0"],
                    ["901", "reloadCoolTimeRatio", "3", "3", "-0.01"], ["902", "damagedDecRatioMin", "3", "3", "0.01"],
                    ["903", "damagedDecRatioMax", "3", "3", "0.01"], ["904", "remotePangDist", "4", "4", "1"])).ToString());
        }
    }
}
