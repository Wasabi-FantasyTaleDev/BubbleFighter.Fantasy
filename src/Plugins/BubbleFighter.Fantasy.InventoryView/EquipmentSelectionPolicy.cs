using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Applies the same precommit decision to client and service initiated equipment changes.</summary>
internal sealed class EquipmentSelectionPolicy(IEquipmentSelection inner, IEventBus events, string protocol, ILogger logger) : IEquipmentSelection
{
    private readonly SemaphoreSlim _selectionGate = new(1, 1);

    public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) => inner.ReadAsync(userNum, ct);

    /// <summary>Applies shared equipment policy without allowing a policy to replace an authorized Article target.</summary>
    public async ValueTask<InventorySnapshot?> ApplyArticleAsync(long userNum, InventorySlotRecord expectedTarget, bool worn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (expectedTarget.UserNum != userNum || InventoryArticleIdentityResolver.Of(expectedTarget) is null)
            throw new ArgumentException("Article selection requires the authenticated owner's resolved row.", nameof(expectedTarget));
        await _selectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var decision = await events.WaterfallAsync(new EquipmentSelectionApplying(protocol, userNum, expectedTarget.ItemId, worn),
                _ => ValueTask.FromResult(new EquipmentSelectionDecision(true, expectedTarget.ItemId, worn)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decision is null) throw new InvalidOperationException("An equipment policy returned no decision.");
            if (!decision.Allowed || decision.ItemId != expectedTarget.ItemId) return null;
            return await inner.ApplyArticleAsync(userNum, expectedTarget, decision.Worn, ct).ConfigureAwait(false);
        }
        finally { _selectionGate.Release(); }
    }

    /// <summary>Applies equipment policy without transferring an exact ordinary ownership precondition to another item.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplyOwnedSnapshotAsync(long userNum, InventorySlotRecord expectedTarget, bool worn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (expectedTarget.UserNum != userNum || expectedTarget.Bag != InventoryBag.CharacterBag)
            throw new ArgumentException("Owned selection requires the authenticated owner's character-bag row.", nameof(expectedTarget));
        await _selectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var decision = await events.WaterfallAsync(new EquipmentSelectionApplying(protocol, userNum, expectedTarget.ItemId, worn),
                _ => ValueTask.FromResult(new EquipmentSelectionDecision(true, expectedTarget.ItemId, worn)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decision is null) throw new InvalidOperationException("An equipment policy returned no decision.");
            if (!decision.Allowed || decision.ItemId != expectedTarget.ItemId) return null;
            return await inner.ApplyOwnedSnapshotAsync(userNum, expectedTarget, decision.Worn, ct).ConfigureAwait(false);
        }
        finally { _selectionGate.Release(); }
    }

    /// <summary>Serializes policy evaluation with the item selection commit and returns its complete inventory snapshot when allowed.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplySnapshotAsync(long userNum, int itemId, bool worn, CancellationToken ct)
    {
        await _selectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var decision = await events.WaterfallAsync(new EquipmentSelectionApplying(protocol, userNum, itemId, worn),
                _ => ValueTask.FromResult(new EquipmentSelectionDecision(true, itemId, worn)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decision is null) throw new InvalidOperationException("An equipment policy returned no decision.");
            if (!decision.Allowed) return null;
            if (decision.ItemId <= 0) throw new InvalidOperationException("An equipment policy allowed an invalid item id.");
            return await inner.ApplySnapshotAsync(userNum, decision.ItemId, decision.Worn, ct).ConfigureAwait(false);
        }
        finally { _selectionGate.Release(); }
    }

    /// <summary>Applies equipment policy to a resolved Article while refusing any policy retargeting that would change its authorized item.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplyPetAsync(long userNum, PetEquipmentTarget target, bool worn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        await _selectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var decision = await events.WaterfallAsync(new EquipmentSelectionApplying(protocol, userNum, target.ItemId, worn),
                _ => ValueTask.FromResult(new EquipmentSelectionDecision(true, target.ItemId, worn)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decision is null) throw new InvalidOperationException("An equipment policy returned no decision.");
            // An Article precondition authorizes one item; policy retargeting cannot transfer that ownership.
            if (!decision.Allowed || decision.ItemId != target.ItemId) return null;
            return await inner.ApplyPetAsync(userNum, target, decision.Worn, ct).ConfigureAwait(false);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct)
    {
        // Policies inspect the state that the commit replaces; keep that inspection and
        // persistence in one critical section so concurrent requests cannot both pass.
        await _selectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var request = new EquipmentSelectionApplying(protocol, userNum, itemId, worn);
            var decision = await events.WaterfallAsync(request,
                _ => ValueTask.FromResult(new EquipmentSelectionDecision(true, itemId, worn)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decision is null) throw new InvalidOperationException("An equipment policy returned no decision.");
            if (!decision.Allowed)
            {
                logger.LogInformation("Equipment selection for player {User} on {Protocol} was refused before persistence: {Reason}", userNum, protocol, decision.Reason);
                return null;
            }
            if (decision.ItemId <= 0) throw new InvalidOperationException("An equipment policy allowed an invalid item id.");
            return await inner.ApplyAsync(userNum, decision.ItemId, decision.Worn, ct).ConfigureAwait(false);
        }
        finally
        {
            _selectionGate.Release();
        }
    }
}
