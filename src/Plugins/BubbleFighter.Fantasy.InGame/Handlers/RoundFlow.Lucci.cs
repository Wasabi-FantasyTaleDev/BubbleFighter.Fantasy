using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundLucciCreated>? _lucciCreated;
    private readonly IMessageSender<RoundLucciPicked>? _lucciPicked;

    // The only item kinds C2S gcpReqCreateLucci can ask for (kr-812 sub_A1C640 sends 1 or 2).
    private const int PlainLucciKind = 1, BigLucciKind = 2;

    internal ValueTask HandleLucciDropAsync(ISession session, RoundLucciDropRequested request, CancellationToken ct)
    {
        if (request.Kind is not (PlainLucciKind or BigLucciKind))
            throw new InvalidOperationException($"Lucci drop kind {request.Kind} is not a plain or big Lucci.");
        return CreateLucciAsync(session, request.Point, request.Kind, request.Tag, ct);
    }

    // The award request names no kind (kr-812 sub_514B60 writes only a constant 5 at +28); which
    // award item the client builds is a deployment choice.
    internal ValueTask HandleHordeAwardAsync(ISession session, RoundHordeAwardRequested request, CancellationToken ct) =>
        CreateLucciAsync(session, request.Point, _options.HordeAwardKind, request.Tag, ct);

    private async ValueTask CreateLucciAsync(ISession session, RoundLucciPoint point, int kind, int tag, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members = [];
            RoundLucciCreated? created = null;
            var refused = false;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is null) return;
                var state = room.Lucci;
                if (state is null || state.Generation != room.StartGeneration)
                    room.Lucci = state = new RoundLucciState(room.StartGeneration, _options.LucciObjectIdBase);
                if (state.Items.Count >= _options.MaxDroppedLucci)
                {
                    refused = !state.RefusalLogged;
                    state.RefusalLogged = true;
                }
                else
                {
                    var id = checked(++state.LastId);
                    state.Items.Add(id, kind);
                    created = new RoundLucciCreated(id, kind, point, tag);
                    members = [.. room.Seats];
                }
            }
            if (refused)
                _logger.LogWarning("Room {Room}: {Limit} dropped Lucci items are unconsumed; further drops this round are ignored.",
                    room.Id, _options.MaxDroppedLucci);
            foreach (var member in members)
                await _lucciCreated!.SendAsync(member.Game, created!, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleLucciPickupAsync(ISession session, RoundLucciPickupRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundLucciPicked picked;
            lock (room.Gate)
            {
                // Every client touches the same item independently; the first report wins and the rest find it gone.
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } seat
                    || room.Lucci is not { } state || state.Generation != room.StartGeneration
                    || !state.Items.Remove(request.ObjectId, out var kind))
                    return;
                room.Scoring.AddLucci(seat.Slot, _options.LucciKindValues[kind - 1], _options.MaxRoundLucci);
                picked = new RoundLucciPicked(request.CharacterObjectId, request.ObjectId);
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _lucciPicked!.SendAsync(member.Game, picked, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>One round's dropped Lucci items that no player has picked up yet.</summary>
/// <param name="generation">The room's start generation the items belong to.</param>
/// <param name="objectIdBase">The id handed out before the first item.</param>
internal sealed class RoundLucciState(int generation, int objectIdBase)
{
    /// <summary>Gets the start generation; a state from an earlier round is discarded.</summary>
    internal int Generation { get; } = generation;

    /// <summary>Gets or sets the last object id handed out.</summary>
    internal int LastId { get; set; } = objectIdBase;

    /// <summary>Gets the unconsumed items by object id with their client item kind.</summary>
    internal Dictionary<int, int> Items { get; } = [];

    /// <summary>Gets or sets whether the bound was already reported this round.</summary>
    internal bool RefusalLogged { get; set; }
}
