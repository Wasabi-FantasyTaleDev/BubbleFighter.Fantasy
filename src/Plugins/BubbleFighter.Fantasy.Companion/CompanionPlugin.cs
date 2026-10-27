using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Launcher;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// Fantasy Tale Nirinsou Link, the server side. The launcher API opens a launch with every game launch; the launcher
/// connects here with the launch's ticket, and once the game login redeems the launch's game ticket the connection is
/// bound to that game session, so server plugins can talk to the launcher of a player they already know.
/// </summary>
[Plugin("companion", Description = "Fantasy Tale Nirinsou Link: launch-bound WebSocket channel between launchers and server plugins.",
    Provides = [typeof(ICompanionTicketIssuer), typeof(ICompanionHub)])]
public sealed class CompanionPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<CompanionOptions>();
        var launches = new CompanionLaunches(options, TimeProvider.System, context.Logger);
        var hub = new CompanionHub(launches);
        var server = await CompanionServer.StartAsync(options, launches, hub, context.Logger, cancellationToken).ConfigureAwait(false);
        context.Effect(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult());
        var sweep = new Timer(_ => launches.Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        context.Effect(sweep);
        context.Effect(context.Events.On<LauncherLaunchRedeemed>(launches.Bind));
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => launches.End(e.Connection)));
        context.Effect(context.Services.Register<ICompanionTicketIssuer>(launches));
        context.Effect(context.Services.Register<ICompanionHub>(hub));
        context.Logger.LogInformation("Nirinsou Link listening on {Address}; launchers connect to {PublicUrl}.", options.Listen, options.EffectivePublicUrl);
    }
}
