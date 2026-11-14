using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// Tells a negotiated launcher when room creation is available to its player, so it watches the client's own Create Room
/// form only then. Active means the player is in the main menu or the plaza, the two scenes the server accepts a room
/// creation from; a successful create or join, a round and a return from a round to its room are inactive, and a failed
/// create or join changes nothing. The launcher receives <c>game.room.create.context</c> v1 on every change and once on
/// every bound connection, and may ask for the same object with <c>game.room.create.context.get</c> v1.
/// </summary>
[Plugin("companion.room_create", Category = "launcher", Names = ["en=Nirinsou Link room creation", "zh-Hans=Nirinsou Link 创建房间", "zh-Hant=Nirinsou Link 建立房間", "ko=Nirinsou Link 방 만들기"], Description = "Tells Nirinsou Link launchers when the player can create a room.",
    Requires = [typeof(ICompanionHub)])]
public sealed class CompanionRoomCreatePlugin : IPlugin
{
    internal const string ContextType = "game.room.create.context";
    internal const string SnapshotType = "game.room.create.context.get";

    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<CompanionFeedOptions>();
        var hub = context.Services.Get<ICompanionHub>();
        var feed = new CompanionStateFeed<bool, RoomCreateContext>(hub, ContextType, 1, Available, RoomCreateContext.Next,
            TimeSpan.FromMilliseconds(options.RetryMilliseconds), context.Logger);
        context.Effect(hub.Offer(ContextType, 1));
        context.Effect(hub.Route(SnapshotType, 1, async (caller, payload, ct) =>
        {
            if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any())
                throw new InvalidDataException("The room creation context request requires an empty object payload.");
            return await feed.SnapshotAsync(caller.Session, ct).ConfigureAwait(false);
        }));
        await feed.BindAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the session is in a scene the server accepts a room creation from.</summary>
    internal static bool Available(ISession session) =>
        !session.Connection.IsClosed && session.TryFeature<IPlayerSession>(out var player)
        && player.Phase is SessionPhase.MainMenu or SessionPhase.Plaza;
}

/// <summary>
/// The room creation context sent to the launcher. <see cref="ContextId"/> is opaque, created for one game session and kept
/// across launcher reconnects to it; a new login gets a new one. <see cref="Revision"/> starts at 1 and grows by one each
/// time <see cref="Active"/> changes, so a repeated delivery keeps its revision and an older one can be ignored.
/// </summary>
/// <param name="ContextId">Identity of the game session's context.</param>
/// <param name="Revision">The state's revision within the context.</param>
/// <param name="Active">Whether the player can create a room now.</param>
internal sealed record RoomCreateContext(string ContextId, long Revision, bool Active)
{
    internal static RoomCreateContext Next(RoomCreateContext? previous, bool active) =>
        previous is null ? new(Guid.NewGuid().ToString("N"), 1, active)
        : previous.Active == active ? previous : previous with { Revision = previous.Revision + 1, Active = active };
}
