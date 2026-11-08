using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private IClientDataStore? _finalZoneData;
    private FinalZoneItemCatalog? _finalZoneCatalog;
    private FinalZoneDataLimits? _finalZoneDataLimits;

    internal void ConfigureFinalZoneItems(IClientDataStore data)
    {
        if (_options.FinalZoneFirstObjectId <= 0 || _options.FinalZoneMaximumObjects <= 0
            || (long)_options.FinalZoneFirstObjectId + _options.FinalZoneMaximumObjects > RoomRegistry.SceneEntity(0))
            throw new ConfigException("FinalZone item handles must not overlap participant handles or overflow a signed word.");
        var limits = new FinalZoneDataLimits(_options.FinalZoneMaximumFileBytes, _options.FinalZoneMaximumRows,
            _options.FinalZoneMaximumItemSpecs, _options.FinalZoneMaximumAlternatives,
            _options.FinalZoneMaximumQuantity, _options.FinalZoneMaximumBonusItems);
        _finalZoneCatalog = FinalZoneItemCatalog.Load(data, limits);
        _finalZoneDataLimits = limits;
        _finalZoneData = data;
    }

    // All participants have loaded. Resource failures must occur before announcing the countdown.
    private void PrepareFinalZoneItems(PlayRoom room)
    {
        if (!IsFinalZoneMode(room) || _finalZoneData is null) return;
        if (room.FinalZone?.Generation == room.StartGeneration) return;
        foreach (var seat in room.Seats) _finalZoneCatalog!.ValidateOpeningEquipment(seat.EquippedItemIds);
        var map = FinalZoneMapItems.Load(_finalZoneData, room.RoundStageKey, _finalZoneCatalog!, _finalZoneDataLimits!);
        var manifest = FinalZoneItemManifest.Create(map, Random.Shared, _options.FinalZoneFirstObjectId,
            _options.FinalZoneMaximumObjects);
        var state = CreateFinalZoneState(room);
        var ledger = new FinalZoneItemLedger(_finalZoneCatalog!.Items,
            room.Seats.Select(seat => RoomRegistry.SceneEntity(seat.Slot)), _options.FinalZoneFirstObjectId,
            _options.FinalZoneMaximumObjects, _options.FinalZoneMaximumQuantity);
        ledger.Initialize(manifest, _finalZoneCatalog.OpeningInventory());
        state.Items = ledger;
        state.OpeningItems = manifest;
        var fields = FinalZoneWaterRules.LoadFields(_finalZoneData, room.RoundStageKey, _finalZoneDataLimits!);
        state.WaterRules = FinalZoneWaterRules.FromFields(fields);
        var drones = FinalZoneDroneRules.FromFields(fields);
        state.DroneRules = drones;
        state.Supplies = FinalZoneSupplyCatalog.LoadForMap(_finalZoneData, room.RoundStageKey, _finalZoneCatalog,
            _finalZoneDataLimits!, drones.Enabled);
        room.FinalZone = state;
    }

    private async ValueTask StartFinalZoneItemsAsync(PlayRoom room, CancellationToken ct)
    {
        if (!IsFinalZoneMode(room) || room.FinalZone is not { OpeningItems: { } manifest } state) return;
        // kr-1256 sub_CDB180 consumes explicit objects after the scene has loaded, with no seed prerequisite.
        await PublishFinalZoneAsync(room, FinalZoneItemsSpawned!, new RoundFinalZoneItemsSpawned(manifest), ct).ConfigureAwait(false);
        state.OpeningItems = null;
        await StartFinalZoneWaterAsync(room).ConfigureAwait(false);
        await StartFinalZoneDronesAsync(room).ConfigureAwait(false);
    }
}
