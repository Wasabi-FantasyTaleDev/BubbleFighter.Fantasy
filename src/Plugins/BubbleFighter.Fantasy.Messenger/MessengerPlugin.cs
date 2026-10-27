using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Messenger;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Messenger;

/// <summary>
/// Publishes the game-neutral messenger core over a messenger store. Tenants are trusted through their public keys,
/// or implicitly when a <c>messenger.tickets</c> row runs in the same process, so the core works both inside a game
/// server and as a standalone deployment shared by several servers.
/// </summary>
[Plugin("messenger", Description = "Game-neutral friends, categories, presence and change events for native messenger clients.",
    Requires = [typeof(IMessengerStore)], Optional = [typeof(IMessengerTicketIssuer)], Provides = [typeof(IMessengerService)])]
public sealed class MessengerPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MessengerOptions>();
        var tenants = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        foreach (var tenant in options.Tenants)
            tenants.Add(tenant.Key, Own(context, MessengerTicketCodec.ImportKey(tenant.PublicKey, requirePrivate: false)));
        if (options.TrustLocalIssuer && context.Services.TryGet<IMessengerTicketIssuer>(out var issuer) && issuer is not null)
        {
            if (tenants.ContainsKey(issuer.Tenant))
                throw new ConfigException($"Tenant '{issuer.Tenant}' is listed in tenants and also trusted as the local issuer; keep one.");
            var key = Own(context, ECDsa.Create());
            key.ImportSubjectPublicKeyInfo(issuer.PublicKey.Span, out _);
            tenants.Add(issuer.Tenant, key);
        }
        if (tenants.Count == 0)
            throw new ConfigException("The messenger trusts no tenant: list tenants or mount a messenger.tickets row with trust_local_issuer.");

        var service = new MessengerService(options, context.Services.Get<IMessengerStore>(), tenants, TimeProvider.System, context.Logger);
        context.Effect(context.Services.Register<IMessengerService>(service));
        await context.RunTaskAsync("messenger-sweep", async token =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepSeconds));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await service.SweepAsync(token).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { context.Logger.LogError(e, "Messenger sweep failed."); }
            }
        }, cancellationToken).ConfigureAwait(false);
        context.Logger.LogInformation("Messenger core active for {TenantCount} tenant(s) and {GameCount} catalog game(s).", tenants.Count, options.Games.Length);
    }

    private static ECDsa Own(IPluginContext context, ECDsa key)
    {
        context.Effect(key);
        return key;
    }
}
