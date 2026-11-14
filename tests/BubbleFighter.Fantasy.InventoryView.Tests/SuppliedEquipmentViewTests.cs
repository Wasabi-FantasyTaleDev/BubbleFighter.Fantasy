using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>Checks that scene-supplied appearances remain authoritative across asynchronous publication.</summary>
public sealed class SuppliedEquipmentViewTests
{
    /// <summary>Local and remote publications copy native words and IDs before yielding and never consult mutable inventory.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppliedAppearanceOwnsItsSnapshotWithoutInventoryReads(bool remote)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        var events = new EventBus(NullLogger.Instance);
        var publications = new List<EquipmentViewPublished>();
        using var subscription = events.OnAsync<EquipmentViewPublished>((message, _) =>
        {
            publications.Add(message);
            return ValueTask.CompletedTask;
        });
        var store = new ForbiddenStorage();
        var push = new EquippedViewPush(NullLogger.Instance, sender, sender, selection: store, inventory: store, events: events);
        int[] ids = [300001, 203100];
        uint[] words = [4294965296u, 0x3F800000u, 0, 7];
        var request = new EquipmentViewRequested(sender.Protocol, new RecordingSession(42), 42, 300001, ids, remote)
        {
            ItemAttributes = [new(203100, 7, words, uint.MaxValue, "A")],
        };
        var pending = push.SendSuppliedAsync(request, default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(publications);
            ids[1] = 203101;
            words[0] = 0;
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }
        var receivedIds = remote ? Assert.Single(sender.Remote).EquippedItemIds : Assert.Single(sender.Local).EquippedItemIds;
        var received = remote ? sender.Remote[0].ItemAttributes : sender.Local[0].ItemAttributes;
        Assert.Equal(new[] { 300001, 203100 }, receivedIds);
        var attribute = Assert.Single(received);
        Assert.Equal((203100, 7u, uint.MaxValue, "A"), (attribute.ItemId, attribute.Kind, attribute.Unknown28, attribute.Unknown32));
        Assert.Equal(new uint[] { 4294965296u, 0x3F800000u, 0, 7 }, attribute.Values);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)attribute.Values)[0] = 1);
        if (remote) Assert.Empty(publications);
        else
        {
            var publication = Assert.Single(publications);
            Assert.Same(sender.Local[0], publication.View);
            Assert.True(publication.IsSceneReplay);
            Assert.False(publication.SelectionChange);
        }
    }

    /// <summary>An explicit empty snapshot clears modifiers without falling back to the repository.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitEmptyModifiersArePublishedAsAClear(bool remote)
    {
        var sender = new Sender(_ => ValueTask.CompletedTask);
        var events = new EventBus(NullLogger.Instance);
        var store = new ForbiddenStorage();
        var push = new EquippedViewPush(NullLogger.Instance, sender, sender, selection: store, inventory: store, events: events);
        await push.SendSuppliedAsync(new(sender.Protocol, new RecordingSession(42), 42, 300001, [300001, 203100], remote)
            { ItemAttributes = [] }, default);
        Assert.Empty(remote ? Assert.Single(sender.Remote).ItemAttributes : Assert.Single(sender.Local).ItemAttributes);
    }

    /// <summary>A committed bag publishes its character first while retaining the other item order and frozen pet attributes.</summary>
    [Fact]
    public async Task CommittedBagMovesOnlyItsPresentCharacterBeforeOtherWornItems()
    {
        var pet = PetProgressions.Empty.With("kr-812", new(1, 23, DateTimeOffset.UnixEpoch, null, new(8, 1, 2, 3, 4), null));
        InventorySlotRecord[] bag =
        [
            new(42, InventoryBag.CharacterBag, 1, 202001, 1, InventorySlotRecord.WornFlagBit),
            new(42, InventoryBag.CharacterBag, 2, 301001, 1, InventorySlotRecord.WornFlagBit) { Pet = pet },
            new(42, InventoryBag.CharacterBag, 3, 300001, 1, InventorySlotRecord.WornFlagBit),
            new(42, InventoryBag.CharacterBag, 4, 201001, 1, InventorySlotRecord.WornFlagBit),
            new(42, InventoryBag.CharacterBag, 5, 205001, 1, InventorySlotRecord.WornFlagBit),
            new(42, InventoryBag.CharacterBag, 6, 203001, 1, 0),
        ];
        var sender = new Sender(_ => ValueTask.CompletedTask);
        var events = new EventBus(NullLogger.Instance);
        using var presentation = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
        {
            if (request.Slot.ItemId == 202001)
                bag[1] = bag[1] with { Pet = pet.With("kr-812", new(1, 99, DateTimeOffset.UnixEpoch, null, new(8, 9, 9, 9, 9), null)) };
            var progress = request.Slot.Pet?.Find("kr-812");
            return ValueTask.FromResult(progress is null ? InventoryItemPresentation.Empty
                : new InventoryItemPresentation([new(request.Slot.ItemId, 7, [(uint)progress.Grade, (uint)progress.Experience, 0, 0], 0, "")], null));
        });
        var store = new ForbiddenStorage();
        var push = new EquippedViewPush(NullLogger.Instance, sender, sender, selection: store, inventory: store, events: events);
        await push.SendSnapshotAsync(new RecordingSession(42), 42, 300001, bag, default);
        var view = Assert.Single(sender.Local);
        Assert.Equal(300001, view.CharacterId);
        Assert.Equal(new[] { 300001, 202001, 301001, 201001, 205001 }, view.EquippedItemIds);
        Assert.Equal(new[] { 202001, 201001, 205001, 0, 0 }, WeaponPresetSlots.FromWorn(view.EquippedItemIds));
        Assert.Equal((202001, 205001), (view.PrimaryWeaponId, view.SpecialWeaponId));
        Assert.Equal(new uint[] { 1, 23, 0, 0 }, Assert.Single(view.ItemAttributes).Values);
        Assert.Equal(99, bag[1].Pet!.Find("kr-812")!.Experience);
        Assert.Equal(new[] { 202001, 301001, 300001, 201001, 205001, 203001 }, bag.Select(row => row.ItemId));
    }

    /// <summary>Both snapshot implementations move only a present positive character and never synthesize a missing identity.</summary>
    [Theory]
    [InlineData(300001, false)]
    [InlineData(300001, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(300027, false)]
    [InlineData(300027, true)]
    public async Task SnapshotCompatibilityNeverInsertsAMissingCharacter(int characterId, bool compatibility)
    {
        InventorySlotRecord[] bag =
        [
            new(42, InventoryBag.CharacterBag, 1, 202001, 1, InventorySlotRecord.WornFlagBit),
            new(42, InventoryBag.CharacterBag, 2, 300001, 1, InventorySlotRecord.WornFlagBit),
            new(42, InventoryBag.CharacterBag, 3, 201001, 1, InventorySlotRecord.WornFlagBit),
        ];
        var sender = new Sender(_ => ValueTask.CompletedTask);
        var fallback = new SelectionOnlyPush();
        IEquippedViewPush push = compatibility ? fallback : new EquippedViewPush(NullLogger.Instance, sender, sender);
        await push.SendSnapshotAsync(new RecordingSession(42), 42, characterId, bag, default);
        var ids = compatibility ? fallback.Items! : Assert.Single(sender.Local).EquippedItemIds;
        Assert.Equal(characterId, compatibility ? fallback.CharacterId : sender.Local[0].CharacterId);
        Assert.Equal(characterId == 300001 ? new[] { 300001, 202001, 201001 } : new[] { 202001, 300001, 201001 }, ids);
        Assert.DoesNotContain(0, ids);
        Assert.DoesNotContain(300027, ids);
    }

    private sealed class SelectionOnlyPush : IEquippedViewPush
    {
        internal int CharacterId { get; private set; }
        internal IReadOnlyList<int>? Items { get; private set; }
        /// <summary>Records the character and item IDs delivered through the compatibility equipment-push method.</summary>
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            CharacterId = characterId;
            Items = equippedIds;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Sender(Func<CancellationToken, ValueTask> beforeSend)
        : IMessageSender<LocalEquipmentViewUpdated>, IMessageSender<RemoteEquipmentViewUpdated>
    {
        internal List<LocalEquipmentViewUpdated> Local { get; } = [];
        internal List<RemoteEquipmentViewUpdated> Remote { get; } = [];
        /// <summary>Gets the protocol shared by the fixture's local and remote appearance senders.</summary>
        public string Protocol => "test/game";
        /// <summary>Runs the test's publication hook before recording a local equipment snapshot.</summary>
        public async ValueTask SendAsync(ISession session, LocalEquipmentViewUpdated message, CancellationToken ct)
        {
            await beforeSend(ct);
            Local.Add(message);
        }
        /// <summary>Runs the test's publication hook before recording a remote equipment snapshot.</summary>
        public async ValueTask SendAsync(ISession session, RemoteEquipmentViewUpdated message, CancellationToken ct)
        {
            await beforeSend(ct);
            Remote.Add(message);
        }
    }

    private sealed class ForbiddenStorage : IInventoryRepository, IEquipmentSelection
    {
        /// <summary>Rejects normalization because a supplied appearance already carries its authoritative selection.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
            throw new InvalidOperationException("A supplied appearance must not normalize inventory.");
        /// <summary>Rejects selection changes while replaying a supplied appearance.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("A supplied appearance must not change inventory.");
        /// <summary>Rejects inventory rereads that could replace the supplied appearance with later state.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct) =>
            throw new InvalidOperationException("A supplied appearance must not reread inventory.");
        /// <summary>Rejects ownership replacement during appearance publication.</summary>
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            throw new InvalidOperationException("A supplied appearance must not replace inventory.");
        /// <summary>Rejects worn-flag writes during appearance publication.</summary>
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("A supplied appearance must not change worn flags.");
    }
}
