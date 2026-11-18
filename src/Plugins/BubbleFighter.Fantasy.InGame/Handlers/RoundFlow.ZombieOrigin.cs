using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IMessageSender<RoundZombieOriginPrototypeRoll>? ZombieOriginRolls { get; set; }
    internal Lazy<ZombieOriginPrototypeRules>? ZombieOriginRules { get; set; }

    private static bool IsZombieOriginPlaying(PlayRoom room) =>
        room.ModeId == (int)GameModeId.ZombieOrigin && room.Tutorial is null && room.Phase == RoomPhase.Playing
        && !room.RoundCancelled && !room.IsStopping && room.DecidedWinnerTeam is null && room.DecidedWinnerSlots is null;

    private static bool CanReceiveZombieOriginRoll(PlayRoom room, RoomSeat seat) =>
        !seat.Game.Connection.IsClosed && !room.IsRefereeSeat(seat) && seat.Life.State == SeatLife.Alive;

    private ValueTask CheckZombieOriginOutcomeAsync(PlayRoom room, CancellationToken ct)
    {
        lock (room.Gate)
            if (!IsZombieOriginPlaying(room) || room.ZombieOrigin?.Decide(room, timeExpired: false) != true)
                return ValueTask.CompletedTask;
        return EndRoundAsync(room, RoundEndReason.WinCondition, ct);
    }

    // Called under the room gate during the transition to Playing, before the start burst reaches either client.
    internal void StartZombieOrigin(PlayRoom room)
    {
        if (ZombieOriginRolls is null || !IsZombieOriginPlaying(room) || room.ZombieOrigin is not null) return;
        room.ZombieOrigin = new(room.StartGeneration, room.Seats.Where(s => CanReceiveZombieOriginRoll(room, s)).ToArray(),
            _options.ZombieOriginPrototypeCount);
    }

    internal async ValueTask HandleZombieOriginPrototypeAsync(ISession session, RoundZombieOriginPrototypeRequested request, CancellationToken ct)
    {
        if (ZombieOriginRolls is null || FindGameReportRoom(session) is not { } room) return;
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        // kr-1256 sub_CE90F0 reports the controlled character's kind; the request cannot nominate a recipient.
        await room.Executor.ExecuteAsync(token => SendZombieOriginDrawAsync(room, session, generation, token), ct).ConfigureAwait(false);
    }

    // Both paths run on the room executor. The native receiver kr-1256 sub_CF1B40 -> kr-1256 sub_CEAEB0
    // selects the local character's installed role, without testing for a pending game request.
    private async ValueTask SendZombieOriginDrawAsync(PlayRoom room, ISession session, int generation, CancellationToken ct)
    {
        ZombieOriginRoundState.Prototype draw;
        lock (room.Gate)
        {
            if (!IsZombieOriginPlaying(room) || room.ZombieOrigin is not { } state || state.Generation != generation
                || room.StartGeneration != generation || room.FindByGame(session.Id) is not { } seat
                || !ReferenceEquals(seat.Game, session) || !CanReceiveZombieOriginRoll(room, seat)
                || state.For(seat) is not { Delivered: false } selected) return;
            draw = selected;
        }
        await ZombieOriginRolls!.SendAsync(session, new(draw.Roll), ct).ConfigureAwait(false);
        lock (room.Gate) draw.Delivered = true;
    }

    internal async ValueTask PublishOpeningZombieOriginAsync(PlayRoom room, int generation, CancellationToken ct)
    {
        RoomSeat[] selected;
        lock (room.Gate)
        {
            // The Korean opening guide calls for a fighter to become the prototype. Our timing policy is
            // the first safe point after all start frames. Never send to an unbuilt actor: the receiver
            // gets the local character before checking the role at +1400 (kr-1256 sub_5063A0 / kr-1256 sub_CEAEB0).
            if (ZombieOriginRolls is null || !IsZombieOriginPlaying(room) || room.ZombieOrigin is not { } state
                || state.Generation != generation || room.StartGeneration != generation
                || room.Seats.Any(s => !room.IsRefereeSeat(s) && (!s.SceneReady || !s.ObjectAdded))) return;
            selected = [.. room.Seats.Where(s => state.For(s) is { Delivered: false })];
        }
        foreach (var seat in selected)
        {
            try { await SendZombieOriginDrawAsync(room, seat.Game, generation, ct).ConfigureAwait(false); }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                // The native reset-time request may retry the very same draw; do not stop other players' round clocks.
                _logger.LogWarning(ex, "Room {Room}: opening zombie prototype did not reach seat {Slot}.", room.Id, seat.Slot);
            }
        }
    }

    private async ValueTask HandleZombieOriginConfirmationAsync(PlayRoom room, ISession session, RoundFeedSent packet, CancellationToken ct)
    {
        if (ZombieOriginRules is not { } rules || packet.ZombieOriginPrototype is not { } report) return;
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        await room.Executor.ExecuteAsync(async token =>
        {
            ZombieOriginRoundState.Prototype draw;
            RoomSeat owner;
            lock (room.Gate)
            {
                var seat = room.Seats.FirstOrDefault(s => ReferenceEquals(s.Game, session)
                    || ReferenceEquals(s.Relay, session) && s.RelayGeneration == generation && s.RelayInitialized);
                if (seat is null || session.Connection.IsClosed || !IsZombieOriginPlaying(room) || !CanReceiveZombieOriginRoll(room, seat)
                    || room.StartGeneration != generation || room.ZombieOrigin is not { } state || state.Generation != generation
                    || RoomRegistry.SceneEntity(seat.Slot) != report.Actor || state.For(seat) is not { Delivered: true, Confirmed: false } selected)
                    return;
                owner = seat;
                draw = selected;
            }
            if (rules.Value.Choose(draw.Roll, draw.HasForceItem) != report.Kind) return;
            await ForwardFeedAsync(room, session, packet, token, () =>
            {
                if (!IsZombieOriginPlaying(room) || room.StartGeneration != generation || session.Connection.IsClosed || draw.Confirmed
                    || !ReferenceEquals(room.ZombieOrigin?.For(owner), draw)
                    || !ReferenceEquals(room.FindByGame(owner.Game.Id), owner) || !CanReceiveZombieOriginRoll(room, owner)
                    || !ReferenceEquals(owner.Game, session) && !(ReferenceEquals(owner.Relay, session)
                        && owner.RelayGeneration == generation && owner.RelayInitialized))
                    return false;
                // Claim before fanout: if a later recipient fails, repeating the transformation for earlier recipients is unsafe.
                if (report.Kind is { } kind)
                    room.ZombieOrigin!.MemberFor(owner)!.Confirm(owner, ZombieOriginRole.Prototype, kind);
                draw.Confirmed = true;
                return true;
            }).ConfigureAwait(false);
            await CheckZombieOriginOutcomeAsync(room, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
