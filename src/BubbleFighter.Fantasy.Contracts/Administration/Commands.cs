using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Administration;

/// <summary>The supported argument validation rules shared by text and graphical adapters.</summary>
public enum CommandArgumentKind
{
    /// <summary>A single nonempty token.</summary>
    Text,
    /// <summary>A signed invariant-culture 64-bit integer.</summary>
    Integer,
    /// <summary>The final argument consumes all remaining text tokens.</summary>
    RemainingText
}

/// <summary>Declares one positional command argument.</summary>
/// <param name="Name">The unique argument name used in help.</param>
/// <param name="Kind">The validation and text-consumption rule.</param>
/// <param name="Optional">Whether the argument may be omitted; required arguments precede optional ones.</param>
public sealed record CommandArgument(string Name, CommandArgumentKind Kind = CommandArgumentKind.Text, bool Optional = false);

/// <summary>Describes a command independently of its input transport.</summary>
/// <param name="Name">The unique lowercase canonical name.</param>
/// <param name="Aliases">Additional unique names resolving to the same registration.</param>
/// <param name="Permission">A registered permission node required before execution.</param>
/// <param name="Description">Help text for users and graphical panels.</param>
/// <param name="Arguments">The ordered positional schema.</param>
public sealed record CommandDefinition(string Name, IReadOnlyList<string> Aliases, string Permission,
    string Description, IReadOnlyList<CommandArgument> Arguments);

/// <summary>Server-established identity and presentation information for one invocation.</summary>
/// <param name="Subject">The effective authenticated principal.</param>
/// <param name="Actor">The original authenticated initiator, preserved during delegated execution.</param>
/// <param name="Language">The user's UI language, independent of client build; empty requests the deployment fallback.</param>
/// <param name="SessionId">The transport's verified game session identifier, when applicable.</param>
public sealed record CommandCaller(CommandPrincipal Subject, CommandPrincipal Actor, string Language = "en", string? SessionId = null);

/// <summary>The normalized, read-only input delivered to a plugin handler.</summary>
/// <param name="Caller">The verified invocation identity.</param>
/// <param name="Command">The canonical registration name.</param>
/// <param name="Arguments">Validated positional arguments; remaining text occupies a single entry.</param>
public sealed record CommandInvocation(CommandCaller Caller, string Command, IReadOnlyList<string> Arguments);

/// <summary>Distinguishes normal user errors from exceptional plugin failures.</summary>
public enum CommandStatus
{
    /// <summary>The command handler completed successfully.</summary>
    Success,
    /// <summary>No registered command matched the requested name.</summary>
    NotFound,
    /// <summary>The invocation did not match the registered argument schema or input limits.</summary>
    InvalidArguments,
    /// <summary>The effective principal lacks the registered permission.</summary>
    Denied,
    /// <summary>The configured concurrent execution capacity is occupied.</summary>
    Busy,
    /// <summary>The command is no longer available or execution was cancelled.</summary>
    Cancelled,
    /// <summary>The handler rejected the operation because current application state does not permit it.</summary>
    InvalidState,
    /// <summary>The handler faulted; the exception remains observable to its caller and owner.</summary>
    Failed
}

/// <summary>A bounded user-facing result returned by a command handler or the dispatcher.</summary>
/// <param name="Status">The outcome classification.</param>
/// <param name="Message">Presentation text; handlers must not expose secrets.</param>
public sealed record CommandResult(CommandStatus Status, string Message);

/// <summary>Runs inside the registering plugin's supervised lifetime and observes its cancellation token.</summary>
public delegate ValueTask<CommandResult> CommandHandler(CommandInvocation invocation, CancellationToken cancellationToken);

/// <summary>Registers native plugin callbacks and exposes permission-filtered command metadata.</summary>
public interface ICommandRegistry
{
    /// <summary>Atomically registers a name and aliases; track the handle with the supplied owner's <c>Effect</c>.</summary>
    IDisposable Register(IPluginContext owner, CommandDefinition definition, CommandHandler handler);

    /// <summary>Returns detached, permitted registrations, once per canonical name.</summary>
    IReadOnlyList<CommandDefinition> List(CommandPrincipal principal);
}

/// <summary>Dispatches validated command input without evaluating shell code.</summary>
public interface ICommandExecutor
{
    /// <summary>Executes positional UI arguments; a remaining-text argument is supplied as one value.</summary>
    ValueTask<CommandResult> ExecuteAsync(CommandCaller caller, string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>Parses quoted text; when required, only '/', '!' or U+FF01 admits a command.</summary>
    ValueTask<CommandResult> ExecuteTextAsync(CommandCaller caller, string text, bool requirePrefix = true, CancellationToken cancellationToken = default);
}
