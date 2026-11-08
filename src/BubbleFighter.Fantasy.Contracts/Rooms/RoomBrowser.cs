using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Projects cluster rooms into connection-local native room numbers without granting admission.</summary>
public interface IRoomBrowser
{
    /// <summary>Gets the build served by this browser.</summary>
    string ClientBuild { get; }

    /// <summary>Replaces this connection's visible aliases using fresh local snapshots and directory advertisements.</summary>
    ValueTask<IReadOnlyList<RoomListEntry>> ListAsync(ISession session,
        IReadOnlyList<PublishedRoomSnapshot> localRooms, CancellationToken cancellationToken);

    /// <summary>Resolves a visible alias; remote identities are rechecked against a fresh directory response.</summary>
    ValueTask<RoomBrowserSelection?> ResolveAsync(ISession session, int alias, CancellationToken cancellationToken);

    /// <summary>Invalidates visible aliases after an unsuccessful native list send, retaining bounded identity history.</summary>
    void Invalidate(ISession session);
}

/// <summary>The exact room incarnation selected by a native click; the destination must still authorize admission.</summary>
/// <param name="Local">Whether the room belongs to this node's live registry.</param>
/// <param name="NodeId">The configured owning node.</param>
/// <param name="Epoch">The remote lease generation, or zero for a directly captured local room.</param>
/// <param name="RoomId">The authoritative room number, independent of the connection's alias.</param>
/// <param name="InstanceId">The room creation identity, which must be checked again at admission.</param>
/// <param name="JoinAllowed">The directory's routing hint; it is neither authentication nor a reservation.</param>
public sealed record RoomBrowserSelection(bool Local, string NodeId, long Epoch, int RoomId, Guid InstanceId, bool JoinAllowed);

/// <summary>A bounded room-browser operation failed without changing the player's scene or authentication.</summary>
public sealed class RoomBrowserUnavailableException : Exception
{
    /// <summary>Creates a browser failure retaining the underlying transport, capacity or validation error for diagnostics.</summary>
    public RoomBrowserUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
