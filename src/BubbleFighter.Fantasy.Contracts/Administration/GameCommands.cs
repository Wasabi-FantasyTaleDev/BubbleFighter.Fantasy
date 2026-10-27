using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Administration;

/// <summary>Routes shared room commands to the gameplay owner that currently holds a player's seat.</summary>
public interface IGameCommandEndpoint
{
    /// <summary>Gets the deployment's unique gameplay scope, independent of command names.</summary>
    string Scope { get; }

    /// <summary>Finds the seated game connection, or null when this owner does not hold it.</summary>
    ISession? FindSession(long sessionId);

    /// <summary>Revalidates membership and performs the command on the room's serialized executor.</summary>
    ValueTask<CommandResult> ExecuteAsync(ISession session, CommandInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Registers per-scope room operations without registering duplicate global command names.</summary>
public interface IGameCommandEndpoints
{
    /// <summary>Registers one scope; transfer its removal handle to the gameplay owner's effects.</summary>
    IDisposable Register(IPluginContext owner, IGameCommandEndpoint endpoint);
}
