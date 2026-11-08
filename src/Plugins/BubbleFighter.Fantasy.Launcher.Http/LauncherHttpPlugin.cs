using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Messenger;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>Publishes the common launcher account API, its client policy and a one-use game-ticket redemption service; launches also carry a messenger ticket when a messenger ticket issuer is mounted.</summary>
[Plugin("launcher.http", Description = "Public launcher accounts, profiles and client instance catalog.",
    Requires = [typeof(ILauncherAccountRepository), typeof(ILauncherSessionStore)], Optional = [typeof(IPlayerWalletRepository), typeof(IPlayerProgressRepository), typeof(ISessionRegistry), typeof(IMessengerTicketIssuer), typeof(IHostReadiness), typeof(ICompanionTicketIssuer), typeof(IServerDirectory), typeof(ILauncherServerSelection), typeof(ILauncherPresenceDirectory)],
    Provides = [typeof(ILauncherTicketService), typeof(IClientPolicyView), typeof(ILocalLauncherPresence)])]
public sealed class LauncherHttpPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<LauncherOptions>(); var sessions = new LauncherSessions(options, context.Services.Get<ILauncherSessionStore>());
        context.Services.TryGet<IPlayerWalletRepository>(out var wallets); context.Services.TryGet<IPlayerProgressRepository>(out var progress);
        context.Services.TryGet<ISessionRegistry>(out var players); context.Services.TryGet<IMessengerTicketIssuer>(out var messenger); context.Services.TryGet<IHostReadiness>(out var readiness);
        context.Services.TryGet<ICompanionTicketIssuer>(out var companion);
        context.Services.TryGet<IServerDirectory>(out var directory);
        context.Services.TryGet<ILauncherServerSelection>(out var serverSelection);
        context.Services.TryGet<ILauncherPresenceDirectory>(out var presenceDirectory);
        var server = await LauncherServer.StartAsync(options, context.Services.Get<ILauncherAccountRepository>(), sessions,
            players: players, matchConnections: null, wallets: wallets, progress: progress,
            ct: cancellationToken, logger: context.Logger, messenger: messenger, readiness: readiness, companion: companion,
            directory: directory, serverSelection: serverSelection, presenceDirectory: presenceDirectory).ConfigureAwait(false);
        context.Effect(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult());
        context.Effect(context.Services.Register<ILauncherTicketService>(sessions));
        context.Effect(context.Services.Register<ILocalLauncherPresence>(server.LocalPresence));
        context.Effect(context.Services.Register<IClientPolicyView>(new PolicyView(options.ClientPolicy.ToContract())));
        context.Logger.LogInformation("Launcher API listening on {Address}.", options.Listen);
    }

    private sealed class PolicyView(ClientPolicy current) : IClientPolicyView
    {
        public ClientPolicy Current { get; } = current;
    }
}
