using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Transfers a solo game connection to a freshly resolved remote room under the caller's scene lock.</summary>
public interface IRemoteRoomMigration
{
    /// <summary>Gets the client build whose native move sender this source uses.</summary>
    string ClientBuild { get; }

    /// <summary>Attempts an opted-in return after normal room exit and settlement, while the caller holds the scene lock.</summary>
    ValueTask ReturnHomeAsync(ISession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Prepares a seat, hands off account authority and offers the destination; null means the offer was sent.</summary>
    ValueTask<RoomJoinRefusal?> BeginAsync(ISession session, RoomBrowserSelection selection, string password, CancellationToken cancellationToken);
}

/// <summary>Accepts one migration source after the room service and node heartbeat have started.</summary>
public interface IRemoteRoomMigrationEndpoint
{
    /// <summary>Gets the client build served by these rooms.</summary>
    string ClientBuild { get; }

    /// <summary>
    /// Attaches the matching source, rejecting duplicates. The caller owns the returned asynchronous effect;
    /// disposing it prevents new calls and drains admitted calls before the source can be released.
    /// </summary>
    IAsyncDisposable Bind(IRemoteRoomMigration source);
}
