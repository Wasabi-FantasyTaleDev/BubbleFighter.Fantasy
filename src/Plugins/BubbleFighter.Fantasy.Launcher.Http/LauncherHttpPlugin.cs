using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>Publishes the common launcher account API and a one-use game-ticket redemption service.</summary>
[Plugin("launcher.http", Description = "Public launcher accounts, profiles and client instance catalog.",
    Requires = [typeof(ILauncherAccountRepository), typeof(ILauncherSessionStore)], Optional = [typeof(IPlayerWalletRepository), typeof(IPlayerProgressRepository)],
    Provides = [typeof(ILauncherTicketService)])]
public sealed class LauncherHttpPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<LauncherOptions>(); var sessions = new LauncherSessions(options, context.Services.Get<ILauncherSessionStore>());
        context.Services.TryGet<IPlayerWalletRepository>(out var wallets); context.Services.TryGet<IPlayerProgressRepository>(out var progress);
        var server = await LauncherServer.StartAsync(options, context.Services.Get<ILauncherAccountRepository>(), sessions, wallets, progress, cancellationToken).ConfigureAwait(false);
        context.Effect(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult());
        context.Effect(context.Services.Register<ILauncherTicketService>(sessions));
        context.Logger.LogInformation("Launcher API listening on {Address}.", options.Listen);
    }
}
