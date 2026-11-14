using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGamePlugin
{
    private static void MountBnB(IPluginContext context, InGameOptions options, RoundFlow round)
    {
        bool Bound<T>(string protocol) where T : IMessage => context.Services.GetAll<IInboundMessageBinding<T>>()
            .Any(binding => binding.Protocol == protocol);
        foreach (var protocol in new[] { options.GameProtocol, options.RelayProtocol })
            if (Bound<RoundBnBPlacementReported>(protocol) != options.BnB
                || Bound<RoundBnBExplosionReported>(protocol) != options.BnB
                || Bound<RoundBnBPickupReported>(protocol) != options.BnB)
                throw new ConfigException("bnb must enable the complete game and relay exchange on both the in-game and room dialect rows.");
        if (!options.BnB) return;
        BnBSenders Senders(string protocol) => new(PlaySenders.Required<RoundBnBPlacementPublished>(context, protocol),
            PlaySenders.Required<RoundBnBExplosionPublished>(context, protocol), PlaySenders.Required<RoundBnBPickupPublished>(context, protocol));
        round.ConfigureBnB(context.Services.Get<IClientDataStore>(), Senders(options.GameProtocol), Senders(options.RelayProtocol));
        var messages = context.Services.Get<IMessageRouter>().For(context);
        foreach (var protocol in new[] { options.GameProtocol, options.RelayProtocol })
        {
            context.Effect(messages.On<RoundBnBPlacementReported>(protocol, round.HandleBnBPlacementAsync));
            context.Effect(messages.On<RoundBnBExplosionReported>(protocol, round.HandleBnBExplosionAsync));
            context.Effect(messages.On<RoundBnBPickupReported>(protocol, round.HandleBnBPickupAsync));
        }
    }
}
