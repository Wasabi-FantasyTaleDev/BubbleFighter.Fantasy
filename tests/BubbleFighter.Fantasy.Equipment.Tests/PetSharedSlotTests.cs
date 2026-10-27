using BubbleFighter.Fantasy.Tests;
using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Equipment.Data;
using BubbleFighter.Fantasy.Equipment.Selection;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Lobby;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Pins the default-off shared pet slot: the loader reads the workbook's own function
/// column, the policy refuses a second worn pet through the selection's denial path, and
/// a pet never conflicts with ordinary equipment.
/// </summary>
public sealed class PetSharedSlotTests
{
    private const string Fixture = """
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet">
        <Worksheet ss:Name="ShopItem" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet"><Table>
        <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell><Data ss:Type="String">function</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">301001</Data></Cell><Cell><Data ss:Type="Number">10001</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">301002</Data></Cell><Cell><Data ss:Type="Number">10001</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">301003</Data></Cell><Cell><Data ss:Type="Number">10002</Data></Cell></Row>
        <Row><Cell><Data ss:Type="String"># placeholder</Data></Cell><Cell><Data ss:Type="Number">10001</Data></Cell></Row>
        </Table></Worksheet></Workbook>
        """;

    private static WorkbookFile FixtureFile(string xml = Fixture) =>
        WorkbookFile.FromBytes("fixture/ShopItem2.xml", Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void LoaderKeepsOnlyPetFunctionRows()
    {
        var pets = PetItemCatalogLoader.Load(FixtureFile(), 134_217_728, "equipment.max_pet_catalog_bytes");
        Assert.Equal([301001, 301002], pets.Order().ToArray());
    }

    [Theory]
    [InlineData("""
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet">
        <Worksheet ss:Name="ShopItem" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet"><Table>
        <Row><Cell><Data ss:Type="String">itemId</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">301001</Data></Cell></Row>
        </Table></Worksheet></Workbook>
        """, "function")]
    [InlineData("""
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet">
        <Worksheet ss:Name="ShopItem" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet"><Table>
        <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell><Data ss:Type="String">function</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">301001</Data></Cell><Cell><Data ss:Type="String">pet</Data></Cell></Row>
        </Table></Worksheet></Workbook>
        """, "invalid function")]
    [InlineData("""
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet">
        <Worksheet ss:Name="ShopItem" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet"><Table>
        <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell><Data ss:Type="String">function</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">301001</Data></Cell><Cell><Data ss:Type="Number">10001</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">301001</Data></Cell><Cell><Data ss:Type="Number">10001</Data></Cell></Row>
        </Table></Worksheet></Workbook>
        """, "repeats itemId")]
    [InlineData("""
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet">
        <Worksheet ss:Name="ShopItem" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet"><Table>
        <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell><Data ss:Type="String">function</Data></Cell></Row>
        <Row><Cell><Data ss:Type="Number">203073</Data></Cell><Cell><Data ss:Type="Number">0</Data></Cell></Row>
        </Table></Worksheet></Workbook>
        """, "no function-10001")]
    public void LoaderFailsLoudOnSchemaDrift(string xml, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => PetItemCatalogLoader.Load(FixtureFile(xml), 134_217_728, "equipment.max_pet_catalog_bytes"));
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("kr-812", 14, 301014)]
    [InlineData("kr-1256", 26, null)]
    [Trait("Category", "LocalClientData")]
    public void LoaderReadsTheRealWorkbooks(string build, int expectedPets, int? kr812Last)
    {
        var pets = PetItemCatalogLoader.Load(
            WorkbookFile.FromBytes($"Config/ShopItem2.xml", LocalClientData.Bytes(build, "Config/ShopItem2.xml")),
            134_217_728, "equipment.max_pet_catalog_bytes");
        Assert.Equal(expectedPets, pets.Count);
        Assert.Contains(301001, pets);
        Assert.All(pets, id => Assert.True(id >= 301001));
        if (kr812Last is { } last)
            Assert.Contains(last, pets);
    }

    [Fact]
    public async Task FirstPetWearsAndSecondPetIsRefused()
    {
        using var h = await Harness.CreateAsync();
        var first = await h.Selection.ApplyAsync(42, 301001, true, default);
        Assert.NotNull(first);
        Assert.Contains(301001, Worn(first));
        Assert.Null(await h.Selection.ApplyAsync(42, 301002, true, default));
        var worn = Worn(await h.Selection.ReadAsync(42, default));
        Assert.Contains(301001, worn);
        Assert.DoesNotContain(301002, worn);
    }

    [Fact]
    public async Task ConcurrentPetWearsKeepTheSharedSlotExclusive()
    {
        using var h = await Harness.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pause = h.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(async (request, next, ct) =>
        {
            var decision = await next(ct);
            if (request.ItemId == 301001)
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
            return decision;
        });
        var first = h.Selection.ApplyAsync(42, 301001, true, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = h.Selection.ApplyAsync(42, 301002, true, default).AsTask();
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result is not null);
        Assert.Single(Worn(await h.Selection.ReadAsync(42, default)), item => item is 301001 or 301002);
    }

    [Fact]
    public async Task RefusedSecondPetRepliesFailureOnTheWire()
    {
        using var h = await Harness.CreateAsync();
        await h.WearAsync(301001);
        h.Session.Sent.Clear();
        await h.WearAsync(301002);
        Assert.Single(h.Session.Sent);
        Assert.Equal(811, h.Session.Sent[0].Opcode);
        Assert.NotEqual(0, BinaryPrimitives.ReadInt32LittleEndian(h.Session.Sent[0].Payload));
        Assert.DoesNotContain(301002, Worn(await h.Selection.ReadAsync(42, default)));
    }

    [Fact]
    public async Task RemovingASecondPetPassesWhileAnotherIsWorn()
    {
        using var h = await Harness.CreateAsync();
        await h.Selection.ApplyAsync(42, 301001, true, default);
        Assert.NotNull(await h.Selection.ApplyAsync(42, 301002, false, default));
        Assert.Contains(301001, Worn(await h.Selection.ReadAsync(42, default)));
    }

    [Fact]
    public async Task RewearingTheSamePetIsAllowed()
    {
        using var h = await Harness.CreateAsync();
        Assert.NotNull(await h.Selection.ApplyAsync(42, 301001, true, default));
        Assert.NotNull(await h.Selection.ApplyAsync(42, 301001, true, default));
        Assert.Single(Worn(await h.Selection.ReadAsync(42, default)), id => id is >= 301000 and < 302000);
    }

    [Fact]
    public async Task PetAndRegularEquipmentDoNotConflict()
    {
        using var h = await Harness.CreateAsync();
        Assert.NotNull(await h.Selection.ApplyAsync(42, 301001, true, default));
        var worn = await h.Selection.ApplyAsync(42, 203073, true, default);
        Assert.NotNull(worn);
        Assert.Contains(301001, Worn(worn));
        Assert.Contains(203073, Worn(worn));
        Assert.DoesNotContain(202001, Worn(worn));
        // A non-pet wear while a pet is worn never trips the pet rule, and the pet survives
        // the weapon swap the group commit performs.
        var again = await h.Selection.ApplyAsync(42, 204003, true, default);
        Assert.NotNull(again);
        Assert.Contains(301001, Worn(again));
        Assert.DoesNotContain(203073, Worn(again));
    }

    [Fact]
    public async Task RefusalShortCircuitsWhileAllowedWearsKeepTheChain()
    {
        using var h = await Harness.CreateAsync();
        var calls = 0;
        using var chain = h.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>((_, next, ct) =>
        {
            calls++;
            return next(ct);
        });
        Assert.NotNull(await h.Selection.ApplyAsync(42, 301001, true, default));
        Assert.Equal(1, calls);
        Assert.Null(await h.Selection.ApplyAsync(42, 301002, true, default));
        Assert.Equal(1, calls);
        Assert.NotNull(await h.Selection.ApplyAsync(42, 203073, true, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DefaultOffAllowsTwoWornPets()
    {
        using var h = await Harness.CreateAsync(petSharedSlot: false);
        Assert.NotNull(await h.Selection.ApplyAsync(42, 301001, true, default));
        Assert.NotNull(await h.Selection.ApplyAsync(42, 301002, true, default));
        var worn = Worn(await h.Selection.ReadAsync(42, default));
        Assert.Contains(301001, worn);
        Assert.Contains(301002, worn);
    }

    [Fact]
    public async Task ActivationWithoutAClientDataRowFailsLoud()
    {
        var error = await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(petSharedSlot: true, mountClientData: false));
        Assert.Contains("pet_shared_slot", error.Message);
    }

    [Fact]
    public void PetSharedSlotDefaultsToOffAndValidatesItsRequirements()
    {
        var options = new EquipmentOptions();
        Assert.False(options.PetSharedSlot);
        Assert.Equal("Config/ShopItem2.xml", options.PetCatalogClientPath);
        Assert.Equal(134_217_728, options.MaxPetCatalogBytes);
        Assert.Empty(options.Validate());

        var ackOnly = new EquipmentOptions { PetSharedSlot = true, PersistWornState = false };
        Assert.Contains(ackOnly.Validate(), r => r.MemberNames.Contains(nameof(EquipmentOptions.PetSharedSlot)));

        var noPath = new EquipmentOptions { PetSharedSlot = true, PetCatalogClientPath = "" };
        Assert.Contains(noPath.Validate(), r => r.MemberNames.Contains(nameof(EquipmentOptions.PetCatalogClientPath)));

        Assert.Empty(new EquipmentOptions { PetSharedSlot = true }.Validate());
    }

    private static int[] Worn(IReadOnlyList<InventorySlotRecord> rows) =>
        rows.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(row => row.ItemId).ToArray();

    /// <summary>
    /// The real plugin chain of the equipment row over the in-memory store, with the pet
    /// rule's workbook served by a stub client data row.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        internal IEventBus Events => _events;
        private readonly List<PluginContext> _contexts = [];
        internal MemoryInventoryRepository Store { get; } = new(new MemoryPlayerTable(8));
        internal RecordingSession Session { get; } = new(11, 42);
        internal IEquipmentSelection Selection => _services.Get<IEquipmentSelection>();
        internal IPacketRouter Router => _services.Get<IPacketRouter>();

        private PluginContext Context(string name, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(
                name == "dialect.messages" ? SharedDialectRows.Kr1256(config) : new PluginRow { Id = name, Plugin = name, Source = "test", Config = config },
                _services, _events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }

        internal static async Task<Harness> CreateAsync(bool petSharedSlot = true, bool mountClientData = true)
        {
            var h = new Harness();
            var root = h.Context("root");
            root.Effect(h._services.Register<IInventoryRepository>(h.Store));
            if (mountClientData)
                root.Effect(h._services.Register<IClientDataStore>(new FixtureClientDataStore(Encoding.UTF8.GetBytes(Fixture))));
            await new PacketRouterPlugin().ActivateAsync(h.Context("router"), default);
            await new OpcodeTableRegistryPlugin().ActivateAsync(h.Context("tables"), default);
            root.Effect(h._services.Get<IOpcodeTableRegistry>().Register(EquipmentOpcodeTests.LoadTable()));
            await new MessageRouterPlugin().ActivateAsync(h.Context("core.message-router"), default);
            await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(
                h.Context("dialect.messages", new() { ["inventory"] = true }), default);
            await new BubbleFighter.Fantasy.InventoryView.InventoryViewPlugin().ActivateAsync(h.Context("inventoryview", new()
            {
                ["enabled"] = true, ["publish_equipped_view_push"] = true, ["item_locations_path"] = "",
            }), default);
            root.Effect(h._services.Register<IInventoryPushService>(new InventoryPush(
                new LoginInventoryResolver(h.Store, true, h._services.Get<IEquipmentSelection>()),
                h._services.GetAll<IMessageSender<InventoryReplaced>>().Single(),
                h._services.GetAll<IMessageSender<InventoryPurchaseApplied>>().Single(),
                h._services.GetAll<IMessageSender<InventorySlotsUpdated>>().Single())));
            await new EquipmentPlugin().ActivateAsync(
                h.Context("equipment", petSharedSlot ? new() { ["pet_shared_slot"] = true } : null), default);
            await h.Store.ReplaceSlotsAsync(42, InventoryBag.CharacterBag, StarterInventory.Slots(42, InventoryBag.CharacterBag, DateTimeOffset.UtcNow), default);
            await h.Store.ReplaceSlotsAsync(42, InventoryBag.EquippedBag, StarterInventory.Slots(42, InventoryBag.EquippedBag, DateTimeOffset.UtcNow), default);
            await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
                [new(42, InventoryBag.CharacterBag, 776, 203073, 7, 0),
                 new(42, InventoryBag.CharacterBag, 777, 301001, 7, 0),
                 new(42, InventoryBag.CharacterBag, 778, 301002, 0, 0)], default);
            return h;
        }

        internal Task WearAsync(int id) =>
            Router.DispatchAsync("kr-1256/game", Session, new(804, BitConverter.GetBytes((long)id)), default).AsTask();

        public void Dispose()
        {
            for (var i = _contexts.Count - 1; i >= 0; i--)
                _contexts[i].Unwind();
        }
    }

    /// <summary>Serves one workbook bytes for its client path; any other path fails like the real store.</summary>
    private sealed class FixtureClientDataStore(byte[] workbook) : IClientDataStore
    {
        public bool Exists(string path) => path == "Config/ShopItem2.xml";

        public byte[] Read(string path) =>
            path == "Config/ShopItem2.xml" ? workbook : throw new FileNotFoundException("No such client file.", path);
    }
}
