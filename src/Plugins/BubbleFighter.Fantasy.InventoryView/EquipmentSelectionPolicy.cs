using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Applies the same precommit decision to client and service initiated equipment changes.</summary>
internal sealed class EquipmentSelectionPolicy(IEquipmentSelection inner, IEventBus events, string protocol, ILogger logger) : IEquipmentSelection
{
    private readonly SemaphoreSlim _selectionGate = new(1, 1);

    public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) => inner.ReadAsync(userNum, ct);

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
