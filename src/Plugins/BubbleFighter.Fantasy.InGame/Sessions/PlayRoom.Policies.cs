using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    /// <summary>A host's start a room policy held, kept so the policy can repeat exactly that start.</summary>
    /// <param name="SessionId">The game session that pressed start.</param>
    /// <param name="Request">The start as the client sent it.</param>
    /// <param name="AllowMemberStart">Whether the start came through a path that lets a member start.</param>
    internal sealed record HeldRoomStart(long SessionId, RoomStartRequested Request, bool AllowMemberStart);

    /// <summary>Gets or sets the last start a room policy held; a start that goes ahead clears it.</summary>
    internal HeldRoomStart? HeldStart { get; set; }

    /// <summary>
    /// Gets the seats whose equipment selection is between its start and its settlement, with how many are open per
    /// session. A start is held while any is open. Read and written under <see cref="Gate"/>.
    /// </summary>
    internal Dictionary<long, int> PendingSelections { get; } = [];
}
