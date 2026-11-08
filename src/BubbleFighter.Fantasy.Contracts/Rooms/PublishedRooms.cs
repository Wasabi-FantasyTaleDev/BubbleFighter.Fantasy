namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Exposes detached native list entries for published gameplay rooms, excluding private scenes.</summary>
public interface IPublishedRoomDirectory
{
    /// <summary>Gets the build whose dialect consumes these room settings.</summary>
    string ClientBuild { get; }

    /// <summary>Captures occupied, published gameplay rooms without passwords or player account records.</summary>
    /// <param name="cancellationToken">Cancels snapshot collection between room gates.</param>
    /// <returns>A detached view; a later admission must recheck the room's identity and state.</returns>
    ValueTask<IReadOnlyList<PublishedRoomSnapshot>> ListPublishedAsync(CancellationToken cancellationToken);
}

/// <summary>One room incarnation and the native list row captured under its room gate.</summary>
/// <param name="InstanceId">A new identity for every room creation, even when the numeric room id is reused.</param>
/// <param name="Entry">The native list entry with detached settings and no session prefix.</param>
/// <param name="Map">The selected or active map key.</param>
/// <param name="Phase">The owner-reported lifecycle phase at capture time.</param>
public sealed record PublishedRoomSnapshot(Guid InstanceId, RoomListEntry Entry, string Map, string Phase);
