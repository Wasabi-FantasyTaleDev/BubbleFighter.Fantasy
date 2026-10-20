using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The plaza: entering it (C2S 106), returning from it to the main menu (C2S 105), and leaving it
/// when a room is created or joined or the connection closes. The plaza has its own executor and
/// seat numbers (<see cref="PlazaRoster"/>); a player seated in a room is never shown in it.
/// </summary>
internal sealed class PlazaLoop
{
    private readonly RoomRegistry _registry;
    private readonly InGameOptions _options;
    private readonly ILogger _logger;
    private readonly PlazaRoster _plaza;
    private readonly ISeatCharacterResolver? _characters;
    private readonly IEquippedViewPush? _equippedView;
    private readonly IEquipmentSelection? _selection;
    private readonly IMessageSender<PartyUpdated>? _partySender;

    /// <summary>Builds the plaza handlers over one roster.</summary>
    /// <param name="registry">The room registry, to leave out players who are seated in a room.</param>
    /// <param name="options">The row configuration.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="plaza">The plaza roster and its executor.</param>
    /// <param name="characters">Optional equipped-character resolver.</param>
    /// <param name="equippedView">Optional publisher for the S2C 806 equipped view.</param>
    /// <param name="selection">Current persisted equipment, preferred over login and optional roster snapshots.</param>
    /// <param name="partySender">The dialect sender that retains membership during scene appearance refreshes.</param>
    public PlazaLoop(RoomRegistry registry, InGameOptions options, ILogger logger, PlazaRoster plaza,
        ISeatCharacterResolver? characters = null, IEquippedViewPush? equippedView = null, IEquipmentSelection? selection = null,
        IMessageSender<PartyUpdated>? partySender = null)
    {
        _registry = registry;
        _options = options;
        _logger = logger;
        _plaza = plaza;
        _characters = characters;
        _equippedView = equippedView;
        _selection = selection;
        _partySender = partySender;
    }

    private ValueTask<SeatEquipment> ResolveSeatEquipmentAsync(ISession game, int userNum, CancellationToken ct) =>
        SeatCharacter.ResolveEquipmentAsync(game, userNum, _selection, _characters, ct);

    public async ValueTask HandleEnterPlazaAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        EmptyPayload.Parse(packet.Payload.Span, "C2S 106");
        if (!SessionPlayer.IsPastLoginBurst(session))
        {
            _logger.LogWarning(
                "Session {Id} C2S 106 ignored: the login burst has not finished. Ingame does not send S2C 116 on the login burst.",
                session.Id);
            return;
        }

        if (!_options.SendPlazaSnapshot)
        {
            await session.SendAsync(InGameOpcodes.S2CGotoLobby, RoomSessionBlob.GotoLobby(0), ct).ConfigureAwait(false);
            return;
        }
        await EnterPlazaAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the session to the plaza on the plaza executor. Equipment is resolved first, outside
    /// the queue, so the seat is complete before anyone can see it.
    /// </summary>
    /// <param name="session">The entering game session.</param>
    /// <param name="ct">Cancellation token.</param>
    private async ValueTask EnterPlazaAsync(ISession session, CancellationToken ct)
    {
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        await _plaza.Executor.ExecuteAsync(
            operationCt => EnterPlazaCoreAsync(session, userNum, nickname, equipment, operationCt), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The entrant gets the whole plaza once: the S2C 117 kind-3 install placing every present
    /// player at their plaza seat, then S2C 201 and the S2C 116 enter result (117 before 116). Players already in the plaza are not reinstalled:
    /// each receives the entrant's equipped view (S2C 806) and seat record (S2C 511), which adds
    /// one character without rebuilding the stage (the 511 reaches the plaza
    /// spawner <c>sub_920940</c>).
    /// </summary>
    private async ValueTask EnterPlazaCoreAsync(ISession session, int userNum, string nickname, SeatEquipment equipment,
        CancellationToken ct)
    {
        var seat = _plaza.Enter(session, userNum, nickname, equipment.CharacterId, SessionPlayer.ResolveLevel(session));
        PlazaSeat[] present;
        lock (_plaza.Gate)
        {
            seat.EquippedItemIds = equipment.ItemIds;
            present = [.. _plaza.Snapshot().Where(other => _registry.TryGet(other.Game.Id) is null)];
        }

        var identities = present.Select(other => other.ToIdentity(other.Slot)).ToArray();
        var entries = present.Select(other => new ProfileMapEntry(
            other.UserNum, other.CharacterId, other.Slot, EquippedItemIds: other.EquippedItemIds)).ToArray();
        var install = RoomSessionBlob.GotoRoom(SessionSettingBlob.ForPlaza(), identities, entries, localSlot: seat.Slot);
        await session.SendAsync(InGameOpcodes.S2CGotoRoom, install, ct).ConfigureAwait(false);
        ScenePhase.Entered(session, SessionPhase.Plaza, _logger);
        // The 117 kind-3 branch installs the seats but reaches no spawner for the entrant's own
        // avatar; S2C 201 (sub_9064B0) does.
        if (_options.SendPartyUpdate)
        {
            await PartyScenePush.SendAsync(session, seat.UserNum, seat.Nickname, seat.CharacterId,
                seat.EquippedItemIds, _partySender, ct)
                .ConfigureAwait(false);
        }
        await session.SendAsync(InGameOpcodes.S2CGotoLobby, RoomSessionBlob.GotoLobby(0), ct).ConfigureAwait(false);

        var record = GameToClient.SeatRecord(seat.Slot, seat.ToIdentity(seat.Slot).ToPlayerInfo());
        var others = present.Where(other => other.SessionId != session.Id).ToArray();
        foreach (var other in others)
        {
            if (_equippedView is not null)
                await _equippedView.SendRemoteAsync(other.Game, seat.UserNum, seat.CharacterId,
                    seat.EquippedItemIds ?? [seat.CharacterId], ct).ConfigureAwait(false);
            await other.Game.SendAsync(InGameOpcodes.S2CSeatRecord, record, ct).ConfigureAwait(false);
        }
        _logger.LogInformation(
            "Session {Id} entered the plaza at seat {Slot}: {Count} player(s) present, character {Character}; {Others} told with S2C 806/511.",
            session.Id, seat.Slot, present.Length, seat.CharacterId, others.Length);
    }

    /// <summary>
    /// Takes a session out of the plaza (it created or joined a room, returned to the menu, or
    /// disconnected) and tells the players who stay with the vacated S2C 511 for its seat; the
    /// client removes that character without a reinstall (<c>sub_920940</c> unoccupied branch).
    /// Runs on the plaza executor. A session that is not in the plaza changes nothing.
    /// </summary>
    internal async Task LeavePlazaAsync(long sessionId, CancellationToken ct)
    {
        await _plaza.Executor.ExecuteAsync(async operationCt =>
        {
            var seat = _plaza.Remove(sessionId);
            if (seat is null)
                return;
            var vacated = GameToClient.SeatVacated(seat.Slot);
            var others = _plaza.Snapshot();
            foreach (var other in others)
                await other.Game.SendAsync(InGameOpcodes.S2CSeatRecord, vacated, operationCt).ConfigureAwait(false);
            _logger.LogInformation("Session {Id} left plaza seat {Slot}; {Count} player(s) told with S2C 511.",
                sessionId, seat.Slot, others.Length);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// C2S 105 from the plaza: the player leaves the plaza (the others see the seat vacated) and
    /// receives the S2C 117 kind-1 install that returns the client to the main menu, followed by
    /// S2C 201 and the own equipped view.
    /// </summary>
    public async ValueTask HandleReturnToMenuAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        EmptyPayload.Parse(packet.Payload.Span, "C2S 105");
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        await LeavePlazaAsync(session.Id, ct).ConfigureAwait(false);
        var self = new RoomPlayerIdentity(userNum, nickname, 0, SessionPlayer.ResolveLevel(session));
        var entry = new ProfileMapEntry(userNum, equipment.CharacterId, 0, EquippedItemIds: equipment.ItemIds);
        var install = RoomSessionBlob.GotoRoom(
            SessionSettingBlob.ForPlaza() with { Kind = SessionSettingBlob.MenuReturnKind }, [self], [entry]);
        await session.SendAsync(InGameOpcodes.S2CGotoRoom, install, ct).ConfigureAwait(false);
        ScenePhase.Entered(session, SessionPhase.MainMenu, _logger);
        // After the install: the party record for the menu avatar and the player's own
        // equipped view (S2C 836/837/806), which the kind-1 install alone does not refresh.
        if (_options.SendPartyUpdate)
        {
            await PartyScenePush.SendAsync(session, userNum, nickname, equipment.CharacterId,
                equipment.ItemIds, _partySender, ct)
                .ConfigureAwait(false);
        }
        if (_equippedView is not null)
            await _equippedView.SendSelectionAsync(session, userNum, equipment.CharacterId,
                equipment.ItemIds ?? [equipment.CharacterId], ct).ConfigureAwait(false);
        _logger.LogInformation("Session {Id} C2S 105 returned from the plaza to the main menu.", session.Id);
    }

    /// <summary>
    /// Queues the plaza departure of a closed connection without waiting, so the event bus is not
    /// blocked behind plaza work. Failures are logged.
    /// </summary>
    /// <param name="e">The connection-closed event.</param>
    public void OnConnectionClosed(ConnectionClosedEvent e)
    {
        var seat = _plaza.Snapshot().FirstOrDefault(candidate => candidate.Game.Connection.Id == e.Connection.Id);
        if (seat is null)
            return;
        Task posted;
        using (ExecutionContext.SuppressFlow())
            posted = Task.Run(() => LeavePlazaAsync(seat.SessionId, CancellationToken.None));
        _ = posted.ContinueWith(completed => _logger.LogError(completed.Exception!.GetBaseException(),
                "Plaza could not remove closed connection {Connection}.", e.Connection.Id),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
