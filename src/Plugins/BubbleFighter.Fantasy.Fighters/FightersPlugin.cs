using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Fighters.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Fighters;

/// <summary>
/// Playable fighter items on one account (Dao, Dizni and the rest of the 300000-range family). The row reads
/// <c>FighterConfig.xml</c> from the build's client data through <c>IClientDataStore</c>, validates the
/// HorzTable and publishes the account's fighter roster and the room row's seat character resolver. The
/// build comes from the row's client data and lineage, and native character creation is served by the
/// shared <c>dialect.messages</c> creation binding, so this row handles no packets.
/// </summary>
/// <remarks>The roster is published as <see cref="IFighterRoster"/> and <see cref="ISeatCharacterResolver"/>
/// so the room row can resolve each seat's equipped character id for the S2C 117 profile map. The roster
/// supplies the server-side equipped character identity.</remarks>
[Plugin("fighters",
    Description = "Playable fighter items from FighterConfig.xml; publishes IFighterRoster and ISeatCharacterResolver.",
    Requires = [typeof(IClientDataStore)],
    Optional = [typeof(IInventoryRepository), typeof(IFighterRepository), typeof(IEquipmentSelection)],
    Provides = [typeof(IFighterRoster), typeof(ISeatCharacterResolver)])]
public sealed class FightersPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<FightersOptions>();
        var clientData = context.Services.Get<IClientDataStore>();
        var catalog = FighterCatalogLoader.Load(
            clientData.Read(options.FighterConfigClientPath),
            options.FighterConfigClientPath, options.MaxFighterConfigBytes);
        if (catalog.Playable.Count == 0)
            throw new InvalidOperationException($"{catalog.Source} has no 1..31 Field columns.");

        // The unlocked set is the character bag and the equipped id is the
        // fighters table, so a restart keeps what the player owns and wears.
        // Without the persistence rows the in-memory roster is used and the
        // session's unlocks reset on restart, which the log states.
        IFighterRoster roster;
        var hasInventory = context.Services.TryGet<IInventoryRepository>(out var inventoryStore)
            && inventoryStore is not null;
        var hasFighters = context.Services.TryGet<IFighterRepository>(out var fighterStore)
            && fighterStore is not null;
        if (hasInventory && hasFighters)
        {
            context.Services.TryGet<IEquipmentSelection>(out var selection);
            roster = new StoreBackedFighterRoster(inventoryStore!, fighterStore!, catalog, selection);
            context.Logger.LogInformation(
                "Fighters roster is store-backed: unlocked = character bag, equipped = fighters table.");
        }
        else
        {
            roster = new MemoryFighterRoster();
            context.Logger.LogWarning(
                "Fighters roster is in-memory: the persistence.mysql row is not active, so unlocks and the equipped character reset on restart.");
        }

        context.Effect(context.Services.Register<IFighterRoster>(roster));
        // The room row resolves each seat's equipped character id through this adapter, using the server-side
        // equipped character identity. The roster is keyed by long; the room seats by int.
        context.Effect(context.Services.Register<ISeatCharacterResolver>(new SeatCharacterResolver(roster)));
        context.Logger.LogInformation(
            "Fighters catalog {Path}: {All} Field columns, {Playable} playable 300000-range items (Dao item {Dao}). Roster published as IFighterRoster and ISeatCharacterResolver.",
            catalog.Source, catalog.All.Count, catalog.Playable.Count, catalog.Default.ItemId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Publishes the roster as the room row's seat character resolver. The roster is keyed by <c>long</c>
    /// identity and the room seats by <c>int</c>, so the widening is explicit.
    /// </summary>
    private sealed class SeatCharacterResolver(IFighterRoster roster) : ISeatCharacterResolver
    {
        /// <inheritdoc />
        public ValueTask<int> GetEquippedAsync(int userNum, CancellationToken ct) =>
            roster.GetEquippedAsync(userNum, ct);
    }
}
