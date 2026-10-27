namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Provides administration snapshots for the client build owned by one room registry.</summary>
public interface IRoomAdminDirectory
{
    /// <summary>Gets the client build served by this room registry.</summary>
    string ClientBuild { get; }

    /// <summary>Lists current rooms without exposing their passwords.</summary>
    /// <param name="cancellationToken">Cancels the snapshot request.</param>
    ValueTask<IReadOnlyList<AdminRoomSnapshot>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Gets one room, including its password for the authenticated administrator.</summary>
    /// <param name="roomId">The live room identifier.</param>
    /// <param name="cancellationToken">Cancels the snapshot request.</param>
    ValueTask<AdminRoomSnapshot?> GetAsync(int roomId, CancellationToken cancellationToken);
}

/// <summary>A detached room snapshot for the administration interface.</summary>
public sealed record AdminRoomSnapshot(int RoomId, string Title, string? ClientBuild, string Mode, string? Map,
    string Phase, int PlayerCount, int Capacity, bool Locked, string? Password, string? Owner,
    IReadOnlyList<AdminRoomPlayer> Players);

/// <summary>A player currently seated in an administered room.</summary>
public sealed record AdminRoomPlayer(int Id, string Nickname, int Level, int Team, bool Ready, bool Host, bool Ai);
