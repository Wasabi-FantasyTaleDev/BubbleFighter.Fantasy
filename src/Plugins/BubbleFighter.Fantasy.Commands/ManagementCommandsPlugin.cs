using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>Registers online counts, announcements and notified disconnects once across all player scopes.</summary>
[Plugin("commands.management", Requires = [typeof(ICommandRegistry), typeof(IPermissionRegistry), typeof(IGameText)],
    Provides = [typeof(IOnlineCommandScopes)])]
public sealed class ManagementCommandsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var service = new OnlineManagement(context, context.Services.Get<IGameText>(),
            context.Config.Bind<ManagementCommandOptions>());
        context.Effect(service);
        context.Effect(context.Services.Register<IOnlineCommandScopes>(service));
        Add("list", "Show the number of online accounts.", true, []);
        Add("notice", "Announce text to every online game connection.", false,
            [new("text", CommandArgumentKind.RemainingText)]);
        Add("disconnect", "Notify and disconnect one online player by nickname.", false,
            [new("player"), new("reason", CommandArgumentKind.RemainingText, true)]);
        return ValueTask.CompletedTask;

        void Add(string name, string description, bool allowed, CommandArgument[] arguments)
        {
            var permission = "game.management." + name;
            context.Effect(context.Services.Get<IPermissionRegistry>().Register(new(permission, description, allowed)));
            context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
                new(name, [], permission, description, arguments), service.ExecuteAsync));
        }
    }
}

/// <summary>Controls management routing storage and the deadline for notification before an explicit disconnect.</summary>
public sealed class ManagementCommandOptions
{
    /// <summary>Gets or sets the maximum concurrently registered player scopes, independent of player count.</summary>
    [Range(1, 8192)] public int MaxScopes { get; set; } = 512;

    /// <summary>Gets or sets the time allowed for the disconnect notification to reach its ordered transport fence.</summary>
    [Range(1, 300000)] public int DisconnectNoticeTimeoutMs { get; set; } = 5000;

    /// <summary>Gets or sets the text budget within the native server notice buffer.</summary>
    /// <remarks>cn-324 sub_78A570 formats into 512 wide characters, including its terminating zero.</remarks>
    [Range(1, 511)] public int MaxNoticeCharacters { get; set; } = 511;
}

internal sealed class OnlineManagement(IPluginContext context, IGameText texts, ManagementCommandOptions options)
    : IOnlineCommandScopes, IDisposable
{
    private sealed class Entry(IPluginContext owner, IOnlineCommandScope scope)
    {
        internal readonly IPluginContext Owner = owner;
        internal readonly IOnlineCommandScope Scope = scope;
        internal bool Registered = true;
    }

    private sealed record Target(Entry Entry, ISession Session, int AccountId, string Nickname);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _scopes = new(StringComparer.Ordinal);
    private bool _disposed;

    public IDisposable Register(IPluginContext owner, IOnlineCommandScope scope)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Scope);
        owner.Lifetime.ThrowIfCancellationRequested();
        var key = scope.Scope;
        var entry = new Entry(owner, scope);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scopes.Count >= options.MaxScopes) throw new InvalidOperationException("Online command scope storage is occupied.");
            if (!_scopes.TryAdd(key, entry)) throw new InvalidOperationException("Online command scope is already registered.");
        }
        return new Registration(() =>
        {
            lock (_gate)
            {
                entry.Registered = false;
                if (_scopes.TryGetValue(key, out var current) && ReferenceEquals(entry, current)) _scopes.Remove(key);
            }
        });
    }

    internal async ValueTask<CommandResult> ExecuteAsync(CommandInvocation invocation, CancellationToken ct)
    {
        var targets = Snapshot();
        var caller = targets.SingleOrDefault(target => target.Session.Id.ToString(CultureInfo.InvariantCulture) == invocation.Caller.SessionId &&
            invocation.Caller.Subject.Kind == PrincipalKind.Game &&
            target.AccountId.ToString(CultureInfo.InvariantCulture) == invocation.Caller.Subject.Id);
        CommandResult Result(CommandStatus status, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
            new(status, caller is null ? texts.Format("", key, arguments) : texts.Format(caller.Session, key, arguments));
        if (invocation.Caller.Subject.Kind == PrincipalKind.Game && caller is null)
            return Result(CommandStatus.InvalidState, "management.player_unavailable");
        if (targets.Length == 0)
        {
            lock (_gate)
                if (_scopes.Count == 0) return Result(CommandStatus.InvalidState, "management.scopes_unavailable");
        }
        if (invocation.Command == "list")
            return Result(CommandStatus.Success, "management.online", Args("count", targets.DistinctBy(t => t.AccountId).Count()));
        if (invocation.Command == "notice")
        {
            var message = invocation.Arguments[0];
            if (message.Length > options.MaxNoticeCharacters)
                return Result(CommandStatus.InvalidArguments, "management.notice_too_long");
            var sent = 0;
            var failed = 0;
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var delivered = false;
                    if (await RunAsync(target, "notice", async token =>
                    {
                        try
                        {
                            await target.Entry.Scope.SendNoticeAsync(target.Session, message, token).ConfigureAwait(false);
                            delivered = true;
                        }
                        catch (IOException ex)
                        {
                            context.Logger.LogError(ex, "Announcement transport failed for account {AccountId} on session {SessionId}.",
                                target.AccountId, target.Session.Id);
                        }
                    }, ct).ConfigureAwait(false) && delivered) sent++;
                    else failed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    context.Logger.LogError(ex, "Announcement delivery failed for account {AccountId} on session {SessionId}.", target.AccountId, target.Session.Id);
                }
            }
            return Result(failed == 0 ? CommandStatus.Success : CommandStatus.Failed, "management.notice_sent",
                new Dictionary<string, string> { ["count"] = sent.ToString(CultureInfo.InvariantCulture), ["failed"] = failed.ToString(CultureInfo.InvariantCulture) });
        }
        if (invocation.Command != "disconnect") throw new InvalidOperationException("Unsupported management command.");
        var matches = targets.Where(target => string.Equals(target.Nickname, invocation.Arguments[0], StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            return Result(CommandStatus.InvalidState, matches.Length == 0 ? "management.player_unavailable" : "management.player_ambiguous");
        var selected = matches[0];
        var reason = invocation.Arguments.Count == 2
            ? texts.Format(selected.Session, "management.disconnect_reason", new Dictionary<string, string> { ["reason"] = invocation.Arguments[1] })
            : texts.Format(selected.Session, "management.disconnect_default");
        if (reason.Length > options.MaxNoticeCharacters)
            return Result(CommandStatus.InvalidArguments, "management.notice_too_long");
        var notificationFailed = false;
        var closed = false;
        var current = await RunAsync(selected, "disconnect", async token =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(options.DisconnectNoticeTimeoutMs);
            try
            {
                await selected.Entry.Scope.SendNoticeAsync(selected.Session, reason, deadline.Token).ConfigureAwait(false);
                if (selected.Session is not ISessionOutputDrain drain)
                    throw new NotSupportedException("The selected session cannot drain its outbound transport.");
                await drain.DrainOutputAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                notificationFailed = true;
                context.Logger.LogWarning(ex, "Disconnect notification could not complete for account {AccountId} on session {SessionId}.",
                    selected.AccountId, selected.Session.Id);
            }
            // Keep the selected connection; never follow a replacement login after notification.
            if (Current(selected))
            {
                await selected.Session.Connection.CloseAsync("administrative disconnect").ConfigureAwait(false);
                closed = true;
            }
        }, ct).ConfigureAwait(false);
        if (!current || !closed) return Result(CommandStatus.InvalidState, "management.player_unavailable");
        if (caller is not null && ReferenceEquals(caller.Session, selected.Session))
            return new(CommandStatus.Success, "");
        return Result(CommandStatus.Success, notificationFailed ? "management.disconnected_without_notice" : "management.disconnected");
    }

    private Target[] Snapshot()
    {
        Entry[] entries;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entries = [.. _scopes.Values.Where(entry => !entry.Owner.Lifetime.IsCancellationRequested)];
        }
        var targets = new Dictionary<long, Target>();
        foreach (var entry in entries)
            foreach (var session in entry.Scope.Snapshot().ToArray())
            {
                if (!session.TryFeature<IPlayerSession>(out var player)) continue;
                var target = new Target(entry, session, player.Identity.UserNum, player.Identity.Nickname);
                if (!Current(target)) continue;
                if (!targets.TryAdd(session.Id, target))
                    throw new InvalidOperationException("More than one online scope owns a command session.");
            }
        return [.. targets.Values];
    }

    private bool Current(Target target)
    {
        lock (_gate)
            if (_disposed || !target.Entry.Registered || target.Entry.Owner.Lifetime.IsCancellationRequested) return false;
        return target.Entry.Scope.IsCurrent(target.Session, target.AccountId);
    }

    private async ValueTask<bool> RunAsync(Target target, string operation, Func<CancellationToken, ValueTask> action, CancellationToken ct)
    {
        if (!Current(target)) return false;
        var executed = false;
        try
        {
            var owned = await target.Entry.Owner.RunTaskAsync("online-command:" + operation, async token =>
            {
                if (!Current(target)) return;
                executed = true;
                await action(token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await owned.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (target.Entry.Owner.Lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
        { return false; }
        return executed;
    }

    private static IReadOnlyDictionary<string, string> Args(string key, int value) =>
        new Dictionary<string, string> { [key] = value.ToString(CultureInfo.InvariantCulture) };

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var entry in _scopes.Values) entry.Registered = false;
            _scopes.Clear();
        }
    }
}
