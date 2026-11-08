using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGamePlugin
{
    private static void MountMapScripts(IPluginContext context, InGameOptions options, RoundFlow round)
    {
        var game = options.GameProtocol;
        var relay = options.RelayProtocol;
        var messages = context.Services.Get<IMessageRouter>().For(context);
        Mount<RoundFinalZoneMapEffectChanged>(sender => round.FinalZoneMapEffects = sender, round.HandleFinalZoneMapEffectAsync);
        Mount<RoundMapObjectVisibilityChanged>(sender => round.MapObjectVisibility = sender, round.HandleMapObjectVisibilityAsync);
        Mount<RoundBreakableMapObjectCreated>(sender => round.BreakableMapObjects = sender, round.HandleBreakableMapObjectAsync);
        if (round.ZombieEscapeEnabled && (round.FinalZoneMapEffects is null || round.MapObjectVisibility is null
            || round.BreakableMapObjects is null))
            throw new ConfigException("Zombie Escape requires the complete native map-script input and output surface.");

        void Mount<T>(Action<IMessageSender<T>> assign, MessageHandler<T> handler) where T : IMessage
        {
            var inputs = context.Services.GetAll<IInboundMessageBinding<T>>();
            var onGame = inputs.Any(binding => binding.Protocol == game);
            var onRelay = inputs.Any(binding => binding.Protocol == relay);
            if (!onGame && !onRelay) return;
            if (!onGame || !onRelay)
                throw new ConfigException($"Map script {typeof(T).Name} requires both native input links.");
            assign(PlaySenders.Required<T>(context, game));
            context.Effect(messages.On<T>(game, handler));
            context.Effect(messages.On<T>(relay, handler));
        }
    }
}
