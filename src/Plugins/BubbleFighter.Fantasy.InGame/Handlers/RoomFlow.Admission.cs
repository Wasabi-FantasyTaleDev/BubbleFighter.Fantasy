using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow : IRoomAdmissionDirectory, IMigrationHomeAdmission, IMigrationRoomRecovery
{
    string IMigrationHomeAdmission.ClientBuild => _registry.ClientBuild;

    string IMigrationRoomRecovery.ClientBuild => _registry.ClientBuild;

    ValueTask<bool> IMigrationHomeAdmission.EnterAsync(ISession session, SessionPhase scene, CancellationToken ct) =>
        scene is SessionPhase.MainMenu or SessionPhase.Plaza
            ? InstallRetainedSceneAsync(session, SessionPhase.Authenticated, ct, scene == SessionPhase.Plaza && PlazaBound)
            : ValueTask.FromResult(false);

    ValueTask<bool> IMigrationRoomRecovery.InstallMenuAsync(ISession session, CancellationToken ct) =>
        InstallRetainedSceneAsync(session, SessionPhase.MainMenu, ct);

    ValueTask IMigrationRoomRecovery.ReturnHomeAsync(ISession session, long generation, CancellationToken ct) =>
        CaptureHomeReturn(session) == generation ? ReturnHomeAfterSceneAsync(session, generation, ct) : ValueTask.CompletedTask;

    private async ValueTask<bool> InstallRetainedSceneAsync(ISession session, SessionPhase expectedPhase, CancellationToken ct,
        bool restorePlaza = false)
    {
        if ((!restorePlaza && _mainMenu is null) || session.Connection.IsClosed || !session.TryFeature<IPlayerSession>(out var player)
            || player.Phase != expectedPhase || player.LoginConnectionId != session.Connection.Id
            || session.Profile.Id != _registry.ClientBuild || !_registry.AdmissionsActive) return false;
        var generation = player.Generation;
        var entered = false;
        await RunSceneAsync(session, async (_, token) =>
        {
            if (_registry.TryGet(session.Id) is not null || _plaza.Find(session.Id) is not null
                || player.Generation != generation || player.Phase != expectedPhase) return;
            if (restorePlaza)
            {
                // Native promotion has completed login; cn-324 sub_707600 can install the plaza directly once ready.
                player.MoveTo(SessionPhase.MainMenu);
                await EnterPlazaAsync(session, token).ConfigureAwait(false);
                entered = player.Phase == SessionPhase.Plaza && !session.Connection.IsClosed;
                return;
            }
            var equipment = await ResolveEquipmentAsync(session, player.Identity.UserNum, token).ConfigureAwait(false);
            if (session.Connection.IsClosed || player.Generation != generation) return;
            await _mainMenu!.SendAsync(session, new(player.Identity.UserNum, player.Identity.Nickname, equipment.CharacterId,
                equipment.ItemIds ?? [equipment.CharacterId], player.Identity.Level)
                { OfferTutorial = false, ColorIndex = player.Identity.ColorIndex }, token).ConfigureAwait(false);
            player.MoveTo(SessionPhase.MainMenu);
            entered = true;
        }, ct).ConfigureAwait(false);
        return entered;
    }

    private sealed record PreparedAdmission(RoomAdmissionReservation Reservation, IPlayerSession Player, long Generation);

    string IRoomAdmissionDirectory.ClientBuild => _registry.ClientBuild;

    async ValueTask<RoomAdmissionPreparation> IRoomAdmissionDirectory.PrepareAsync(RoomAdmissionRequest request, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        return await _registry.PrepareAdmissionAsync(request, linked.Token).ConfigureAwait(false);
    }

    ValueTask IRoomAdmissionDirectory.CancelAsync(RoomAdmissionReservation reservation, CancellationToken ct) =>
        _registry.CancelAdmissionAsync(reservation, ct);

    async ValueTask<bool> IRoomAdmissionDirectory.JoinAsync(ISession session, RoomAdmissionReservation reservation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reservation);
        ct.ThrowIfCancellationRequested();
        if (!session.TryFeature<IPlayerSession>(out var player)) return false;
        var admission = new PreparedAdmission(reservation, player, player.Generation);
        if (!IsAdmissionSession(session, admission)) return false;
        try
        {
            await RunSceneAsync(session, (_, token) => JoinSceneAsync(session, new(reservation.RoomId, string.Empty),
                null, token, reservation.InstanceId, admission), ct).ConfigureAwait(false);
            return _registry.TryGame(session) is { } joined && joined.Room.InstanceId == reservation.InstanceId
                && ReferenceEquals(joined.Seat.Game, session);
        }
        catch
        {
            await RevokeFailedPreparedJoinAsync(session, reservation).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await _registry.CancelAdmissionAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask RevokeFailedPreparedJoinAsync(ISession session, RoomAdmissionReservation reservation)
    {
        if (_registry.TryGame(session) is not { } joined || joined.Room.InstanceId != reservation.InstanceId
            || !ReferenceEquals(joined.Seat.Game, session)) return;
        try { await session.Connection.CloseAsync("prepared room installation failed").ConfigureAwait(false); }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Prepared room connection {Connection} failed to close after installation failed.", session.Connection.Id);
        }
        try
        {
            await joined.Room.Executor.ExecuteAsync(async token =>
            {
                if (_registry.TryGame(session) is { } current && ReferenceEquals(current.Room, joined.Room)
                    && ReferenceEquals(current.Seat.Game, session))
                    await LeaveCoreAsync(joined.Room, session.Id, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            if (_registry.TryGame(session) is { } current && ReferenceEquals(current.Room, joined.Room)
                && ReferenceEquals(current.Seat.Game, session)) _registry.Detach(session.Id);
            _logger.LogError(failure, "Prepared room {Room} failed to announce revoked session {Session}.", joined.Room.Id, session.Id);
        }
    }

    private bool IsAdmissionSession(ISession session, PreparedAdmission admission) =>
        _registry.AdmissionsActive && !_lifetime.IsCancellationRequested && !session.Connection.IsClosed
        && session.Profile.Id == _registry.ClientBuild
        && session.TryFeature<IPlayerSession>(out var player) && ReferenceEquals(player, admission.Player)
        && player.LoginConnectionId == session.Connection.Id && player.Identity.UserNum == admission.Reservation.UserNumber
        && player.Generation == admission.Generation && player.Phase is SessionPhase.MainMenu or SessionPhase.Plaza;
}
