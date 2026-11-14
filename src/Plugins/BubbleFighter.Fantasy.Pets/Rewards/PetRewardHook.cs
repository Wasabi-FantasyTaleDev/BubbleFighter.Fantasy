using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets.Rewards;

internal static class PetRewardHook
{
    internal static void Register(IPluginContext context, PetCatalog catalog, string protocol, PetRewardOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        var policy = new PetRewardPolicy(catalog, options);
        if (!policy.Enabled) return;
        if (context.Services.GetAll<IPreparedRoundRewards>().Count(capability => capability.Protocol == protocol) != 1)
            throw new ConfigException($"Enabled pet rewards require exactly one prepared-round settlement provider for '{protocol}'.");
        context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
        {
            var reward = await next(ct).ConfigureAwait(false);
            return request.Protocol == protocol ? policy.Apply(request.ModeId, request.Equipment, reward) : reward;
        }));
    }
}
