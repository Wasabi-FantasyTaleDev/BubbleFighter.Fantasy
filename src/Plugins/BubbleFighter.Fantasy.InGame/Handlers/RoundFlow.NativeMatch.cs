using System.Net;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // kr-812 sub_810C20 immediately reads the scene actor on relay 1; offer only after map loading.
    private async ValueTask OfferNativeMatchAsync(PlayRoom room, RoomSeat seat, CancellationToken ct)
    {
        var sender = _capabilityOffers ?? throw new InvalidOperationException("Native match offer encoder is not mounted.");
        var ticket = _registry.IssueCapabilityCore(room, seat.Game, sender.TicketLength);
        var generation = room.StartGeneration;
        try
        {
            await sender.SendAsync(seat.Game, new(IPAddress.Parse(_options.RelayAdvertiseIpv4),
                checked((ushort)_options.RelayAdvertisePort), ticket.ToArray()), ct).ConfigureAwait(false);
            lock (room.Gate)
            {
                if (!ReferenceEquals(room.FindByGame(seat.Game.Id), seat) || room.Phase != RoomPhase.Starting
                    || seat.NativeCapability is not { } key || key.Generation != generation
                    || !CryptographicOperations.FixedTimeEquals(key.Bytes, ticket))
                    throw new InvalidOperationException("Native match seat departed while its endpoint was sent.");
                seat.EndpointAdvertised = true;
            }
        }
        catch
        {
            var relay = _registry.RevokeCapabilityCore(room, seat.Game, ticket);
            if (relay is not null)
                await relay.Connection.CloseAsync("native match endpoint offer failed").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Confirms only an independently authenticated and initialized current-round relay before releasing the loading barrier.</summary>
    internal async ValueTask HandleMatchConnectionReportedAsync(ISession session, MatchConnectionReported report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_options.NativeMatchRelay)
            throw new InvalidOperationException("Native match connection reporting is disabled.");
        var (room, _) = _registry.RequireGame(session);
        // Native close reports have no generation, so an old false report cannot revoke a newer lease.
        if (!report.Connected)
        {
            if (_options.MatchTransportFallback)
            {
                await FallBackToGameAsync(room, session, ct).ConfigureAwait(false);
                return;
            }
            _logger.LogInformation("Session {Id} reported a closed native match transport; lease ownership is unchanged.", session.Id);
            return;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, session) || session.Connection.IsClosed)
                    throw new InvalidOperationException("Native match connection report requires its live game seat.");
                if (room.Phase is RoomPhase.Waiting or RoomPhase.Result)
                {
                    _logger.LogInformation("Session {Id} reported late native match success in {Phase}.", session.Id, room.Phase);
                    return;
                }
                if (!seat.SceneReady || !seat.EndpointAdvertised || seat.NativeCapability?.Generation != room.StartGeneration
                    || !seat.RelayInitialized || seat.RelayGeneration != room.StartGeneration
                    || seat.Relay is null || seat.Relay.Connection.IsClosed)
                    throw new InvalidOperationException("Native match connection report has no initialized current-round relay.");
                seat.RelayConfirmed = true;
            }
            await StartCountdownWhenAllLoadedAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Only a failure reported while this round is still loading moves a seat to its game connection: once the round
    // plays, the countdown already told the client which connection to use, and a confirmed match connection keeps it.
    private async ValueTask FallBackToGameAsync(PlayRoom room, ISession session, CancellationToken ct)
    {
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            ISession? released;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, session) || session.Connection.IsClosed
                    || room.Phase != RoomPhase.Starting || room.CountdownGeneration == room.StartGeneration
                    || !seat.EndpointAdvertised || seat.RelayConfirmed || seat.GameLinkGeneration == room.StartGeneration)
                {
                    _logger.LogInformation("Session {Id} reported a failed match connection outside its loading window; unchanged.", session.Id);
                    return;
                }
                released = _registry.FallBackToGameCore(room, seat);
                _logger.LogInformation("Room {Room} slot {Slot}: the match connection failed; the round uses the game connection.",
                    room.Id, seat.Slot);
            }
            if (released is not null)
                await released.Connection.CloseAsync("match connection replaced by the game connection").ConfigureAwait(false);
            await StartCountdownWhenAllLoadedAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Runs on the room's executor once the countdown went out, when every seat's character exists: the seed appliers
    // look the actor up by its entity and drop a table without one (cn-324 sub_460820, kr-812 sub_506290). A seat
    // playing over its game connection has no relay to receive them. A failed seat is logged and the round goes on,
    // because its tables only decide critical hits.
    private async ValueTask SendSeedTablesAsync(PlayRoom room, RoomSeat[] members, int generation, CancellationToken ct)
    {
        foreach (var member in members)
        {
            ISession? relay;
            MatchSeedTablesRequested request;
            lock (room.Gate)
            {
                relay = UsesGameLink(room, member) ? null : MatchLink(room, member);
                request = new(room.Id, generation, RoomRegistry.SceneEntity(member.Slot), member.PreviousRound);
            }
            if (relay is null)
                continue;
            try
            {
                await _seedTables!.SendAsync(relay, request, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _logger.LogError(failure, "Room {Room} slot {Slot}: the round's seed tables could not be sent.", room.Id, member.Slot);
            }
        }
    }

    // The previous-round history the next round's critical table keys on (CriticalHitTable rows prevWin, prevDraw,
    // prevLose and prevLoseInRow; cn-279 sub_705DF0 sets the same four states at its round end).
    private static void RememberOutcomes(PlayRoom room, int generation, Func<RoomSeat, RoundOutcome> outcome)
    {
        lock (room.Gate)
        {
            if (room.StartGeneration != generation)
                return;
            foreach (var seat in room.Seats)
                seat.PreviousRound = outcome(seat) switch
                {
                    RoundOutcome.Won => MatchPreviousRound.Won,
                    RoundOutcome.Draw => MatchPreviousRound.Drew,
                    _ => seat.PreviousRound is MatchPreviousRound.Lost or MatchPreviousRound.LostInRow
                        ? MatchPreviousRound.LostInRow : MatchPreviousRound.Lost,
                };
        }
    }

    // Callers hold the room gate. A game-side report alone never grants a relay lease; a seat moved to its game
    // connection in this round is ready once that connection is open.
    private static bool NativeMatchReady(PlayRoom room, RoomSeat seat) =>
        UsesGameLink(room, seat) ? !seat.Game.Connection.IsClosed
        : seat.EndpointAdvertised && seat.NativeCapability?.Generation == room.StartGeneration
        && seat.RelayInitialized && seat.RelayConfirmed && seat.RelayGeneration == room.StartGeneration
        && seat.Relay is not null && !seat.Relay.Connection.IsClosed && !seat.Game.Connection.IsClosed;

    // Callers hold the room gate.
    private static bool UsesGameLink(PlayRoom room, RoomSeat seat) => seat.GameLinkGeneration == room.StartGeneration;

    // Callers hold the room gate. The connection that carries this seat's round packets: its match connection of this
    // round, its game connection after a reported match failure, or none while neither is usable.
    private static ISession? MatchLink(PlayRoom room, RoomSeat seat) =>
        UsesGameLink(room, seat) ? (seat.Game.Connection.IsClosed ? null : seat.Game)
        : seat.Relay is { } relay && seat.RelayGeneration == room.StartGeneration && !relay.Connection.IsClosed ? relay : null;
}
