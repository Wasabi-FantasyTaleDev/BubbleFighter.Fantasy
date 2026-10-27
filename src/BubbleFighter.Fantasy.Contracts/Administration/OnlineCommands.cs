using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Administration;

/// <summary>Identifies authenticated game connections and sends notices through one deployment scope's dialect.</summary>
public interface IOnlineCommandScope
{
    /// <summary>Gets the unique registration scope, independent of client region and command names.</summary>
    string Scope { get; }

    /// <summary>Returns a detached snapshot of live connections held by this scope's authoritative player directory.</summary>
    IReadOnlyList<ISession> Snapshot();

    /// <summary>Checks that this exact connection still holds the expected authenticated account.</summary>
    bool IsCurrent(ISession session, int accountId);

    /// <summary>Queues an already localized notice using the scope's configured game protocol.</summary>
    ValueTask SendNoticeAsync(ISession session, string text, CancellationToken cancellationToken);
}

/// <summary>Connects independently owned online-player scopes to globally registered management commands.</summary>
public interface IOnlineCommandScopes
{
    /// <summary>Registers one scope and returns its removal handle for the owner's effects.</summary>
    IDisposable Register(IPluginContext owner, IOnlineCommandScope scope);
}
