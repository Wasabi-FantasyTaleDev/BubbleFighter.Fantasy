using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
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
/// <param name="udpEnabled">Whether configured UDP transports may be selected by room commands.</param>
internal sealed class GameCommandHandler(RoomRegistry registry,
    Func<ISession, CancellationToken, ValueTask> start,
    RoomNotices? notices = null,
    Func<PlayRoom, ushort, CancellationToken, ValueTask<bool>>? setMode = null,
    bool canStart = true, bool udpEnabled = false) : IGameCommandEndpoint, IGameCommandInput
{
    private ICommandExecutor? _dispatcher;
    private IPluginContext? _owner;

    internal Func<ISession, CancellationToken, bool>? AdmitInput { get; set; }

    internal bool SharedDispatch => _dispatcher is not null;

    /// <summary>Gets or sets the live UDP status providers of this row's game protocol, read when a status is asked.</summary>
    internal Func<IReadOnlyList<IUdpTransportStatus>>? UdpStatus { get; set; }

    /// <summary>Gets or sets the departure <c>!plaza</c> runs: a disconnect's seat release, then the plaza.</summary>
    internal Func<ISession, CancellationToken, ValueTask<bool>>? LeaveToPlaza { get; set; }

    internal Func<ISession, CancellationToken, ValueTask<bool>>? LeavePlayingMatchToPlaza { get; set; }

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
        _owner = context;
        context.Effect(endpoints!.Register(context, this));
        context.Effect(context.Services.RegisterMany<IGameCommandInput>(this));
    }

    /// <inheritdoc />
    public bool OwnsSession(ISession session) => _owner is { Lifetime.IsCancellationRequested: false }
        && !session.Connection.IsClosed && ReferenceEquals(FindSession(session.Id), session);

    /// <inheritdoc />
    public async ValueTask<CommandResult> ExecuteTextAsync(ISession session, string command, CommandSource source, CancellationToken cancellationToken)
    {
        if (!OwnsSession(session) || _dispatcher is null || AdmitInput is null
            || !session.TryFeature<IPlayerSession>(out _))
            return new(CommandStatus.InvalidState, "The player's room is no longer available.");
        var owner = _owner!;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime, cancellationToken);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!OwnsSession(session)) return new(CommandStatus.InvalidState, "The player's room is no longer available.");
            if (!AdmitInput(session, lifetime.Token))
                return new(CommandStatus.Busy, "The shared chat and command rate limit is occupied.");
            // The dispatcher and gameplay endpoint supervise execution; admission adds no enclosing task on that same owner.
            return await DispatchTextAsync(session, command, source, lifetime.Token, requireMatchDeparture: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (lifetime.IsCancellationRequested && error.CancellationToken == lifetime.Token)
        {
            return new(CommandStatus.Cancelled, "The gameplay command was cancelled.");
        }
        catch (ObjectDisposedException) when (owner.Lifetime.IsCancellationRequested)
        {
            return new(CommandStatus.Cancelled, "The gameplay owner stopped.");
        }
    }

    private ValueTask<CommandResult> DispatchTextAsync(ISession session, string command, CommandSource source, CancellationToken ct, bool requireMatchDeparture = false)
    {
        var identity = session.Feature<IPlayerSession>().Identity;
        var principal = new CommandPrincipal(PrincipalKind.Game, identity.UserNum.ToString(CultureInfo.InvariantCulture));
        var language = session.TryFeature<ISessionLanguage>(out var supplied) ? supplied.LauncherLanguage : "";
        return _dispatcher!.ExecuteTextAsync(new CommandCaller(principal, principal, language,
            session.Id.ToString(CultureInfo.InvariantCulture)) { Source = source, RequireMatchDeparture = requireMatchDeparture },
            command, requirePrefix: false, cancellationToken: ct);
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
        if (invocation.Command == "udp" && invocation.Arguments is [var only] && IsStatus(only))
            return DescribeUdp(session);
        if (invocation.Command == "plaza")
            return await LeavePlazaAsync(session, cancellationToken, invocation.Caller.RequireMatchDeparture).ConfigureAwait(false);
        if (invocation.Command == "udp")
            return await SetUdpAsync(session, invocation.Arguments, CommandText.DisplayPrefix(invocation.Caller), cancellationToken).ConfigureAwait(false);
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

    private static bool IsStatus(string argument) => argument.Equals("status", StringComparison.OrdinalIgnoreCase);

    // Any member may ask; it reads the room under its lock and each member's path from the build's UDP coordinator.
    private CommandResult DescribeUdp(ISession session)
    {
        var room = registry.TryGet(session.Id);
        if (room is null) return new(CommandStatus.InvalidState, "A room is required.");
        (string Nickname, ISession Game)[] seats;
        bool? forced;
        bool match;
        lock (room.Gate)
        {
            if (room.FindByGame(session.Id) is null) return new(CommandStatus.InvalidState, "A room is required.");
            seats = [.. room.Seats.Select(seat => (seat.Nickname, seat.Game))];
            forced = room.UdpOverride;
            match = room.IsMatchRoom;
        }
        if (!udpEnabled) return new(CommandStatus.Success, "UDP: off on this server. Every room uses TCP.");
        var providers = UdpStatus?.Invoke() ?? [];
        if (providers.Count == 0) return new(CommandStatus.Success, "UDP: not available for this client version on this server. TCP only.");
        var provider = providers[0];
        var open = seats.Count(seat => !seat.Game.Connection.IsClosed);
        var preference = !match ? "not a gameplay room, TCP"
            : forced is { } value ? value ? "forced on" : "forced off"
            : open > 1 ? "on (multiplayer)" : "off (single player; !udp on forces it)";
        var members = seats.Select(seat =>
        {
            var status = provider.Describe(seat.Game);
            return $"{seat.Nickname} {status.State.ToString().ToUpperInvariant()} ({status.Detail})";
        });
        return new(CommandStatus.Success, $"UDP {provider.Kind}, room {preference}: {string.Join("; ", members)}");
    }

    private async ValueTask<CommandResult> LeavePlazaAsync(ISession session, CancellationToken ct, bool requirePlayingMatch = false)
    {
        var leave = requirePlayingMatch ? LeavePlayingMatchToPlaza : LeaveToPlaza;
        if (leave is null) return new(CommandStatus.InvalidState, "Leaving to the plaza is unavailable in this deployment.");
        return await leave(session, ct).ConfigureAwait(false)
            ? new(CommandStatus.Success, "")
            : new(CommandStatus.InvalidState, requirePlayingMatch ? "An ordinary playing match is required." : "A room is required.");
    }

    private async ValueTask<CommandResult> SetUdpAsync(ISession session, IReadOnlyList<string> arguments, string displayPrefix, CancellationToken ct)
    {
        if (arguments.Count > 1 || arguments.Count == 1 && !arguments[0].Equals("on", StringComparison.OrdinalIgnoreCase)
            && !arguments[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            return new(CommandStatus.InvalidArguments, $"Usage: {displayPrefix}udp [on|off|status]");
        if (!udpEnabled)
            return new(CommandStatus.InvalidState, "UDP is disabled or unavailable in this deployment; the room uses TCP.");
        var room = registry.TryGet(session.Id);
        if (room is null) return new(CommandStatus.InvalidState, "A room is required.");
        CommandResult result = new(CommandStatus.InvalidState, "The room is no longer available.");
        await room.Executor.ExecuteAsync(_ =>
        {
            lock (room.Gate)
            {
                if (room.Phase == RoomPhase.Closed || room.FindByGame(session.Id) is not { } seat) return ValueTask.CompletedTask;
                if (!room.IsMatchRoom)
                {
                    result = new(CommandStatus.InvalidState, "UDP can only be configured in a gameplay room.");
                    return ValueTask.CompletedTask;
                }
                if (seat.Slot != room.MasterSlot)
                {
                    result = new(CommandStatus.Denied, "Only the room host can change its UDP preference.");
                    return ValueTask.CompletedTask;
                }
                var current = room.UdpOverride ?? room.Seats.Count(member => !member.Game.Connection.IsClosed) > 1;
                var enabled = arguments.Count == 0 ? !current : arguments[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                room.UdpOverride = enabled;
                result = new(CommandStatus.Success, enabled
                    ? "Room UDP: ON (forced, including single-player rooms). TCP remains available if UDP cannot be verified."
                    : "Room UDP: OFF. The room uses TCP.");
            }
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
        return result;
    }

    internal async ValueTask<bool> TryHandleAsync(ISession session, string text, CancellationToken ct)
    {
        if (!CommandText.TryNormalize(text, requirePrefix: true, out var content)) return false;
        if (_dispatcher is not null)
        {
            var result = await DispatchTextAsync(session, content, CommandSource.InGameChat, ct).ConfigureAwait(false);
            if (notices is not null)
                await notices.SendCommandResultAsync(session, result, ct).ConfigureAwait(false);
            return true;
        }
        var split = 0;
        while (split < content.Length && !char.IsWhiteSpace(content[split])) split++;
        var command = content[..split];
        var argument = content[split..].Trim();
        if (command.Equals("plaza", StringComparison.OrdinalIgnoreCase) && argument.Length == 0)
        {
            var result = await LeavePlazaAsync(session, ct).ConfigureAwait(false);
            if (notices is not null) await notices.SendCommandResultAsync(session, result, ct).ConfigureAwait(false);
            return true;
        }
        if (command.Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var result = arguments is [var only] && IsStatus(only)
                ? DescribeUdp(session)
                : await SetUdpAsync(session, arguments, "!", ct).ConfigureAwait(false);
            if (notices is not null) await notices.SendCommandResultAsync(session, result, ct).ConfigureAwait(false);
            return true;
        }
        if (setMode is not null && (command.Equals("set", StringComparison.OrdinalIgnoreCase)
            || command.Equals("mode", StringComparison.OrdinalIgnoreCase)))
        {
            await SetModeAsync(session, argument, ct).ConfigureAwait(false);
            return true;
        }
        if (!command.Equals("start", StringComparison.OrdinalIgnoreCase) || argument.Length != 0)
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
            await start(session, ct).ConfigureAwait(false);
        else if (notices is not null)
            await notices.SendAsync(session, failure, null, ct).ConfigureAwait(false);
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
