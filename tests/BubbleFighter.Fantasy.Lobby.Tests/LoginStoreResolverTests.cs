using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// The store-backed login resolvers every build shares: first-login seeding, the additive starter top-up, the
/// configured starting balances, and the <see cref="LobbyOptions"/> source and starter-kit validation.
/// </summary>
public sealed class LoginStoreResolverTests
{
    private const long User = 42;
    private const long StoredKey = 0x1122334455667788L;

    private static MemoryInventoryRepository NewStore() => new(new MemoryPlayerTable(8));

    private static async Task<IReadOnlyList<InventorySlotRecord>> Bag(IInventoryRepository store, InventoryBag bag) =>
        await store.ListSlotsAsync(User, bag, InventoryLimits.MaxInventorySlotsPerBag, default);

    [Fact]
    public async Task FirstLoginSeedsTheStarterBagIntoBothBagsAndReplaysIt()
    {
        var store = NewStore();
        var resolver = new LoginInventoryResolver(store, seedStarterInventory: true);
        var count = StarterKit.Default.ItemIds.Count;

        var first = await resolver.ResolveAsync(User, default);

        Assert.Equal(count, first.CharacterBag.Count);
        Assert.Equal(count, first.EquippedBag.Count);
        Assert.Equal(count, (await Bag(store, InventoryBag.CharacterBag)).Count);
        var second = await resolver.ResolveAsync(User, default);
        Assert.Equal(first.CharacterBag, second.CharacterBag);
        Assert.Equal(first.EquippedBag, second.EquippedBag);
    }

    [Fact]
    public async Task AStoredRowSurvivesInPlaceAndOnlyMissingStarterItemsAreAppended()
    {
        var store = NewStore();
        await store.ReplaceSlotsAsync(User, InventoryBag.CharacterBag,
            [new InventorySlotRecord(User, InventoryBag.CharacterBag, StoredKey, 201001, 99999, 0)], default);
        var resolver = new LoginInventoryResolver(store, seedStarterInventory: true);

        var snapshot = await resolver.ResolveAsync(User, default);

        var kept = snapshot.CharacterBag[0];
        Assert.Equal((StoredKey, 201001, 99999), (kept.SlotKey, kept.ItemId, kept.Quantity));
        var ids = snapshot.CharacterBag.Select(row => row.ItemId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(StarterKit.Default.ItemIds, id => Assert.Contains(id, ids));
        Assert.Equal(snapshot.CharacterBag.Count, (await resolver.ResolveAsync(User, default)).CharacterBag.Count);
    }

    [Fact]
    public async Task SeedingDisabledLeavesAnEmptyBag()
    {
        var store = NewStore();
        var resolver = new LoginInventoryResolver(store, seedStarterInventory: false);

        Assert.Empty((await resolver.ResolveAsync(User, default)).CharacterBag);
        Assert.Empty(await Bag(store, InventoryBag.CharacterBag));
    }

    [Fact]
    public async Task AConfiguredSubsetKitSeedsOnlyItsIdsAsPermanentWithItsWornFlags()
    {
        // cn-324's ShopItem table carries six of the kr-812 starter ids (packets/builds.yaml).
        var kit = new StarterKit([300001, 201001, 202001, 203001, 204001, 207001], [300001, 202001, 201001, 207001]);
        var resolver = new LoginInventoryResolver(NewStore(), seedStarterInventory: true, starter: kit);

        var snapshot = await resolver.ResolveAsync(User, default);

        Assert.Equal([300001, 201001, 202001, 203001, 204001, 207001], snapshot.CharacterBag.Select(row => row.ItemId));
        Assert.All(snapshot.CharacterBag, row => Assert.Equal(InventoryLifetimeKind.Permanent, row.Lifetime.Kind));
        Assert.All(snapshot.CharacterBag, row =>
            Assert.Equal(row.ItemId is 300001 or 202001 or 201001 or 207001, (row.Flags & InventorySlotRecord.WornFlagBit) != 0));
        Assert.Equal(6, (await resolver.ResolveAsync(User, default)).EquippedBag.Count);
    }

    [Fact]
    public async Task ASubsetKitTopsUpOnlyItsOwnIdsAndLeavesNewRowsUnworn()
    {
        var store = NewStore();
        await store.ReplaceSlotsAsync(User, InventoryBag.CharacterBag,
            [new InventorySlotRecord(User, InventoryBag.CharacterBag, StoredKey, 300001, 99999, 0)], default);
        var resolver = new LoginInventoryResolver(store, seedStarterInventory: true, starter: new StarterKit([300001, 202001], [300001]));

        var snapshot = await resolver.ResolveAsync(User, default);

        Assert.Equal([300001, 202001], snapshot.CharacterBag.Select(row => row.ItemId));
        Assert.Equal((byte)0, snapshot.CharacterBag[^1].Flags);
    }

    [Fact]
    public async Task MoneySeedsTheConfiguredStartingBalancesOnceAndThenReplaysTheStoredWallet()
    {
        var funded = new LoginMoneyResolver(new Wallets(), 100_000, 500);
        var seeded = await funded.ResolveAsync(User, default);
        Assert.Equal((100_000, 500), (seeded.Lucci, seeded.Cash));

        var store = new Wallets();
        var zero = new LoginMoneyResolver(store, 0, 0);
        Assert.Equal((0, 0), ((await zero.ResolveAsync(User, default)).Lucci, (await zero.ResolveAsync(User, default)).Cash));
        await store.UpsertAsync(new PlayerWallet(User, 55, 66), default);
        var stored = await zero.ResolveAsync(User, default);
        Assert.Equal((55, 66), (stored.Lucci, stored.Cash));
    }

    [Fact]
    public void SourceOptionsDefaultToReferenceAndRejectUnknownValues()
    {
        var options = new LobbyOptions();
        Assert.Equal(("reference", "reference"), (options.InventorySource, options.MoneySource));
        Assert.True(options.SeedStarterInventory);
        Assert.Empty(options.Validate());
        Assert.Empty(new LobbyOptions { InventorySource = "mysql", MoneySource = "mysql" }.Validate());

        var failures = new LobbyOptions { InventorySource = "mysql ", MoneySource = "redis" }.Validate().ToList();
        Assert.Contains(failures, f => f.MemberNames.Contains(nameof(LobbyOptions.InventorySource)));
        Assert.Contains(failures, f => f.MemberNames.Contains(nameof(LobbyOptions.MoneySource)));
    }

    [Fact]
    public void StarterKitsRejectUnseedableSets()
    {
        Assert.Throws<ArgumentException>(() => new StarterKit([], []));
        Assert.Throws<ArgumentException>(() => new StarterKit(
            Enumerable.Range(1, InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(), []));
        Assert.Throws<ArgumentException>(() => new StarterKit([300001, 0], [300001]));
        Assert.Throws<ArgumentException>(() => new StarterKit([300001, 300001], [300001]));
        Assert.Throws<ArgumentException>(() => new StarterKit([300001], [202001]));
        Assert.Throws<ArgumentException>(() => new StarterKit([300001, 202001], [300001, 300001]));

        Assert.Contains(new LobbyOptions { StarterItemIds = [] }.Validate(), f => f.MemberNames.Contains(nameof(LobbyOptions.StarterItemIds)));
        Assert.Contains(new LobbyOptions { StarterItemIds = [300001, -1] }.Validate(), f => f.MemberNames.Contains(nameof(LobbyOptions.StarterItemIds)));
        Assert.Contains(new LobbyOptions { StarterItemIds = [300001, 300001] }.Validate(), f => f.MemberNames.Contains(nameof(LobbyOptions.StarterItemIds)));
        Assert.Contains(new LobbyOptions { StarterItemIds = [300001], StarterWornIds = [202001] }.Validate(),
            f => f.MemberNames.Contains(nameof(LobbyOptions.StarterWornIds)));
        var kit = new LobbyOptions { StarterItemIds = [300001, 202001], StarterWornIds = [300001, 202001] };
        Assert.Empty(kit.Validate());
        Assert.Equal([300001, 202001], kit.BuildStarterKit().WornItemIds);
    }

    [Fact]
    public void DataAnnotationsStillRequireProtocolAndFallback()
    {
        var options = new LobbyOptions { Protocol = "", FallbackUsername = "" };
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true));
    }

    private sealed class Wallets : IPlayerWalletRepository
    {
        private PlayerWallet? _stored;

        public ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(_stored?.UserNum == userNum ? _stored : null);

        public ValueTask UpsertAsync(PlayerWallet wallet, CancellationToken ct)
        {
            _stored = wallet;
            return ValueTask.CompletedTask;
        }
    }
}
