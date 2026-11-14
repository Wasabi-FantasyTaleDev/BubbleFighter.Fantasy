using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Pets;

/// <summary>Retains the match's pet equipment until all affected combat tables can be revised together.</summary>
internal sealed class PetEquipmentPhasePolicy(string protocol, IOnlinePlayers online, IPetEntitlements entitlements)
{
    /// <summary>Checks pet wear and removal before and after downstream decisions, including a retargeted pet.</summary>
    internal async ValueTask<EquipmentSelectionDecision> ApplyAsync(EquipmentSelectionApplying request,
        Func<CancellationToken, ValueTask<EquipmentSelectionDecision>> next, CancellationToken ct)
    {
        if (request.Protocol != protocol)
            return await next(ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        var requestedPet = entitlements.IsPetItem(request.ItemId);
        if (requestedPet && !Allowed(request.UserNum))
            return Refused(request.ItemId, request.Worn);

        var decision = await next(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!decision.Allowed)
            return decision;
        if ((requestedPet || entitlements.IsPetItem(decision.ItemId)) && !Allowed(request.UserNum))
            return Refused(decision.ItemId, decision.Worn);
        return decision;
    }

    private bool Allowed(long userNum)
    {
        if (userNum is <= 0 or > int.MaxValue)
            return false;
        var session = online.FindByUserNum((int)userNum);
        return session is not null && !session.Connection.IsClosed && session.Profile.Id == entitlements.BuildKey
            && session.TryFeature<IPlayerSession>(out var player) && player.Identity.UserNum == userNum
            && player.Phase is SessionPhase.MainMenu or SessionPhase.Plaza or SessionPhase.Room;
    }

    private static EquipmentSelectionDecision Refused(int itemId, bool worn) =>
        new(false, itemId, worn, "pet equipment requires the main menu, plaza or waiting room");
}
