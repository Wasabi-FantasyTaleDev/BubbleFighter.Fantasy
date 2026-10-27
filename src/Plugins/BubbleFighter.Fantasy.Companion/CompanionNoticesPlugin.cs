using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Lobby;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// Launcher popups over Nirinsou Link. It offers <c>ui.notification.show</c>, which the launcher draws as a game-style
/// message box above the game, takes the launcher's <c>ui.notification.result</c> back, and adds the <c>popup</c>
/// command that shows a message to every online player whose launcher negotiated it.
/// </summary>
[Plugin("companion.notices", Description = "Launcher popups over Nirinsou Link and the popup management command.",
    Requires = [typeof(ICompanionHub), typeof(ICommandRegistry), typeof(IPermissionRegistry), typeof(IGameText)])]
public sealed class CompanionNoticesPlugin : IPlugin
{
    internal const string ShowType = "ui.notification.show";
    internal const string ResultType = "ui.notification.result";

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<CompanionNoticeOptions>();
        var hub = context.Services.Get<ICompanionHub>();
        var texts = context.Services.Get<IGameText>();
        var logger = context.Logger;
        context.Effect(hub.Offer(ShowType, 1));
        context.Effect(hub.Route(ResultType, 1, (caller, payload, _) =>
        {
            // The launcher reports what became of a popup; nothing waits on it yet, so it is only recorded.
            logger.LogDebug("Launch {LaunchId} reported popup {NotificationId}: {Phase}.", caller.LaunchId,
                Text(payload, "notification_id"), Text(payload, "phase"));
            return ValueTask.FromResult<object?>(null);
        }));

        const string permission = "game.management.popup";
        const string description = "Show a popup in the launcher of every online player connected through Nirinsou Link.";
        context.Effect(context.Services.Get<IPermissionRegistry>().Register(new(permission, description)));
        context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
            new("popup", [], permission, description, [new("text", CommandArgumentKind.RemainingText)]), (invocation, _) =>
            {
                var text = invocation.Arguments[0];
                if (text.Length > options.MaxTextCharacters)
                    return ValueTask.FromResult(new CommandResult(CommandStatus.InvalidArguments, texts.Format("", "companion.popup_too_long",
                        new Dictionary<string, string> { ["max"] = options.MaxTextCharacters.ToString(CultureInfo.InvariantCulture) })));
                int sent = 0, failed = 0;
                foreach (var session in hub.BoundSessions())
                {
                    if (!hub.Supports(session, ShowType, 1)) continue;
                    if (hub.Send(session, ShowType, 1, new { Title = texts.Format(session, "companion.popup_title"), Text = text })) sent++;
                    else failed++;
                }
                return ValueTask.FromResult(sent + failed == 0
                    ? new CommandResult(CommandStatus.InvalidState, texts.Format("", "companion.popup_none"))
                    : new CommandResult(CommandStatus.Success, texts.Format("", "companion.popup_sent", new Dictionary<string, string>
                    {
                        ["count"] = sent.ToString(CultureInfo.InvariantCulture), ["failed"] = failed.ToString(CultureInfo.InvariantCulture),
                    })));
            }));
        return ValueTask.CompletedTask;
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

/// <summary>Bounds of the <c>companion.notices</c> row.</summary>
public sealed class CompanionNoticeOptions
{
    /// <summary>Gets or sets the longest popup text; the launcher refuses text over 8192 characters.</summary>
    [Range(1, 8192)] public int MaxTextCharacters { get; set; } = 2000;
}
