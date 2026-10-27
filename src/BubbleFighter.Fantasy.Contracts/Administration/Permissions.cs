using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Administration;

/// <summary>Separates authenticated accounts from unrelated accounts with the same identifier.</summary>
public enum PrincipalKind
{
    /// <summary>An authenticated game account identified by its persistent account key.</summary>
    Game,
    /// <summary>An authenticated administration account, independent of game OP status.</summary>
    WebUi,
    /// <summary>The server-owned local console; never selected by remote input.</summary>
    Console
}

/// <summary>An authenticated identity supplied by a trusted transport adapter, never by command arguments.</summary>
/// <param name="Kind">The authentication namespace.</param>
/// <param name="Id">The stable account key, rather than a mutable display name.</param>
public sealed record CommandPrincipal(PrincipalKind Kind, string Id);

/// <summary>A permission node owned by a plugin registration.</summary>
/// <param name="Node">A lowercase dot-separated permission identifier.</param>
/// <param name="Description">Human-readable help for permission editors.</param>
/// <param name="DefaultAllowed">Whether authenticated ordinary principals receive this node absent configured rules.</param>
public sealed record PermissionDefinition(string Node, string Description, bool DefaultAllowed = false);

/// <summary>Publishes permission nodes for the lifetime of their owning plugins.</summary>
public interface IPermissionRegistry
{
    /// <summary>Registers a unique node; the owner must track the returned handle with <c>context.Effect</c>.</summary>
    IDisposable Register(PermissionDefinition definition);

    /// <summary>Returns a detached, ordered snapshot of currently registered nodes.</summary>
    IReadOnlyList<PermissionDefinition> List();
}

/// <summary>Evaluates registered permissions using one resolver for game, WebUI and console identities.</summary>
public interface IPermissionService
{
    /// <summary>Checks a registered node; absent nodes and ordinary explicit denials fail closed.</summary>
    bool IsAllowed(CommandPrincipal principal, string node);
}

/// <summary>Reports a completed command without retaining its possibly sensitive argument values.</summary>
/// <param name="Command">The canonical command name, or an empty value when input was invalid.</param>
/// <param name="Owner">The registering row, or an empty value for an unresolved command.</param>
/// <param name="Actor">The original authenticated initiator.</param>
/// <param name="Subject">The effective account whose permissions and state apply.</param>
/// <param name="Status">The outcome classification.</param>
public sealed record CommandCompleted(string Command, string Owner, CommandPrincipal Actor,
    CommandPrincipal Subject, CommandStatus Status) : IEvent;
