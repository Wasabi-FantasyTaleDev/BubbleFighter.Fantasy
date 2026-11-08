using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.RoomCore;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    private readonly Dictionary<Guid, AdmissionHold> _admissions = [];
    private TimeProvider _admissionClock = TimeProvider.System;

    private sealed record AdmissionHold(RoomAdmissionReservation Reservation, int Slot, long Created,
        TimeSpan Lifetime, byte[] PasswordHash);

    internal void SetAdmissionClock(TimeProvider clock) => _admissionClock = clock;

    internal int PendingAdmissionCount
    {
        get { ExpireAdmissions(); return _admissions.Count; }
    }

    internal RoomAdmissionPreparation PrepareAdmission(RoomAdmissionRequest request, TimeSpan lifetime)
    {
        ExpireAdmissions();
        if (request.InstanceId != InstanceId || !IsMatchRoom || Seats.Count == 0)
            return new(null, RoomJoinRefusal.NoRoom);
        if (Phase != RoomPhase.Waiting)
            return new(null, Phase == RoomPhase.Closed ? RoomJoinRefusal.NoRoom : RoomJoinRefusal.Started);
        if (Seats.Any(seat => seat.UserNum == request.UserNumber)
            || _admissions.Values.Any(hold => hold.Reservation.UserNumber == request.UserNumber))
            return new(null, RoomJoinRefusal.NoRoom);
        if (IsFull) return new(null, RoomJoinRefusal.Full);
        if (Password.Length > 0 && request.Password != Password)
            return new(null, RoomJoinRefusal.WrongPassword);
        var slot = Enumerable.Range(0, _maxSeats).First(candidate => _members.Reserve(candidate));
        var reservation = new RoomAdmissionReservation(Guid.NewGuid(), Id, InstanceId, request.UserNumber);
        _admissions.Add(reservation.Id, new(reservation, slot, _admissionClock.GetTimestamp(), lifetime, PasswordHash()));
        return new(reservation, null);
    }

    internal RoomJoinRefusal? ValidateAdmission(RoomAdmissionReservation reservation, int userNumber)
    {
        ExpireAdmissions();
        if (!_admissions.TryGetValue(reservation.Id, out var hold) || hold.Reservation != reservation
            || reservation.UserNumber != userNumber || !IsMatchRoom || Seats.Count == 0)
            return RoomJoinRefusal.NoRoom;
        if (Phase != RoomPhase.Waiting)
            return Phase == RoomPhase.Closed ? RoomJoinRefusal.NoRoom : RoomJoinRefusal.Started;
        return CryptographicOperations.FixedTimeEquals(hold.PasswordHash, PasswordHash())
            ? null : RoomJoinRefusal.WrongPassword;
    }

    internal RoomSeat AddAdmission(ISession game, RelaySessionKey key, int userNumber, string nickname,
        RoomAdmissionReservation reservation)
    {
        if (ValidateAdmission(reservation, userNumber) is { } refusal)
            throw new InvalidOperationException($"Prepared room admission was refused: {refusal}.");
        var hold = _admissions[reservation.Id];
        var seat = _members.AddReserved(hold.Slot, slot => new RoomSeat(slot, game, key, userNumber, nickname));
        _admissions.Remove(reservation.Id);
        if (Setting.Kind == SessionSettingBlob.MainMenuKind) AssignSpawnTeams();
        return seat;
    }

    internal void CancelAdmission(RoomAdmissionReservation reservation)
    {
        ExpireAdmissions();
        if (_admissions.TryGetValue(reservation.Id, out var hold) && hold.Reservation == reservation)
            ReleaseAdmission(hold);
    }

    internal void ClearAdmissions()
    {
        foreach (var hold in _admissions.Values.ToArray()) ReleaseAdmission(hold);
    }

    private void ExpireAdmissions()
    {
        if (_admissions.Count == 0) return;
        var now = _admissionClock.GetTimestamp();
        foreach (var hold in _admissions.Values.ToArray())
            if (_admissionClock.GetElapsedTime(hold.Created, now) >= hold.Lifetime) ReleaseAdmission(hold);
    }

    private void ReleaseAdmission(AdmissionHold hold)
    {
        if (!_members.Release(hold.Slot))
            throw new InvalidOperationException($"Room {Id} lost its pending admission seat {hold.Slot}.");
        _admissions.Remove(hold.Reservation.Id);
    }

    private byte[] PasswordHash() => SHA256.HashData(Encoding.UTF8.GetBytes(Password));
}
