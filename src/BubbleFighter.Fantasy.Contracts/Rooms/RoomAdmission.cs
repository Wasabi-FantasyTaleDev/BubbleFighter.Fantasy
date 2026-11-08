using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Prepares bounded destination seats independently of cross-node authentication and ownership transfer.</summary>
public interface IRoomAdmissionDirectory
{
    /// <summary>Gets the client dialect whose rooms this destination owns.</summary>
    string ClientBuild { get; }

    /// <summary>Checks the authoritative room and temporarily holds an available seat for the named account.</summary>
    ValueTask<RoomAdmissionPreparation> PrepareAsync(RoomAdmissionRequest request, CancellationToken cancellationToken);

    /// <summary>Releases only the matching unused reservation; repeated cancellation is harmless.</summary>
    ValueTask CancelAsync(RoomAdmissionReservation reservation, CancellationToken cancellationToken);

    /// <summary>Consumes a reservation through normal room entry after this exact game connection has independently authenticated.</summary>
    ValueTask<bool> JoinAsync(ISession session, RoomAdmissionReservation reservation, CancellationToken cancellationToken);
}

/// <summary>Authoritative target identity and admission inputs; this request is not a player authentication credential.</summary>
/// <param name="RoomId">The destination's native room number.</param>
/// <param name="InstanceId">The exact room creation selected at the source.</param>
/// <param name="UserNumber">The account that must independently authenticate on the destination game connection.</param>
/// <param name="Password">The native room password; never published in room discovery.</param>
public sealed record RoomAdmissionRequest(int RoomId, Guid InstanceId, int UserNumber, string Password)
{
    /// <summary>Describes the request without exposing the submitted password.</summary>
    public override string ToString() => $"Room admission {RoomId}/{InstanceId} for account {UserNumber}";
}

/// <summary>An opaque, single-use seat hold, not a replacement for destination authentication or account ownership.</summary>
/// <param name="Id">Unpredictable reservation identity.</param>
/// <param name="RoomId">The owning native room number.</param>
/// <param name="InstanceId">The owning room creation.</param>
/// <param name="UserNumber">The only account allowed to consume this hold.</param>
public sealed record RoomAdmissionReservation(Guid Id, int RoomId, Guid InstanceId, int UserNumber);

/// <summary>A successful seat hold or an ordinary native room-admission refusal.</summary>
/// <param name="Reservation">The held seat, present only on success.</param>
/// <param name="Refusal">The native refusal, present only when no seat was held.</param>
public sealed record RoomAdmissionPreparation(RoomAdmissionReservation? Reservation, RoomJoinRefusal? Refusal);
