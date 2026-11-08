using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGamePlugin
{
    private static void MountFinalZone(IPluginContext context, InGameOptions options, RoundFlow round)
    {
        var game = options.GameProtocol;
        bool Bound<T>(string protocol) where T : IMessage => context.Services
            .GetAll<IInboundMessageBinding<T>>().Any(binding => binding.Protocol == protocol);
        if (!Bound<RoundFinalZoneItemPickupRequested>(game)) return;
        if (!Bound<RoundFinalZoneItemsDropped>(game) || !Bound<RoundFinalZoneBackpackDropped>(game)
            || !Bound<RoundFinalZoneBackpackPickupRequested>(game)
            || !Bound<RoundFinalZoneMapEffectChanged>(game) || !Bound<RoundFinalZoneMapEffectChanged>(options.RelayProtocol)
            || !Bound<RoundFinalZoneDroneDestroyed>(game) || !Bound<RoundFinalZoneGoldenSupplyRequested>(game)
            || !Bound<RoundFinalZoneTrapRequested>(game) || !Bound<RoundFinalZoneTrapLinked>(game)
            || !Bound<RoundFinalZoneTrapLinked>(options.RelayProtocol))
            throw new ConfigException("FinalZone requires the complete native item, backpack and map-effect input surface.");
        round.FinalZoneEliminated = PlaySenders.Required<RoundFinalZonePlayerEliminated>(context, game);
        round.FinalZoneItemsSpawned = PlaySenders.Required<RoundFinalZoneItemsSpawned>(context, game);
        round.FinalZoneItemPicked = PlaySenders.Required<RoundFinalZoneItemPicked>(context, game);
        round.FinalZoneBackpackSpawned = PlaySenders.Required<RoundFinalZoneBackpackSpawned>(context, game);
        round.FinalZoneBackpackPicked = PlaySenders.Required<RoundFinalZoneBackpackPicked>(context, game);
        round.FinalZoneWaterTimer = PlaySenders.Required<RoundFinalZoneWaterTimerChanged>(context, game);
        round.FinalZoneDroneSpawned = PlaySenders.Required<RoundFinalZoneDroneSpawned>(context, game);
        round.FinalZoneDroneBoxSpawned = PlaySenders.Required<RoundFinalZoneDroneBoxSpawned>(context, game);
        round.FinalZoneGoldenSupplySpawned = PlaySenders.Required<RoundFinalZoneGoldenSupplySpawned>(context, game);
        round.FinalZoneTrapSpawned = PlaySenders.Required<RoundFinalZoneTrapSpawned>(context, game);
        round.FinalZoneTrapLinked = PlaySenders.Required<RoundFinalZoneTrapLinked>(context, game);
        round.ConfigureFinalZoneItems(context.Services.Get<IClientDataStore>());
        var messages = context.Services.Get<IMessageRouter>().For(context);
        context.Effect(messages.On<RoundFinalZoneItemPickupRequested>(game, round.HandleFinalZoneItemPickupAsync));
        context.Effect(messages.On<RoundFinalZoneItemsDropped>(game, round.HandleFinalZoneItemsDroppedAsync));
        context.Effect(messages.On<RoundFinalZoneBackpackDropped>(game, round.HandleFinalZoneBackpackDroppedAsync));
        context.Effect(messages.On<RoundFinalZoneBackpackPickupRequested>(game, round.HandleFinalZoneBackpackPickupAsync));
        context.Effect(messages.On<RoundFinalZoneDroneDestroyed>(game, round.HandleFinalZoneDroneDestroyedAsync));
        context.Effect(messages.On<RoundFinalZoneGoldenSupplyRequested>(game, round.HandleFinalZoneGoldenSupplyAsync));
        context.Effect(messages.On<RoundFinalZoneTrapRequested>(game, round.HandleFinalZoneTrapRequestedAsync));
        context.Effect(messages.On<RoundFinalZoneTrapLinked>(game, round.HandleFinalZoneTrapLinkedAsync));
        context.Effect(messages.On<RoundFinalZoneTrapLinked>(options.RelayProtocol, round.HandleFinalZoneTrapLinkedAsync));
    }
}
