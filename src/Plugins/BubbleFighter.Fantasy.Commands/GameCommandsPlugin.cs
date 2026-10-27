using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>Publishes one set of room commands and routes them to independently owned gameplay scopes.</summary>
[Plugin("commands.game", Requires = [typeof(ICommandRegistry), typeof(IPermissionRegistry)],
    Provides = [typeof(IGameCommandEndpoints)])]
public sealed class GameCommandsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var routes = new GameCommandEndpoints(context.Config.Bind<GameCommandOptions>().MaxEndpoints);
        context.Effect(routes);
        context.Effect(context.Services.Register<IGameCommandEndpoints>(routes));
        var nodes = context.Services.Get<IPermissionRegistry>();
        var commands = context.Services.Get<ICommandRegistry>();
        Add("start", [], "Start the current waiting room.", []);
        Add("mode", ["set"], "Change the current waiting room's mode.", [new("mode", CommandArgumentKind.Integer)]);
        return ValueTask.CompletedTask;

        void Add(string name, string[] aliases, string description, CommandArgument[] arguments)
        {
            var permission = "game.room." + name;
            context.Effect(nodes.Register(new(permission, description, true)));
            context.Effect(commands.Register(context, new(name, aliases, permission, description, arguments), routes.ExecuteAsync));
        }
    }
}

/// <summary>Controls storage reserved for independently registered gameplay scopes.</summary>
public sealed class GameCommandOptions
{
    /// <summary>Gets or sets the maximum live endpoint registrations, not the number of players or commands.</summary>
    [Range(1, 8192)] public int MaxEndpoints { get; set; } = 512;
}

internal sealed class GameCommandEndpoints(int capacity) : IGameCommandEndpoints, IDisposable
{
    private sealed record Entry(string Scope, IPluginContext Owner, IGameCommandEndpoint Endpoint);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _routes = new(StringComparer.Ordinal);
    private bool _disposed;

    public IDisposable Register(IPluginContext owner, IGameCommandEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Scope);
        owner.Lifetime.ThrowIfCancellationRequested();
        var scope = endpoint.Scope;
        var entry = new Entry(scope, owner, endpoint);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_routes.Count >= capacity) throw new InvalidOperationException("Game command endpoint storage is occupied.");
            if (!_routes.TryAdd(scope, entry)) throw new InvalidOperationException("Game command scope is already registered.");
        }
        return new Registration(() =>
        {
            lock (_gate)
                if (_routes.TryGetValue(scope, out var current) && ReferenceEquals(current, entry)) _routes.Remove(scope);
        });
    }

    internal async ValueTask<CommandResult> ExecuteAsync(CommandInvocation invocation, CancellationToken ct)
    {
        if (invocation.Caller.Subject.Kind != PrincipalKind.Game ||
            !long.TryParse(invocation.Caller.SessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
            return new(CommandStatus.InvalidState, "A connected player in a waiting room is required.");
        Entry[] entries;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entries = [.. _routes.Values.Where(entry => !entry.Owner.Lifetime.IsCancellationRequested)];
        }
        Entry? match = null;
        ISession? target = null;
        foreach (var entry in entries)
        {
            var session = entry.Endpoint.FindSession(sessionId);
            if (session is null) continue;
            if (match is not null) throw new InvalidOperationException("More than one gameplay scope owns the command session.");
            match = entry;
            target = session;
        }
        if (match is null || !Current(match) || target is null || target.Connection.IsClosed ||
            !target.TryFeature<IPlayerSession>(out var player) ||
            player.Identity.UserNum.ToString(CultureInfo.InvariantCulture) != invocation.Caller.Subject.Id)
            return new(CommandStatus.InvalidState, "The player's room is no longer available.");
        CommandResult? result = null;
        try
        {
            match.Owner.Lifetime.ThrowIfCancellationRequested();
            var operation = await match.Owner.RunTaskAsync("game-command:" + invocation.Command, async token =>
            {
                // A lookup snapshot cannot keep a removed or replaced registration callable.
                if (!Current(match))
                {
                    result = new(CommandStatus.InvalidState, "The gameplay scope is no longer available.");
                    return;
                }
                result = await match.Endpoint.ExecuteAsync(target, invocation, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await operation.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (match.Owner.Lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new(CommandStatus.Cancelled, "The gameplay owner stopped.");
        }
        return result ?? throw new InvalidOperationException("The gameplay command returned no result.");
    }

    private bool Current(Entry entry)
    {
        lock (_gate)
            return !_disposed && !entry.Owner.Lifetime.IsCancellationRequested &&
                _routes.TryGetValue(entry.Scope, out var current) && ReferenceEquals(entry, current);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _routes.Clear(); }
    }
}
