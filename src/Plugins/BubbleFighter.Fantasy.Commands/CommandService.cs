using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Administration;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>
/// The shared command registry and dispatcher behind console, chat and management entry points.
/// Registration is capacity-bounded and name/alias-unique case-insensitively, requires the command's
/// permission node to be registered first, and hands back a removal handle that cancels in-flight
/// invocations. Execution validates and snapshots the caller's input before its first await, admits
/// under the registry gate (unknown, schema mismatch, denied, cancelled and capacity-occupied inputs
/// are rejected without running anything), and then runs the handler inside the registering owner's
/// supervised runtime, so an unloading row cannot execute commands and the owner observes handler
/// faults. Exactly one <see cref="CommandCompleted"/> event carries the final status, whether the
/// command ran or was rejected.
/// </summary>
internal sealed class CommandService(IPluginContext context, IPermissionService permissions, IPermissionRegistry nodes, CommandOptions options)
    : ICommandRegistry, ICommandExecutor, IDisposable
{
    /// <summary>One registration: its owner row, validated definition, handler, and the cancellation state shared with in-flight invocations.</summary>
    private sealed class Entry(IPluginContext owner, CommandDefinition definition, CommandHandler handler)
    {
        internal readonly IPluginContext Owner = owner;
        internal readonly CommandDefinition Definition = definition;
        internal readonly CommandHandler Handler = handler;
        internal readonly CancellationTokenSource Stopping = new();
        internal int Active;
        internal bool Closed;
        internal bool CancellationComplete;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Entry> _entries = [];
    private int _active;
    private bool _disposed;

    /// <summary>
    /// Registers one command for the owner's row lifetime. The permission node must already be
    /// registered, names and aliases must be distinct identifiers, and the argument schema must list
    /// required arguments before optional ones with any remaining-text argument last.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the node is unregistered, the capacity is full, or a name or alias is taken.</exception>
    /// <exception cref="ArgumentException">Thrown for malformed metadata.</exception>
    public IDisposable Register(IPluginContext owner, CommandDefinition definition, CommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        owner.Lifetime.ThrowIfCancellationRequested();
        var snapshot = Snapshot(definition);
        if (!nodes.List().Any(node => node.Node == snapshot.Permission))
            throw new InvalidOperationException("Register the command's permission node before registering the command.");
        var names = snapshot.Aliases.Prepend(snapshot.Name).ToArray();
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Count >= options.MaxCommands) throw new InvalidOperationException("Command registry capacity is occupied.");
            if (names.Any(_names.ContainsKey)) throw new InvalidOperationException("A command name or alias is already registered.");
            entry = new(owner, snapshot, handler);
            foreach (var name in names) _names.Add(name, entry);
            _entries.Add(entry);
        }
        return new Registration(() => Remove(entry));
    }

    /// <summary>Returns the commands the principal may see, detached and ordered by name; entries whose owner row is unloading are omitted.</summary>
    public IReadOnlyList<CommandDefinition> List(CommandPrincipal principal)
    {
        Names.ValidatePrincipal(principal);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Array.AsReadOnly(_entries.Where(e => !e.Owner.Lifetime.IsCancellationRequested && permissions.IsAllowed(principal, e.Definition.Permission))
                .Select(e => e.Definition).OrderBy(d => d.Name, StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>
    /// Executes one positional invocation. The argument array is snapshotted before the first await,
    /// so a caller mutating its list cannot change an admitted operation. The handler runs under the
    /// registering row's runtime with the caller's token, the entry's stopping token and the plugin
    /// lifetime all observed; a handler fault surfaces as <see cref="CommandStatus.Failed"/> after its
    /// owner has seen the exception.
    /// </summary>
    public async ValueTask<CommandResult> ExecuteAsync(CommandCaller caller, string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ValidateCaller(caller);
        if (command is null || arguments is null || command.Length > 64 || !Names.Identifier(command.ToLowerInvariant()) ||
            arguments.Count > options.MaxInputCharacters || command.Length + arguments.Count + arguments.Sum(a => (long)(a?.Length ?? 0)) > options.MaxInputCharacters ||
            arguments.Any(a => a is null || a.Any(char.IsControl)))
            return Complete(caller, "", "", new(CommandStatus.InvalidArguments, "Invalid command input."));
        // Snapshot arguments before the first await so callers cannot change a checked operation.
        var input = Array.AsReadOnly(arguments.ToArray());
        Entry? entry;
        CommandResult? rejected = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _names.TryGetValue(command, out entry);
            if (entry is null) rejected = new(CommandStatus.NotFound, "Unknown command.");
            else if (!ValidArguments(entry.Definition, input)) rejected = new(CommandStatus.InvalidArguments, "Arguments do not match the command schema.");
            else if (!permissions.IsAllowed(caller.Subject, entry.Definition.Permission)) rejected = new(CommandStatus.Denied, "Permission denied.");
            else if (entry.Owner.Lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) rejected = new(CommandStatus.Cancelled, "Command cancelled.");
            else if (_active >= options.MaxConcurrent) rejected = new(CommandStatus.Busy, "Command execution capacity is occupied.");
            else { _active++; entry.Active++; }
        }
        if (rejected is not null) return Complete(caller, entry?.Definition.Name ?? "", entry?.Owner.Row.Id ?? "", rejected);
        var admitted = entry!;
        var status = CommandStatus.Failed;
        try
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, admitted.Stopping.Token, context.Lifetime);
            CommandResult? result = null;
            IOwnedTask? task = null;
            try
            {
                task = await admitted.Owner.RunTaskAsync("command:" + admitted.Definition.Name, async token =>
                {
                    result = await admitted.Handler(new(caller, admitted.Definition.Name, input), token).ConfigureAwait(false);
                    if (result is null || !Enum.IsDefined(result.Status) || result.Message is null || result.Message.Length > options.MaxResultCharacters)
                        throw new InvalidOperationException("The command handler returned an invalid or oversized result.");
                }, stopping.Token).ConfigureAwait(false);
                await task.Completion.ConfigureAwait(false);
                status = result!.Status;
                return result;
            }
            catch (OperationCanceledException) when ((task is null || task.Completion.IsCanceled) &&
                (stopping.IsCancellationRequested || admitted.Owner.Lifetime.IsCancellationRequested))
            {
                status = CommandStatus.Cancelled;
                return new(status, "Command cancelled.");
            }
            catch (ObjectDisposedException) when (task is null && admitted.Owner.Lifetime.IsCancellationRequested)
            {
                status = CommandStatus.Cancelled;
                return new(status, "Command owner is unloading.");
            }
        }
        finally
        {
            lock (_gate)
            {
                _active--;
                admitted.Active--;
                if (admitted.Closed && admitted.CancellationComplete && admitted.Active == 0) admitted.Stopping.Dispose();
            }
            context.Events.Emit(new CommandCompleted(admitted.Definition.Name, admitted.Owner.Row.Id, caller.Actor, caller.Subject, status));
        }
    }

    /// <summary>
    /// Parses and executes one text line. When required, the line must start with <c>/</c>,
    /// <c>!</c> or the full-width <c>！</c> (U+FF01); tokens split on unquoted whitespace with
    /// double-quote grouping, and a trailing remaining-text argument receives the rest of the line
    /// joined back with single spaces.
    /// </summary>
    public ValueTask<CommandResult> ExecuteTextAsync(CommandCaller caller, string text, bool requirePrefix = true, CancellationToken cancellationToken = default)
    {
        ValidateCaller(caller);
        if (text is null || text.Length > options.MaxInputCharacters || text.Any(char.IsControl) || !TryTokens(text, requirePrefix, out var tokens))
            return ValueTask.FromResult(Complete(caller, "", "", new(CommandStatus.InvalidArguments, "Invalid command text or missing prefix.")));
        string[] arguments = tokens.Skip(1).ToArray();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_names.TryGetValue(tokens[0], out var entry) && entry.Definition.Arguments.LastOrDefault()?.Kind == CommandArgumentKind.RemainingText)
            {
                var rest = entry.Definition.Arguments.Count - 1;
                if (arguments.Length > rest) arguments = [.. arguments.Take(rest), string.Join(' ', arguments.Skip(rest))];
            }
        }
        return ExecuteAsync(caller, tokens[0], arguments, cancellationToken);
    }

    private CommandResult Complete(CommandCaller caller, string name, string owner, CommandResult result)
    {
        context.Events.Emit(new CommandCompleted(name, owner, caller.Actor, caller.Subject, result.Status));
        return result;
    }

    private CommandDefinition Snapshot(CommandDefinition definition)
    {
        if (!Names.Identifier(definition.Name) || !Names.Node(definition.Permission) || !Names.Description(definition.Description) ||
            definition.Aliases is null || definition.Arguments is null || definition.Aliases.Count > options.MaxInputCharacters || definition.Arguments.Count > options.MaxInputCharacters ||
            definition.Aliases.Sum(a => (long)(a?.Length ?? 0) + 1) + definition.Arguments.Sum(a => (long)(a?.Name?.Length ?? 0) + 1) > options.MaxInputCharacters)
            throw new ArgumentException("Command metadata is invalid.", nameof(definition));
        var aliases = definition.Aliases.ToArray();
        var args = definition.Arguments.ToArray();
        if (aliases.Any(a => !Names.Identifier(a)) || aliases.Prepend(definition.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Length + 1)
            throw new ArgumentException("Command names and aliases must be valid and distinct.", nameof(definition));
        var optional = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is null || !Names.Identifier(arg.Name) || !names.Add(arg.Name) || !Enum.IsDefined(arg.Kind) || (optional && !arg.Optional) ||
                (arg.Kind == CommandArgumentKind.RemainingText && i != args.Length - 1))
                throw new ArgumentException("Command argument schema is invalid.", nameof(definition));
            optional |= arg.Optional;
        }
        return definition with { Aliases = Array.AsReadOnly(aliases), Arguments = Array.AsReadOnly(args) };
    }

    private static bool ValidArguments(CommandDefinition definition, IReadOnlyList<string> args)
    {
        if (args.Count > definition.Arguments.Count || args.Count < definition.Arguments.Count(a => !a.Optional)) return false;
        for (var i = 0; i < args.Count; i++)
            if (string.IsNullOrWhiteSpace(args[i]) || (definition.Arguments[i].Kind == CommandArgumentKind.Integer &&
                !long.TryParse(args[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))) return false;
        return true;
    }

    private bool TryTokens(string text, bool requirePrefix, out List<string> tokens)
    {
        tokens = [];
        var span = text.AsSpan().Trim();
        if (span.IsEmpty) return false;
        if (span[0] is '/' or '!' or '\uFF01') span = span[1..];
        else if (requirePrefix) return false;
        var token = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var ch in span)
        {
            if (char.IsControl(ch)) return false;
            if (ch == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started) { tokens.Add(token.ToString()); token.Clear(); started = false; }
            }
            else { token.Append(ch); started = true; }
        }
        if (quoted) return false;
        if (started) tokens.Add(token.ToString());
        return tokens.Count > 0;
    }

    private static void ValidateCaller(CommandCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Names.ValidatePrincipal(caller.Subject);
        Names.ValidatePrincipal(caller.Actor);
        if (caller.Language is null || (caller.Language.Length > 0 && !Names.Identity(caller.Language)) ||
            (caller.SessionId is not null && !Names.Identity(caller.SessionId)))
            throw new ArgumentException("Command caller metadata is invalid.", nameof(caller));
    }

    /// <summary>Removes the registration, cancels in-flight invocations, and releases the entry's cancellation source once the last invocation drains.</summary>
    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            if (entry.Closed) return;
            entry.Closed = true;
            _entries.Remove(entry);
            foreach (var name in entry.Definition.Aliases.Prepend(entry.Definition.Name)) _names.Remove(name);
        }
        try { entry.Stopping.Cancel(); }
        finally
        {
            lock (_gate)
            {
                entry.CancellationComplete = true;
                if (entry.Active == 0) entry.Stopping.Dispose();
            }
        }
    }

    /// <summary>Unregisters every command, cancelling in-flight invocations; cancellation faults aggregate rather than skipping the remaining entries.</summary>
    public void Dispose()
    {
        Entry[] entries;
        lock (_gate) { if (_disposed) return; _disposed = true; entries = _entries.ToArray(); }
        List<Exception> failures = [];
        foreach (var entry in entries)
            try { Remove(entry); }
            catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException("Command registration cancellation failed.", failures);
    }
}
