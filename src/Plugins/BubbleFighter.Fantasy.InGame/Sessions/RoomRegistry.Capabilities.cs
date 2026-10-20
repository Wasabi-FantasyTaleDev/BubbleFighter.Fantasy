using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class RoomRegistry
{
    /// <inheritdoc />
    public string Protocol => _options.RelayProtocol;

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> IssueCapabilityAsync(ISession game, int ticketLength, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (room, _) = RequireGame(game);
        byte[]? issued = null;
        await room.Executor.ExecuteAsync(_ =>
        {
            issued = IssueCapabilityCore(room, game, ticketLength);
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
        return issued!;
    }

    // Runs on the owning room's executor, including the native map-load path.
    internal byte[] IssueCapabilityCore(PlayRoom room, ISession game, int ticketLength)
    {
        ValidateCapabilityLength(ticketLength);
        lock (room.Gate)
        {
            var seat = room.FindByGame(game.Id);
            if (!_rooms.TryGetValue(room.Id, out var current) || !ReferenceEquals(current, room)
                || seat is null || !ReferenceEquals(seat.Game, game) || game.Connection.IsClosed
                || room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
                throw new InvalidOperationException("A native capability requires a live game seat in an active round.");
            if (seat.NativeCapability is not null || seat.EndpointAdvertised
                || (seat.Relay is not null && seat.RelayGeneration == room.StartGeneration))
                throw new InvalidOperationException("The current round already issued or attached a match capability.");
            var issued = RandomNumberGenerator.GetBytes(ticketLength);
            seat.NativeCapability = new(room.StartGeneration, issued);
            // Offer buffers cannot mutate the credential retained by the room.
            return issued.ToArray();
        }
    }

    // A seat whose match connection failed in the current round plays over its game connection: its unused credential
    // is withdrawn so a late match connection cannot bind, and a match connection already bound for this round is
    // released. Returns that released connection for the caller to close, if any.
    internal ISession? FallBackToGameCore(PlayRoom room, RoomSeat seat)
    {
        lock (room.Gate)
        {
            var relay = seat.RelayGeneration == room.StartGeneration ? seat.Relay : null;
            seat.NativeCapability = null;
            seat.GameLinkGeneration = room.StartGeneration;
            if (relay is not null) Release(relay);
            return relay;
        }
    }

    // Revokes only this failed offer, never a replacement credential or a later round.
    internal ISession? RevokeCapabilityCore(PlayRoom room, ISession game, ReadOnlySpan<byte> ticket)
    {
        lock (room.Gate)
        {
            var seat = room.FindByGame(game.Id);
            if (seat is null || !ReferenceEquals(seat.Game, game) || seat.NativeCapability is not { } key
                || key.Generation != room.StartGeneration || !CryptographicOperations.FixedTimeEquals(key.Bytes, ticket))
                return null;
            var relay = seat.RelayGeneration == room.StartGeneration ? seat.Relay : null;
            seat.NativeCapability = null;
            seat.EndpointAdvertised = false;
            seat.SceneReady = false;
            if (relay is not null) Release(relay);
            return relay;
        }
    }

    /// <inheritdoc />
    public async ValueTask<MatchSeatBinding> BindCapabilityAsync(ISession session, MatchCapabilityAuthenticate authentication,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateCapabilityLength(authentication.Ticket.Length);
        var ticket = authentication.Ticket.ToArray();
        if (_sessionToRoom.ContainsKey(session.Id)) throw new InvalidOperationException("Session is already attached to a room.");
        PlayRoom? target = null;
        foreach (var room in _rooms.Values)
        {
            lock (room.Gate)
            {
                if (FindCapabilitySeat(room, session, ticket) is null) continue;
                if (target is not null) throw new InvalidOperationException("Ambiguous native match capability.");
                target = room;
            }
        }
        if (target is null) throw new InvalidOperationException("Unknown native match capability.");
        ISession? previous = null;
        MatchSeatBinding? binding = null;
        try
        {
            await target.Executor.ExecuteAsync(_ =>
            {
                lock (target.Gate)
                {
                    var seat = FindCapabilitySeat(target, session, ticket)
                        ?? throw new InvalidOperationException("Native match capability was revoked before binding.");
                    if (target.Phase is not (RoomPhase.Starting or RoomPhase.Playing) || seat.Game.Connection.IsClosed)
                        throw new InvalidOperationException("Native match capability is not valid in this room phase.");
                    (previous, binding) = AttachMatchSeat(target, seat, session);
                }
                return ValueTask.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException("Match room was removed before the native transport bound.");
        }
        if (previous is not null)
            await previous.Connection.CloseAsync("match transport replaced").ConfigureAwait(false);
        return binding!;
    }

    private static RoomSeat? FindCapabilitySeat(PlayRoom room, ISession session, byte[] ticket)
    {
        RoomSeat? found = null;
        foreach (var seat in room.Seats)
        {
            if (seat.Game.Profile.Id != session.Profile.Id || seat.NativeCapability is not { } capability
                || capability.Generation != room.StartGeneration
                || !CryptographicOperations.FixedTimeEquals(capability.Bytes, ticket)) continue;
            if (found is not null) throw new InvalidOperationException("Ambiguous native match capability.");
            found = seat;
        }
        return found;
    }

    private void ValidateCapabilityLength(int length)
    {
        if (length < 1 || length > _options.MaxMatchCapabilityBytes)
            throw new ArgumentOutOfRangeException(nameof(length), $"Native match capability length must be between 1 and {_options.MaxMatchCapabilityBytes} bytes.");
    }
}
