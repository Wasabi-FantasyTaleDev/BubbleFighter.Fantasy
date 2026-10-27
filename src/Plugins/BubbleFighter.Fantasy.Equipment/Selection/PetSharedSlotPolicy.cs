using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Equipment.Selection;

/// <summary>
/// Denies wearing a second pet in the single shared pet slot. Pet rows carry no location
/// columns in <c>ShopItem2</c>, and each client finds the active pet by scanning its
/// inventory for the first worn function-10001 item (kr-812 sub_9782C0 iterates the
/// inventory map and returns the first equipped item whose function is 10001; kr-1256
/// sub_806530 does the same over the InventorySlot map and falls back to the by-function
/// scan kr-1256 sub_8066B0), so one worn pet is the only state either client renders and a
/// second worn pet would be unreachable. The rule runs as one middleware of the shared
/// <c>EquipmentSelectionApplying</c> waterfall, so it composes with every other selection
/// policy — an allowed pet wear still passes through the rest of the chain unchanged — and
/// a refusal returns the denied <see cref="EquipmentSelectionDecision"/> the selection
/// policy turns into the equip reply's failure code.
/// </summary>
internal sealed class PetSharedSlotPolicy(
    IReadOnlySet<int> petItemIds,
    IInventoryRepository inventory,
    string protocol,
    ILogger logger)
{
    /// <summary>Decides one selection request; every non-pet change passes straight through.</summary>
    /// <param name="request">The selection about to run; its protocol names the row that guards it.</param>
    /// <param name="next">The rest of the decision chain, ending at the atomic selection.</param>
    /// <param name="ct">Cancellation for the inventory read.</param>
    /// <returns>The chain's decision, or a denial when another pet already holds the slot.</returns>
    public async ValueTask<EquipmentSelectionDecision> ApplyAsync(
        EquipmentSelectionApplying request,
        Func<CancellationToken, ValueTask<EquipmentSelectionDecision>> next,
        CancellationToken ct)
    {
        // Another protocol's pet set is not this row's workbook; its requests pass through.
        if (request.Protocol != protocol || !request.Worn || !petItemIds.Contains(request.ItemId))
            return await next(ct).ConfigureAwait(false);
        // The character bag's worn flags are the view the selection itself commits against;
        // slotless items are never repaired from the legacy equipped-bag snapshot.
        var bag = await inventory.ListSlotsAsync(request.UserNum, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        foreach (var row in bag)
        {
            if (row.ItemId != request.ItemId && (row.Flags & InventorySlotRecord.WornFlagBit) != 0
                && petItemIds.Contains(row.ItemId))
            {
                logger.LogInformation(
                    "Player {User} wear of pet {Item} on {Protocol} refused: pet {Worn} already holds the shared pet slot.",
                    request.UserNum, request.ItemId, request.Protocol, row.ItemId);
                return new EquipmentSelectionDecision(false, request.ItemId, request.Worn,
                    $"pet {row.ItemId} is worn");
            }
        }

        return await next(ct).ConfigureAwait(false);
    }
}
