using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// Pushes <c>game.session.changed</c> v1: the same snapshot as the <c>game.session.state</c> request, sent to a negotiated
/// launcher whenever the player's scene changes (main menu, plaza, a room's waiting, countdown, loading, match and result
/// phases, its mode, tutorials) and once whenever its connection becomes bound, so an overlay always knows where the
/// player is without polling.
/// </summary>
[Plugin("companion.scene", Category = "launcher", Names = ["en=Nirinsou Link scene", "zh-Hans=Nirinsou Link 场景", "zh-Hant=Nirinsou Link 場景", "ko=Nirinsou Link 장면"], Description = "Pushes the player's current scene to Nirinsou Link launchers when it changes.",
    Requires = [typeof(ICompanionHub)], LiveMany = [typeof(IGameSessionStateReader)])]
public sealed class CompanionScenePlugin : IPlugin
{
    internal const string ChangedType = "game.session.changed";

    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<CompanionFeedOptions>();
        var hub = context.Services.Get<ICompanionHub>();
        var state = new CompanionSessionState(context);
        var feed = new CompanionStateFeed<GameSessionState, GameSessionState>(hub, ChangedType, 1, state.Describe,
            static (_, current) => current, TimeSpan.FromMilliseconds(options.RetryMilliseconds), context.Logger);
        context.Effect(hub.Offer(ChangedType, 1));
        await feed.BindAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
