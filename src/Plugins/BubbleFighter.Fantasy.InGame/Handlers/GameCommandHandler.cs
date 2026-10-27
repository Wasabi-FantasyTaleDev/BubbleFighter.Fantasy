using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Routes prefixed chat through shared command permissions and serialized room operations.
/// Profiles without shared command services retain the host-only start and set adapters.
/// </summary>
/// <param name="registry">The room registry.</param>
/// <param name="start">Starts the sender's room.</param>
/// <param name="notices">Sends failure and mode notices; null sends none.</param>
/// <param name="setMode">
/// Applies a new mode to a waiting room on the room's executor and shows it to the members; returns false when
/// the deployment does not offer the mode. Null leaves <c>!set</c> unrecognized.
/// </param>
/// <param name="canStart">Whether this deployment provides the round-start operation.</param>
internal sealed class GameCommandHandler(RoomRegistry registry,
    Func<ISession, CancellationToken, ValueTask> start,
    RoomNotices? notices = null,
    Func<PlayRoom, ushort, CancellationToken, ValueTask<bool>>? setMode = null,
    bool canStart = true) : IGameCommandEndpoint
{
    private ICommandExecutor? _dispatcher;

    internal bool SharedDispatch => _dispatcher is not null;

    /// <inheritdoc />
    public string Scope { get; private set; } = "";

    internal void BindShared(IPluginContext context, string scope, bool enabled)
    {
        if (!enabled) return;
        var hasEndpoints = context.Services.TryGet<IGameCommandEndpoints>(out var endpoints);
        var hasDispatcher = context.Services.TryGet<ICommandExecutor>(out var dispatcher);
        if (!hasEndpoints || !hasDispatcher)
            throw new ConfigException("Game command dispatch requires both commands and commands.game.");
        Scope = scope;
        _dispatcher = dispatcher;
        context.Effect(endpoints!.Register(context, this));
    }

    /// <inheritdoc />
    public ISession? FindSession(long sessionId)
    {
        var room = registry.TryGet(sessionId);
        if (room is null) return null;
        lock (room.Gate) return room.FindByGame(sessionId)?.Game;
    }

    /// <inheritdoc />
    public async ValueTask<CommandResult> ExecuteAsync(ISession session, CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Command == "mode" && setMode is not null)
            return await SetModeAsync(session, invocation.Arguments[0], cancellationToken, requireHost: false).ConfigureAwait(false);
        if (invocation.Command != "start") return new(CommandStatus.NotFound, "Room command is unavailable.");
        if (!canStart) return new(CommandStatus.InvalidState, "Round start is unavailable in this deployment.");
        var room = registry.TryGet(session.Id);
        if (room is null) return new(CommandStatus.InvalidState, "A waiting room is required.");
        int generation;
        bool armed;
        lock (room.Gate)
        {
            if (room.FindByGame(session.Id) is null || room.Phase != RoomPhase.Waiting)
                return new(CommandStatus.InvalidState, "A waiting room is required.");
            generation = room.StartGeneration;
            armed = room.AutoStartArmed;
        }
        await start(session, cancellationToken).ConfigureAwait(false);
        lock (room.Gate)
            return room.StartGeneration != generation || (!armed && room.AutoStartArmed)
                ? new(CommandStatus.Success, "")
                : new(CommandStatus.InvalidState, "The room could not start.");
    }

    private const string SetCommand = "!set";

    private readonly Dictionary<string, Func<ISession, CancellationToken, ValueTask>> _hostCommands =
        new(StringComparer.OrdinalIgnoreCase) { ["!start"] = start };

    internal async ValueTask<bool> TryHandleAsync(ISession session, string text, CancellationToken ct)
    {
        var normalized = text.Trim();
        if (_dispatcher is not null)
        {
            if (normalized.Length == 0 || normalized[0] is not ('/' or '!' or '！')) return false;
            var identity = session.Feature<IPlayerSession>().Identity;
            var principal = new CommandPrincipal(PrincipalKind.Game, identity.UserNum.ToString(CultureInfo.InvariantCulture));
            var language = session.TryFeature<ISessionLanguage>(out var supplied) ? supplied.LauncherLanguage : "";
            var result = await _dispatcher.ExecuteTextAsync(new(principal, principal, language,
                session.Id.ToString(CultureInfo.InvariantCulture)), normalized, cancellationToken: ct).ConfigureAwait(false);
            if (notices is not null)
                await notices.SendCommandResultAsync(session, result, ct).ConfigureAwait(false);
            return true;
        }
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
    private async ValueTask<CommandResult> SetModeAsync(ISession session, string argument, CancellationToken ct, bool requireHost = true)
    {
        var room = registry.TryGet(session.Id);
        if (room is null)
        {
            await FailAsync(session, "command.set.waiting_only", null, ct).ConfigureAwait(false);
            return new(CommandStatus.InvalidState, "");
        }
        string? failure = null;
        Dictionary<string, string>? failureArguments = null;
        try
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                lock (room.Gate)
                    failure = room.FindByGame(session.Id) is null || (requireHost && room.FindByGame(session.Id)?.Slot != room.MasterSlot)
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
            return new(CommandStatus.InvalidState, "");
        }
        if (failure is not null)
            await FailAsync(session, failure, failureArguments, ct).ConfigureAwait(false);
        return new(failure is null ? CommandStatus.Success : CommandStatus.InvalidState, "");
    }

    private ValueTask FailAsync(ISession session, string key, IReadOnlyDictionary<string, string>? arguments, CancellationToken ct) =>
        notices is null ? ValueTask.CompletedTask : notices.SendAsync(session, key, arguments, ct);
}
