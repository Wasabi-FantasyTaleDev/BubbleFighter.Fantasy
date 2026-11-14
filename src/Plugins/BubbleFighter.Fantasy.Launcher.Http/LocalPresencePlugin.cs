using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>Controls private presence on a game node that does not host the public Launcher API.</summary>
public sealed class LocalPresenceOptions
{
    /// <summary>Gets or sets whether authenticated group members may receive this node's public cards; disabled by default.</summary>
    public bool RosterEnabled { get; set; }
    /// <summary>Gets or sets the maximum unique local login owners retained in a report.</summary>
    [Range(1, 65536)] public int MaxPresencePlayers { get; set; } = 4096;
}

/// <summary>Reports local login owners without mounting an account API or replacing a node's ticket authority.</summary>
[Plugin("launcher.presence-local", Category = "launcher", Names = ["en=Launcher presence", "zh-Hans=启动器在线状态", "zh-Hant=啟動器上線狀態", "ko=런처 접속 상태"], Description = "Private local presence for nodes without a Launcher API.",
    Requires = [typeof(ISessionRegistry)], Provides = [typeof(ILocalLauncherPresence)])]
public sealed class LocalPresencePlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<LocalPresenceOptions>();
        var source = new LauncherPresence(new LauncherOptions
        { RosterEnabled = options.RosterEnabled, MaxPresencePlayers = options.MaxPresencePlayers },
            context.Services.Get<ISessionRegistry>(), null);
        context.Effect(context.Services.Register<ILocalLauncherPresence>(source));
        return ValueTask.CompletedTask;
    }
}
