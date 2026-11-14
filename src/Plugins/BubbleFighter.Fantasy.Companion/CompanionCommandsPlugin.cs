using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>Routes launch-bound command requests through the player's existing gameplay command input.</summary>
[Plugin("companion.commands", Category = "launcher", Names = ["en=Nirinsou Link commands", "zh-Hans=Nirinsou Link 命令", "zh-Hant=Nirinsou Link 指令", "ko=Nirinsou Link 명령"], Requires = [typeof(ICompanionHub)],
    // Each build's gameplay owner comes and goes with the build; reading them live keeps the launcher channel up.
    LiveMany = [typeof(IGameCommandInput), typeof(IGameSessionStateReader)])]
public sealed class CompanionCommandsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var commands = new CompanionCommands(context, context.Config.Bind<CompanionCommandOptions>());
        context.Effect(commands);
        context.Effect(context.Services.Get<ICompanionHub>().Route(CompanionCommands.MessageType, 1, commands.HandleAsync));
        var state = new CompanionSessionState(context);
        context.Effect(context.Services.Get<ICompanionHub>().Route(CompanionSessionState.MessageType, 1, state.HandleAsync));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Bounds command input and completed operation retention for the <c>companion.commands</c> row.</summary>
public sealed class CompanionCommandOptions
{
    /// <summary>Gets or sets the maximum pending and retained operations across all launches.</summary>
    [Range(1, 65536)] public int MaxOperations { get; set; } = 4096;

    /// <summary>Gets or sets how long a completed operation can be replayed, measured from completion in seconds.</summary>
    [Range(30, 86400)] public int RetentionSeconds { get; set; } = 600;

    /// <summary>Gets or sets the maximum command text characters before the shared command limits are applied.</summary>
    [Range(1, 65536)] public int MaxInputCharacters { get; set; } = 4096;
}
