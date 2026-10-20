using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundSupplyCreated>? _supplyCreated;
    private readonly IMessageSender<RoundSupplyPicked>? _supplyPicked;
    private readonly IMessageSender<RoundItemUsed>? _itemUsed;
    private readonly IMessageSender<RoundItemSlotChanged>? _itemSlotChanged;
    private readonly IMessageSender<RoundCountedItemUsed>? _countedItemUsed;

    // Item mode (client mode 3): the clients keep its rules, the host reports the boxes and the server relays them
    // (packets/map-supplies.yaml). The server only holds the kinds and the team ownership the build states.
    private bool IsItemMode(PlayRoom room) => _options.ItemModeIds.Contains(room.ModeId);

    private bool ItemModeAccepts(int kind) => kind == 0 || _options.ItemModeKinds.Contains(kind);

    internal async ValueTask HandleSupplyDropAsync(ISession session, RoundSupplyDropRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundSupplyCreated created;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is null) return;
                if (request.Point.Kind < 0 || !float.IsFinite(request.Point.X) || !float.IsFinite(request.Point.Y)
                    || !float.IsFinite(request.Point.Z))
                    throw new InvalidOperationException("Dropped item kind or position is invalid.");
                room.Supplies ??= new RoundSupplyState(room.StartGeneration, [], _options.SupplyObjectIdBase);
                var state = room.Supplies;
                if (state.Generation != room.StartGeneration) return;
                if (IsItemMode(room) && !ItemModeAccepts(request.Point.Kind))
                {
                    NoteItemModeRefusal(room, state, $"a thrown box of kind {request.Point.Kind}");
                    return;
                }
                if (state.Dropped.Count >= _options.MaxDroppedSupplies)
                    throw new InvalidOperationException("Unconsumed dropped items exceed the configured bound.");
                var id = checked(++state.LastId);
                state.Dropped.Add(id, request.Point.Kind);
                created = new(id, request.Point) { CreationClass = request.CreationClass };
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _supplyCreated!.SendAsync(member.Game, created, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleSuppliesReportedAsync(ISession session, RoundSuppliesReported report, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(operationCt =>
        {
            lock (room.Gate)
            {
                if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing)
                    || room.FindByGame(session.Id) is not { } reporter || reporter.Slot != room.MasterSlot)
                    return ValueTask.CompletedTask;
                if (room.Supplies is { } existing && existing.Generation == room.StartGeneration)
                    return ValueTask.CompletedTask;
                if (report.Points.Count > _options.MaxSupplyPoints)
                    throw new InvalidOperationException("Map supply point count exceeds the configured bound.");
                foreach (var point in report.Points)
                    if ((report.IntervalsStated && (point.MinimumMs < 1 || point.MaximumMs < point.MinimumMs
                            || point.MaximumMs > _options.MaxSupplyIntervalMs)) || point.Kind < 0
                        || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                        throw new InvalidOperationException("Map supply point or interval is invalid.");
                // A build whose report states no interval gets the configured default for every point.
                var reported = report.IntervalsStated ? report.Points
                    : [.. report.Points.Select(point => point with { MinimumMs = _options.SupplyDefaultMinMs, MaximumMs = _options.SupplyDefaultMaxMs })];
                // A point whose kind the build's item mode does not offer is left out; the other points still run.
                var points = IsItemMode(room) ? reported.Where(point => ItemModeAccepts(point.Kind)).ToArray() : reported;
                room.Supplies = new RoundSupplyState(room.StartGeneration, points, _options.SupplyObjectIdBase);
                if (points.Count != report.Points.Count)
                    NoteItemModeRefusal(room, room.Supplies, $"{report.Points.Count - points.Count} map point(s) of an unoffered kind");
                if (room.Phase == RoomPhase.Playing) StartSupplies(room);
            }
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleSupplyPickupAsync(ISession session, RoundSupplyPickupRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        if (_options.ZombieKingUltraItemKind > 0 && request.Kind == _options.ZombieKingUltraItemKind && request.ObjectId == ZombieKingUltraObject)
        {
            await HandleZombieKingUltraAsync(room, session, ct).ConfigureAwait(false);
            return;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundSupplyPicked picked;
            bool immediate;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } seat
                    || room.Supplies is not { } state || state.Generation != room.StartGeneration)
                    return;
                var itemMode = IsItemMode(room);
                if (itemMode && !ItemModeAccepts(request.Kind))
                {
                    NoteItemModeRefusal(room, state, $"a pickup of kind {request.Kind}");
                    return;
                }
                var countedMode = _options.CountedItemModeId > 0 && room.ModeId == _options.CountedItemModeId;
                var counted = countedMode && _options.CountedItemKinds.Contains(request.Kind);
                immediate = _options.ImmediateSupplyItemKinds.Contains(request.Kind)
                    || (countedMode && _options.CountedImmediateItemKinds.Contains(request.Kind));
                var slotted = !immediate && _options.RandomSupplyItemKinds.Contains(request.Kind);
                bool MatchesKind(int kind) => kind == request.Kind || (kind == 0
                    && (_options.RandomSupplyItemKinds.Contains(request.Kind)
                        || (countedMode && _options.CountedImmediateItemKinds.Contains(request.Kind))));
                var itemSlot = 0;
                HeldSupplyItems? held = null;
                if (slotted && !counted)
                {
                    if (!state.HeldItems.TryGetValue(seat.Slot, out held))
                        state.HeldItems.Add(seat.Slot, held = new(_options.NativeItemSlotCount));
                    itemSlot = held.FirstEmptySlot();
                    if (itemSlot < 0) return;
                }
                var countKey = (seat.Slot, request.Kind);
                if (counted && state.CountedItems.GetValueOrDefault(countKey) >= _options.MaxCountedItemsPerKind)
                    throw new InvalidOperationException("Counted cooperative pickup exceeds the configured bound.");
                var point = state.Points.FirstOrDefault(point => point.ActiveId == request.ObjectId
                    && point.ActiveId != 0 && MatchesKind(point.Point.Kind));
                // The clients refuse a box of another team themselves (kr-812 sub_6378A0); a claim that gets past that is refused here too.
                if (itemMode && point is { Point.Team: not 0 } && point.Point.Team != seat.Team)
                {
                    NoteItemModeRefusal(room, state, $"a pickup of a team {point.Point.Team} box by a team {seat.Team} seat");
                    return;
                }
                if (point is not null) point.ActiveId = 0;
                else if (state.Dropped.TryGetValue(request.ObjectId, out var kind) && MatchesKind(kind))
                    state.Dropped.Remove(request.ObjectId);
                else return;
                if (counted)
                    state.CountedItems[countKey] = state.CountedItems.GetValueOrDefault(countKey) + 1;
                else if (slotted)
                    held!.Add(itemSlot, request.Kind);
                picked = new RoundSupplyPicked(seat.Slot, request.ObjectId)
                    { ItemKind = slotted ? request.Kind : 0, ItemSlot = itemSlot };
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _supplyPicked!.SendAsync(member.Game, picked, operationCt).ConfigureAwait(false);
            // cn-324 sub_46DBE0 removes the box; sub_46FED0 separately requests its native effect.
            if (immediate)
                await _countedItemUsed!.SendAsync(session, new(picked.Seat, request.Kind), operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleItemUseAsync(ISession session, RoundItemUseRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            int slot;
            int kind;
            int itemSlot;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { AwaitingRespawn: false } seat
                    || room.Supplies is not { } state || state.Generation != room.StartGeneration
                    || !state.HeldItems.TryGetValue(seat.Slot, out var held) || !held.TryUse(out itemSlot, out kind))
                    return;
                slot = seat.Slot;
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _itemUsed!.SendAsync(member.Game, new RoundItemUsed(slot) { Kind = kind, ItemSlot = itemSlot }, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleItemSlotSwitchAsync(ISession session, RoundItemSlotSwitchRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundItemSlotChanged changed;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } seat)
                    return;
                var held = room.Supplies is { } state && state.Generation == room.StartGeneration
                    ? state.HeldItems.GetValueOrDefault(seat.Slot) : null;
                var switched = !seat.AwaitingRespawn && held is not null && held.TrySwitch();
                changed = new(seat.Slot, held?.SelectedSlot ?? 0, held?.SelectedKind ?? -1, !switched);
                members = switched ? [.. room.Seats] : [seat];
            }
            foreach (var member in members)
                await _itemSlotChanged!.SendAsync(member.Game, changed, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleCountedItemUseAsync(ISession session, RoundCountedItemUseRequested request, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundCountedItemUsed used;
            lock (room.Gate)
            {
                if (_options.CountedItemModeId <= 0 || room.ModeId != _options.CountedItemModeId
                    || room.Phase != RoomPhase.Playing || request.ItemSlot <= 0 || request.ItemSlot > _options.CountedItemKinds.Length
                    || room.FindByGame(session.Id) is not { AwaitingRespawn: false } seat
                    || room.Supplies is not { } state || state.Generation != room.StartGeneration)
                    return;
                var kind = _options.CountedItemKinds[request.ItemSlot - 1];
                var key = (seat.Slot, kind);
                var count = state.CountedItems.GetValueOrDefault(key);
                if (count <= 0) return;
                if (count == 1) state.CountedItems.Remove(key);
                else state.CountedItems[key] = count - 1;
                used = new(seat.Slot, kind);
            }
            // cn-324 sub_46FED0 runs the controlling character's effect and emits gppUseItemDirect for peers.
            await _countedItemUsed!.SendAsync(session, used, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Refusals are logged once per round so a client that keeps asking cannot fill the log.
    private void NoteItemModeRefusal(PlayRoom room, RoundSupplyState state, string what)
    {
        if (state.RefusalLogged) return;
        state.RefusalLogged = true;
        _logger.LogWarning("Room {Room}: item mode refuses {What}; further refusals this round are not logged.", room.Id, what);
    }

    // Called on the room executor after the start burst. Only one worker belongs to each round generation.
    private void StartSupplies(PlayRoom room)
    {
        if (_supplyCreated is null || room.Supplies is not { } state || state.Running
            || state.Generation != room.StartGeneration || state.Points.Length == 0)
            return;
        state.Running = true;
        var now = Environment.TickCount64;
        foreach (var point in state.Points) point.NextAt = now + SupplyInterval(point.Point);
        state.Worker = BackgroundWork.Detached(() => RunSuppliesAsync(room, state));
    }

    private static int SupplyInterval(RoundSupplyPoint point) => Random.Shared.Next(point.MinimumMs, point.MaximumMs + 1);

    private async Task RunSuppliesAsync(PlayRoom room, RoundSupplyState state)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(_options.SupplyPollMs, _lifetime).ConfigureAwait(false);
                var keepRunning = false;
                await room.Executor.ExecuteAsync(async operationCt =>
                {
                    RoomSeat[] members;
                    var created = new List<RoundSupplyCreated>();
                    lock (room.Gate)
                    {
                        if (room.Phase != RoomPhase.Playing || room.StartGeneration != state.Generation
                            || !ReferenceEquals(room.Supplies, state)) return;
                        keepRunning = true;
                        members = [.. room.Seats];
                        var now = Environment.TickCount64;
                        foreach (var point in state.Points)
                        {
                            if (now < point.NextAt) continue;
                            // Replenishment keeps unpicked boxes and renews picked points on their original tick.
                            point.NextAt = now + SupplyInterval(point.Point);
                            if (point.ActiveId != 0) continue;
                            point.ActiveId = checked(++state.LastId);
                            created.Add(new RoundSupplyCreated(point.ActiveId, point.Point));
                        }
                    }
                    foreach (var supply in created)
                        foreach (var member in members)
                            await _supplyCreated!.SendAsync(member.Game, supply, operationCt).ConfigureAwait(false);
                }, _lifetime).ConfigureAwait(false);
                if (!keepRunning) return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: supply refresh failed.", room.Id);
        }
    }
}

internal sealed class RoundSupplyState(int generation, IReadOnlyList<RoundSupplyPoint> points, int objectIdBase)
{
    internal int Generation { get; } = generation;
    internal SupplyPointState[] Points { get; } = points.Select(point => new SupplyPointState(point)).ToArray();
    internal int LastId { get; set; } = objectIdBase;
    internal Dictionary<int, int> Dropped { get; } = [];
    internal Dictionary<int, HeldSupplyItems> HeldItems { get; } = [];
    internal Dictionary<(int Seat, int Kind), int> CountedItems { get; } = [];
    internal bool Running { get; set; }
    internal bool RefusalLogged { get; set; }
    internal Task Worker { get; set; } = Task.CompletedTask;
}

internal sealed class HeldSupplyItems(int capacity)
{
    private readonly int[] _kinds = new int[capacity];
    internal int SelectedSlot { get; private set; }
    internal int SelectedKind => _kinds[SelectedSlot] > 0 ? _kinds[SelectedSlot] : -1;
    internal int FirstEmptySlot() => Array.IndexOf(_kinds, 0);

    internal void Add(int slot, int kind)
    {
        // cn-324 sub_4CC5C0 selects a newly granted slot when the previous selection is empty.
        if (SelectedKind < 0) SelectedSlot = slot;
        _kinds[slot] = kind;
    }

    internal bool TryUse(out int slot, out int kind)
    {
        slot = SelectedSlot;
        kind = _kinds[slot];
        if (kind <= 0) return false;
        // cn-324 sub_4E2160 clears only the specified slot and preserves the selection.
        _kinds[slot] = 0;
        return true;
    }

    internal bool TrySwitch()
    {
        if (_kinds.Length != 2 || _kinds[1 - SelectedSlot] <= 0) return false;
        SelectedSlot = 1 - SelectedSlot;
        return true;
    }
}

internal sealed class SupplyPointState(RoundSupplyPoint point)
{
    internal RoundSupplyPoint Point { get; } = point;
    internal long NextAt { get; set; }
    internal int ActiveId { get; set; }
}
