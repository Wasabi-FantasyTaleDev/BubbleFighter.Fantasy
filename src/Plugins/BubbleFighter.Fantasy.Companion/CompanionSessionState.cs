using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Companion;

internal sealed class CompanionSessionState(IPluginContext context)
{
    internal const string MessageType = "game.session.state";

    internal ValueTask<object?> HandleAsync(CompanionCaller caller, JsonElement payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any())
            throw new InvalidDataException("Session state requires an empty object payload.");
        if (context.Lifetime.IsCancellationRequested || caller.Session.Connection.IsClosed
            || !caller.Session.TryFeature<IPlayerSession>(out var player)
            || !string.Equals(player.Identity.Username, caller.Account, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<object?>(GameSessionState.Unavailable);
        var generation = player.Generation;
        var snapshots = context.Services.GetAll<IGameSessionStateReader>()
            .Select(reader => reader.Describe(caller.Session)).OfType<GameSessionState>().ToArray();
        if (snapshots.Length > 1) throw new InvalidOperationException("Multiple gameplay owners hold the queried session.");
        var result = snapshots.SingleOrDefault() ?? (player.Phase switch
        {
            SessionPhase.Authenticated => new("authenticated", false, false, false, null),
            SessionPhase.MainMenu => new("main_menu", false, false, false, null),
            SessionPhase.Plaza => new("plaza", false, false, false, null),
            SessionPhase.Migrating => new("migrating", false, false, false, null),
            _ => GameSessionState.Unavailable,
        });
        return ValueTask.FromResult<object?>(generation == player.Generation && !caller.Session.Connection.IsClosed
            ? result : GameSessionState.Unavailable);
    }
}
