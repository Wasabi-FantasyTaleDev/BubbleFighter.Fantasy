using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>Checks exact ordinary ownership through the real equipment policy, group resolution and inventory transaction.</summary>
public sealed class OwnedEquipmentSelectionTests
{
    /// <summary>Title-style group contributions replace the previous title in both bags without requiring another feature provider.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedSelectionAppliesExtraGroupsAndReturnsBothCommittedBags(bool withPolicy)
    {
        using var rig = await Rig.CreateAsync();
        var before = await rig.SnapshotAsync();
        var target = before.CharacterBag.Single(row => row.ItemId == 400909);
        Assert.Null(target.ArticleIdentity);
        Assert.Null(target.PetIdentity);
        IEquipmentSelection selection = withPolicy ? rig.Policy : rig.Inner;
        var committed = await selection.ApplyOwnedSnapshotAsync(1, target, true, default);

        Assert.NotNull(committed);
        Assert.Equal(before.CharacterBag.Count, committed.CharacterBag.Count);
        Assert.Equal(before.EquippedBag.Count, committed.EquippedBag.Count);
        foreach (var bag in new[] { committed.CharacterBag, committed.EquippedBag })
        {
            Assert.Equal(0x40, bag.Single(row => row.ItemId == 400901).Flags);
            Assert.Equal(0x41, bag.Single(row => row.ItemId == 400909).Flags);
            Assert.Equal(0x41, bag.Single(row => row.ItemId == 400905).Flags);
            Assert.Equal(400909, Assert.Single(bag, row => (row.ItemId is 400901 or 400909)
                && (row.Flags & InventorySlotRecord.WornFlagBit) != 0).ItemId);
            Assert.All(bag, row => Assert.Null(row.ArticleIdentity));
        }
        var saved = await rig.SnapshotAsync();
        Assert.Equal(committed.CharacterBag, saved.CharacterBag);
        Assert.Equal(committed.EquippedBag, saved.EquippedBag);
    }

    /// <summary>A policy cannot transfer authorization to another item, even when that other item is also owned.</summary>
    [Fact]
    public async Task PolicyCannotRetargetTheAuthorizedOwnedRow()
    {
        using var rig = await Rig.CreateAsync();
        var before = await rig.SnapshotAsync();
        var target = before.CharacterBag.Single(row => row.ItemId == 400909);
        using var policy = rig.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>((request, _, _) =>
        {
            Assert.Equal(Rig.Protocol, request.Protocol);
            Assert.Equal(1, request.UserNum);
            Assert.Equal(target.ItemId, request.ItemId);
            return ValueTask.FromResult(new EquipmentSelectionDecision(true, 400901, false));
        });

        Assert.Null(await rig.Policy.ApplyOwnedSnapshotAsync(1, target, true, default));
        var saved = await rig.SnapshotAsync();
        Assert.Equal(before.CharacterBag, saved.CharacterBag);
        Assert.Equal(before.EquippedBag, saved.EquippedBag);
    }

    /// <summary>Ownership changed by an awaited policy is checked again in the store before any worn state is committed.</summary>
    [Theory]
    [InlineData("replacement")]
    [InlineData("quantity")]
    [InlineData("expiry")]
    public async Task PolicyOwnershipChangesRefuseTheStaleSelection(string change)
    {
        using var rig = await Rig.CreateAsync();
        var target = (await rig.SnapshotAsync()).CharacterBag.Single(row => row.ItemId == 400909);
        InventorySnapshot? afterPolicy = null;
        using var policy = rig.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(async (_, next, ct) =>
        {
            var expired = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddMinutes(-1));
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var rows = await rig.Store.ListSlotsAsync(1, bag, InventoryLimits.MaxInventorySlotsPerBag, ct);
                await rig.Store.ReplaceSlotsAsync(1, bag, rows.Select(row => row.ItemId != target.ItemId ? row : change switch
                {
                    "replacement" => row with { SlotKey = row.SlotKey + 1000 },
                    "quantity" => row with { Quantity = 0 },
                    _ => row with { Lifetime = expired },
                }).ToArray(), ct);
            }
            afterPolicy = await rig.SnapshotAsync();
            return await next(ct);
        });

        Assert.Null(await rig.Policy.ApplyOwnedSnapshotAsync(1, target, true, default));
        Assert.NotNull(afterPolicy);
        var saved = await rig.SnapshotAsync();
        Assert.Equal(afterPolicy.CharacterBag, saved.CharacterBag);
        Assert.Equal(afterPolicy.EquippedBag, saved.EquippedBag);
        Assert.Equal(0x41, saved.CharacterBag.Single(row => row.ItemId == 400901).Flags);
        Assert.Equal(0x40, saved.CharacterBag.Single(row => row.ItemId == 400909).Flags);
    }

    /// <summary>Exact ownership is validated before this same transaction initializes the selected row and other eligible Articles.</summary>
    [Fact]
    public async Task ExactTargetAndMetadataInitializationCommitTogether()
    {
        using var rig = await Rig.CreateAsync(withMetadata: true);
        var target = (await rig.SnapshotAsync()).CharacterBag.Single(row => row.ItemId == 202020);
        Assert.Null(target.ArticleIdentity);
        var committed = await rig.Policy.ApplyOwnedSnapshotAsync(1, target, true, default);

        Assert.NotNull(committed);
        var command = Assert.Single(rig.Articles!.Commands);
        Assert.Equal(1, command.UserNum);
        Assert.Same(rig.Scope, command.Scope);
        Assert.Equal(new[] { 202020, 400909 }, command.EligibleItemIds);
        var weapon = committed.CharacterBag.Single(row => row.ItemId == 202020);
        var title = committed.CharacterBag.Single(row => row.ItemId == 400909);
        Assert.NotNull(weapon.ArticleIdentity);
        Assert.NotNull(title.ArticleIdentity);
        Assert.NotEqual(weapon.ArticleIdentity, title.ArticleIdentity);
        Assert.Null(target.ArticleIdentity);
        foreach (var bag in new[] { committed.CharacterBag, committed.EquippedBag })
        {
            Assert.Equal(0x41, bag.Single(row => row.ItemId == 202020).Flags);
            Assert.Equal(0x40, bag.Single(row => row.ItemId == 202001).Flags);
            Assert.Equal(weapon.ArticleIdentity, bag.Single(row => row.ItemId == 202020).ArticleIdentity);
            Assert.Equal(title.ArticleIdentity, bag.Single(row => row.ItemId == 400909).ArticleIdentity);
        }
        var saved = await rig.SnapshotAsync();
        Assert.Equal(committed.CharacterBag, saved.CharacterBag);
        Assert.Equal(committed.EquippedBag, saved.EquippedBag);
    }

    private sealed class Rig : IDisposable
    {
        internal const string Protocol = "owned-selection/game";
        private readonly IDisposable _titleGroup;
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal MemoryInventoryRepository Store { get; } = new(new(2));
        internal InventoryLoadoutScope Scope { get; } = new("owned-selection",
            [300001, 201001, 202001, 207001, 202020, 400901, 400905, 400909]);
        internal EquipmentSelection Inner { get; }
        internal EquipmentSelectionPolicy Policy { get; }
        internal InitializationInputs? Articles { get; }

        private Rig(bool withMetadata)
        {
            var titleGroup = new InventoryWearGroup(400901, 400909, 400901) { ItemIds = Array.AsReadOnly(new[] { 400901, 400909 }) };
            _titleGroup = Events.Intercept<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>(async (request, next, ct) =>
            {
                var previous = await next(ct);
                return request.Protocol == Protocol ? Array.AsReadOnly(previous.Append(titleGroup).ToArray()) : previous;
            });
            Articles = withMetadata ? new(Scope) : null;
            Inner = new(Store, contributions: new(Events, Protocol), scope: Scope, articleResolver: Articles, loadouts: Store);
            Policy = new(Inner, Events, Protocol, NullLogger.Instance);
        }

        internal static async Task<Rig> CreateAsync(bool withMetadata = false)
        {
            var rig = new Rig(withMetadata);
            var rows = rig.Scope.SupportedItemIds.Select((id, index) =>
                new InventorySlotRecord(1, InventoryBag.CharacterBag, index + 1, id, 1,
                    id is 202020 or 400909 ? (byte)0x40 : (byte)0x41) { Lifetime = InventoryLifetime.Permanent }).ToArray();
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag,
                rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
            return rig;
        }

        internal ValueTask<InventorySnapshot> SnapshotAsync() => Store.ReadEnhancementInventoryAsync(1, Scope, default);

        /// <inheritdoc />
        public void Dispose() => _titleGroup.Dispose();
    }

    private sealed class InitializationInputs(InventoryLoadoutScope scope) : IEquipmentArticleResolver
    {
        internal List<ArticleInitializationCommand> Commands { get; } = [];

        /// <inheritdoc />
        public ArticleInitializationCommand CreateInitialization(long userNum, DateTimeOffset now)
        {
            var command = new ArticleInitializationCommand(userNum, scope, [202020, 400909], now);
            Commands.Add(command);
            return command;
        }

        /// <inheritdoc />
        public ValueTask<InventorySlotRecord?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct) =>
            throw new NotSupportedException("Owned selection already has its authorized row and must not resolve a different Article.");
    }
}
