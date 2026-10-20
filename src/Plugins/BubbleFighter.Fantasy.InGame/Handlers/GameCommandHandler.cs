using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Dispatches recognized chat commands after checking the waiting room and host identity: <c>!start</c>, and
/// <c>!set &lt;modeid&gt;</c> when the deployment path supplies a mode setter.
/// </summary>
/// <param name="registry">The room registry.</param>
/// <param name="start">Starts the sender's room.</param>
/// <param name="notices">Sends failure and mode notices; null sends none.</param>
/// <param name="setMode">
/// Applies a new mode to a waiting room on the room's executor and shows it to the members; returns false when
/// the deployment does not offer the mode. Null leaves <c>!set</c> unrecognized.
/// </param>
internal sealed class GameCommandHandler(RoomRegistry registry,
    Func<ISession, CancellationToken, ValueTask> start,
    RoomNotices? notices = null,
    Func<PlayRoom, ushort, CancellationToken, ValueTask<bool>>? setMode = null)
{
    private const string SetCommand = "!set";

    private readonly Dictionary<string, Func<ISession, CancellationToken, ValueTask>> _hostCommands =
        new(StringComparer.OrdinalIgnoreCase) { ["!start"] = start };

    internal async ValueTask<bool> TryHandleAsync(ISession session, string text, CancellationToken ct)
    {
        var normalized = text.Trim();
        if (normalized.StartsWith('！')) normalized = "!" + normalized[1..];
        if (setMode is not null && TrySplitSet(normalized, out var argument))
        {
            await SetModeAsync(session, argument, ct).ConfigureAwait(false);
            return true;
        }
        if (!_hostCommands.TryGetValue(normalized, out var command))
            return false;
        var room = registry.TryGet(session.Id);
        string? failure;
        if (room is null)
            failure = "command.start.waiting_only";
        else
            lock (room.Gate)
                failure = room.FindByGame(session.Id)?.Slot != room.MasterSlot
                    ? "command.start.host_only"
                    : room.Phase != RoomPhase.Waiting ? "command.start.waiting_only" : null;
        if (failure is null)
            await command(session, ct).ConfigureAwait(false);
        else if (notices is not null)
            await notices.SendAsync(session, failure, null, ct).ConfigureAwait(false);
        return true;
    }

    // "!set" alone or followed by whitespace and an argument; "!settings" is ordinary chat.
    private static bool TrySplitSet(string text, out string argument)
    {
        argument = "";
        if (!text.StartsWith(SetCommand, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = text[SetCommand.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
            return false;
        argument = rest.Trim();
        return true;
    }

    // The checks and the change run on the room's executor, so a start or a join cannot interleave with them.
    private async ValueTask SetModeAsync(ISession session, string argument, CancellationToken ct)
    {
        var room = registry.TryGet(session.Id);
        if (room is null)
        {
            await FailAsync(session, "command.set.waiting_only", null, ct).ConfigureAwait(false);
            return;
        }
        string? failure = null;
        Dictionary<string, string>? failureArguments = null;
        try
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                lock (room.Gate)
                    failure = room.FindByGame(session.Id)?.Slot != room.MasterSlot
                        ? "command.set.host_only"
                        : room.Phase != RoomPhase.Waiting ? "command.set.waiting_only" : null;
                if (failure is not null)
                    return;
                if (!RoomModeTag.TryParseModeId(argument, out var modeId))
                {
                    failure = "command.set.invalid";
                    return;
                }
                if (!await setMode!(room, (ushort)modeId, operationCt).ConfigureAwait(false))
                {
                    failure = "command.set.unsupported";
                    failureArguments = new() { ["mode"] = modeId.ToString(CultureInfo.InvariantCulture) };
                    return;
                }
                if (notices is not null)
                    await notices.AnnounceModeAsync(room, modeId, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The room closed between the lookup and the queue; there is nobody left to tell.
            return;
        }
        if (failure is not null)
            await FailAsync(session, failure, failureArguments, ct).ConfigureAwait(false);
    }

    private ValueTask FailAsync(ISession session, string key, IReadOnlyDictionary<string, string>? arguments, CancellationToken ct) =>
        notices is null ? ValueTask.CompletedTask : notices.SendAsync(session, key, arguments, ct);
}
