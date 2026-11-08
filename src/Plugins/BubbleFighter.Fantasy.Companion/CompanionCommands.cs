using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

internal sealed class CompanionCommands(IPluginContext context, CompanionCommandOptions options,
    TimeProvider? clock = null) : IDisposable
{
    internal const string MessageType = "game.command.execute";
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<(string Launch, string Operation), Entry> _operations = [];
    private bool _closed;

    internal sealed record Reply(string OperationId, string Status, string Code, string Message);

    private sealed class Entry(ISession session, string command)
    {
        internal ISession Session { get; } = session;
        internal string Command { get; } = command;
        internal TaskCompletionSource<Reply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long? CompletedAt { get; set; }
    }

    internal async ValueTask<object?> HandleAsync(CompanionCaller caller, JsonElement payload, CancellationToken ct)
    {
        var (operationId, command) = Read(payload);
        if (caller.Session.Connection.IsClosed || !caller.Session.TryFeature<IPlayerSession>(out var player)
            || !string.Equals(player.Identity.Username, caller.Account, StringComparison.OrdinalIgnoreCase))
            return Rejected(operationId, "invalid_state", "The authenticated game session is no longer available.");
        Entry entry;
        var first = false;
        lock (_gate)
        {
            if (_closed || context.Lifetime.IsCancellationRequested)
                return Rejected(operationId, "cancelled", "The command service stopped.");
            Sweep();
            var key = (caller.LaunchId, operationId);
            if (_operations.TryGetValue(key, out var previous))
            {
                if (!ReferenceEquals(previous.Session, caller.Session) || previous.Command != command)
                    return Rejected(operationId, "operation_conflict", "The operation identifier already belongs to different input.");
                entry = previous;
            }
            else
            {
                if (_operations.Count >= options.MaxOperations)
                    return Rejected(operationId, "busy", "Command operation storage is occupied.");
                entry = new(caller.Session, command);
                _operations.Add(key, entry);
                first = true;
            }
        }
        if (first) await ExecuteAsync(entry, operationId).ConfigureAwait(false);
        return await entry.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private (string Operation, string Command) Read(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Command payload must be an object.");
        var fields = payload.EnumerateObject().ToArray();
        if (fields.Length != 2 || fields.Count(field => field.Name == "operation_id") != 1
            || fields.Count(field => field.Name == "command") != 1
            || payload.GetProperty("operation_id").ValueKind != JsonValueKind.String
            || payload.GetProperty("command").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Command payload requires only operation_id and command strings.");
        var operation = payload.GetProperty("operation_id").GetString()!;
        var command = payload.GetProperty("command").GetString()!;
        if (operation.Length is < 1 or > 64 || operation.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-'))
            || string.IsNullOrWhiteSpace(command) || command.Length > options.MaxInputCharacters || command.Any(char.IsControl))
            throw new InvalidDataException("Command input exceeds its bounds or contains invalid characters.");
        return (operation, command);
    }

    private async Task ExecuteAsync(Entry entry, string operationId)
    {
        Reply reply;
        try
        {
            CommandResult? result = null;
            var task = await context.RunTaskAsync("companion-command", async token =>
            {
                var inputs = context.Services.GetAll<IGameCommandInput>().Where(input => input.OwnsSession(entry.Session)).ToArray();
                if (inputs.Length > 1) throw new InvalidOperationException("Multiple gameplay owners hold the command session.");
                result = inputs.Length == 0
                    ? new CommandResult(CommandStatus.InvalidState, "The player's room is no longer available.")
                    : await inputs[0].ExecuteTextAsync(entry.Session, entry.Command, token).ConfigureAwait(false);
            }, context.Lifetime).ConfigureAwait(false);
            await task.Completion.ConfigureAwait(false);
            reply = FromResult(operationId, result ?? throw new InvalidOperationException("The command returned no result."));
        }
        catch (OperationCanceledException) when (context.Lifetime.IsCancellationRequested)
        {
            reply = Rejected(operationId, "cancelled", "The command service stopped.");
        }
        catch (Exception error)
        {
            context.Logger.LogError(error, "A launch-bound game command failed.");
            reply = new(operationId, "error", "failed", "The command failed.");
        }
        Complete(entry, reply);
    }

    private void Complete(Entry entry, Reply reply)
    {
        lock (_gate)
        {
            entry.CompletedAt = _clock.GetTimestamp();
            entry.Completion.TrySetResult(reply);
        }
    }

    private static Reply FromResult(string operationId, CommandResult result)
    {
        var code = result.Status switch
        {
            CommandStatus.Success => "success", CommandStatus.NotFound => "not_found",
            CommandStatus.InvalidArguments => "invalid_arguments", CommandStatus.Denied => "denied",
            CommandStatus.Busy => "busy", CommandStatus.Cancelled => "cancelled",
            CommandStatus.InvalidState => "invalid_state", CommandStatus.Failed => "failed",
            _ => throw new InvalidOperationException("Unknown command result status."),
        };
        return new(operationId, result.Status == CommandStatus.Success ? "success"
            : result.Status == CommandStatus.Failed ? "error" : "rejected", code, result.Message);
    }

    private static Reply Rejected(string operationId, string code, string message) => new(operationId, "rejected", code, message);

    private void Sweep()
    {
        var now = _clock.GetTimestamp();
        foreach (var pair in _operations.Where(pair => pair.Value.CompletedAt is { } done
            && (pair.Value.Session.Connection.IsClosed
                || _clock.GetElapsedTime(done, now).TotalSeconds >= options.RetentionSeconds)).ToArray())
            _operations.Remove(pair.Key);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            foreach (var pair in _operations)
                pair.Value.Completion.TrySetResult(Rejected(pair.Key.Operation, "cancelled", "The command service stopped."));
            _operations.Clear();
        }
    }
}
