using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class RoomRegistry
{
    internal bool AdmissionsActive => _options.RoomAdmissionReservations && Volatile.Read(ref _admissionsStopped) == 0;

    internal async ValueTask<RoomAdmissionPreparation> PrepareAdmissionAsync(RoomAdmissionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Password);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.UserNumber);
        ct.ThrowIfCancellationRequested();
        if (!AdmissionsActive) throw new InvalidOperationException("Destination room admissions are not active.");
        if (Find(request.RoomId) is not { } room) return new(null, RoomJoinRefusal.NoRoom);
        RoomAdmissionPreparation result = new(null, RoomJoinRefusal.NoRoom);
        try
        {
            await room.Executor.ExecuteAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                lock (room.Gate)
                {
                    if (AdmissionsActive && ReferenceEquals(Find(request.RoomId), room))
                        result = room.PrepareAdmission(request, TimeSpan.FromMilliseconds(_options.RoomAdmissionLifetimeMs));
                }
                return ValueTask.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A room removed before its queued preparation runs is no longer an admission destination.
        }
        return result;
    }

    internal ValueTask CancelAdmissionAsync(RoomAdmissionReservation reservation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ct.ThrowIfCancellationRequested();
        if (Find(reservation.RoomId) is { } room && room.InstanceId == reservation.InstanceId)
            lock (room.Gate) room.CancelAdmission(reservation);
        return ValueTask.CompletedTask;
    }
}
