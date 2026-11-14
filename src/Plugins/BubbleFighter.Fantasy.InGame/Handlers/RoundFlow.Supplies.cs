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

    // Item mode keeps the build's kind/team validation; the optional fallback schedules neutral boxes on existing map helpers.
    private bool IsItemMode(PlayRoom room) => _options.ItemModeIds.Contains(room.ModeId);

    private bool ItemModeAccepts(int kind) => kind == 0 || _options.ItemModeKinds.Contains(kind);

    private readonly record struct SupplyRequestStamp(int Generation, RoomSeat Reporter);

    private static SupplyRequestStamp? CaptureSupplyRequest(PlayRoom room, ISession session, bool allowStarting = false)
    {
        lock (room.Gate)
        {
            if (room.IsStopping || room.RoundCancelled || (room.Phase != RoomPhase.Playing && !(allowStarting && room.Phase == RoomPhase.Starting))
                || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)) return null;
            return new(room.StartGeneration, seat);
        }
    }

    private static bool SupplyRequestIsCurrent(PlayRoom room, ISession session, SupplyRequestStamp stamp) =>
        !room.IsStopping && !room.RoundCancelled && room.StartGeneration == stamp.Generation
        && ReferenceEquals(stamp.Reporter.Game, session) && ReferenceEquals(room.FindByGame(session.Id), stamp.Reporter);

    internal async ValueTask HandleSupplyDropAsync(ISession session, RoundSupplyDropRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || CaptureSupplyRequest(room, session) is not { } stamp) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundSupplyCreated created;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !SupplyRequestIsCurrent(room, session, stamp)) return;
                if (request.Point.Kind < 0 || request.Point.Team is < 0 or > 2 || !float.IsFinite(request.Point.X) || !float.IsFinite(request.Point.Y)
                    || !float.IsFinite(request.Point.Z) || !float.IsFinite(request.Velocity.X)
                    || !float.IsFinite(request.Velocity.Y) || !float.IsFinite(request.Velocity.Z))
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
                created = new(id, request.Point) { CreationClass = request.CreationClass, Velocity = request.Velocity };
                state.Dropped.Add(id, created);
                members = [.. room.Seats];
                if (_options.RenewFullSlotSupplies) StartSupplies(room);
            }
            foreach (var member in members)
                await _supplyCreated!.SendAsync(member.Game, created, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleSuppliesReportedAsync(ISession session, RoundSuppliesReported report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || CaptureSupplyRequest(room, session, allowStarting: true) is not { } stamp) return;
        await room.Executor.ExecuteAsync(operationCt =>
        {
            lock (room.Gate)
            {
                if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing)
                    || !SupplyRequestIsCurrent(room, session, stamp)
                    || room.FindByGame(session.Id) is not { } reporter || reporter.Slot != room.MasterSlot)
                    return ValueTask.CompletedTask;
                if (room.Supplies is { MapReported: true } existing && existing.Generation == room.StartGeneration)
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
                var accepted = IsItemMode(room) ? reported.Where(point => ItemModeAccepts(point.Kind)).ToArray() : reported;
                var points = AddItemModeFallback(room, accepted);
                if (room.Supplies is not { } state || state.Generation != room.StartGeneration)
                    room.Supplies = state = new RoundSupplyState(room.StartGeneration, [], _options.SupplyObjectIdBase);
                state.SetMapPoints(points);
                if (accepted.Count != report.Points.Count)
                    NoteItemModeRefusal(room, state, $"{report.Points.Count - accepted.Count} map point(s) of an unoffered kind");
                if (room.Phase == RoomPhase.Playing) StartSupplies(room);
            }
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleSupplyPickupAsync(ISession session, RoundSupplyPickupRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || CaptureSupplyRequest(room, session) is not { } stamp) return;
        if (request.ObjectId == UltraRequestObject && _options.CaptainUltraItemKind > 0
            && request.Kind == _options.CaptainUltraItemKind && IsCaptainMode(room))
        {
            await HandleCaptainUltraAsync(room, session, ct).ConfigureAwait(false);
            return;
        }
        if (_options.ZombieKingUltraItemKind > 0 && request.Kind == _options.ZombieKingUltraItemKind && request.ObjectId == UltraRequestObject)
        {
            await HandleZombieKingUltraAsync(room, session, ct).ConfigureAwait(false);
            return;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoundSupplyPicked picked;
            bool immediate;
            int resolvedKind;
            int? retainedItemSlot = null;
            int? retainedItemKind = null;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !SupplyRequestIsCurrent(room, session, stamp) || room.FindByGame(session.Id) is not { } seat
                    || room.Supplies is not { } state || state.Generation != room.StartGeneration)
                    return;
                var itemMode = IsItemMode(room);
                resolvedKind = request.Kind;
                if (itemMode && _options.ItemModeResolveInvalidRandomPickups && (resolvedKind <= 0 || !ItemModeAccepts(resolvedKind)))
                {
                    var target = state.Points.FirstOrDefault(point => point.ActiveId != 0 && point.ActiveId == request.ObjectId)?.Point
                        ?? state.Dropped.GetValueOrDefault(request.ObjectId)?.Point;
                    if (target is not { Kind: 0 } || target.Team != 0 && target.Team != seat.Team) return;
                    var candidates = _options.ItemModeKinds.Intersect(_options.RandomSupplyItemKinds).ToArray();
                    resolvedKind = candidates[Random.Shared.Next(candidates.Length)];
                }
                if (resolvedKind <= 0 || itemMode && !ItemModeAccepts(resolvedKind))
                {
                    NoteItemModeRefusal(room, state, $"a pickup of kind {resolvedKind}");
                    return;
                }
                var countedMode = _options.CountedItemModeId > 0 && room.ModeId == _options.CountedItemModeId;
                var counted = countedMode && _options.CountedItemKinds.Contains(resolvedKind);
                immediate = _options.ImmediateSupplyItemKinds.Contains(resolvedKind)
                    || (countedMode && _options.CountedImmediateItemKinds.Contains(resolvedKind));
                if (immediate && state.HeldItems.TryGetValue(seat.Slot, out var retained) && retained.SelectedKind > 0)
                {
                    retainedItemSlot = retained.SelectedSlot;
                    retainedItemKind = retained.SelectedKind;
                }
                var slotted = !immediate && _options.RandomSupplyItemKinds.Contains(resolvedKind);
                // kr-1256 sub_C13F30 resolves a random box before reporting the kind; immediate effects have no held slot.
                bool MatchesKind(int boxKind) => boxKind == resolvedKind || (boxKind == 0
                    && (_options.RandomSupplyItemKinds.Contains(resolvedKind) || immediate));
                var countKey = (seat.Slot, resolvedKind);
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
                state.Dropped.TryGetValue(request.ObjectId, out var dropped);
                if (point is null)
                {
                    if (dropped is null || !MatchesKind(dropped.Point.Kind)) return;
                    if (itemMode && dropped.Point.Team != 0 && dropped.Point.Team != seat.Team)
                    {
                        NoteItemModeRefusal(room, state, $"a pickup of a dropped team {dropped.Point.Team} box by a team {seat.Team} seat");
                        return;
                    }
                }
                var itemSlot = 0;
                HeldSupplyItems? held = null;
                if (slotted && !counted)
                {
                    if (!state.HeldItems.TryGetValue(seat.Slot, out held))
                        state.HeldItems.Add(seat.Slot, held = new(_options.NativeItemSlotCount));
                    itemSlot = held.FirstEmptySlot();
                    if (itemSlot < 0)
                    {
                        // kr-812 sub_6378A0 latches the box before sending even when both native slots are full.
                        if (_options.RenewFullSlotSupplies)
                        {
                            if (!state.FullSlotRefusals.TryGetValue(seat.Slot, out var refused))
                                state.FullSlotRefusals.Add(seat.Slot, refused = []);
                            refused.Add(request.ObjectId);
                        }
                        return;
                    }
                }
                if (point is not null) point.ActiveId = 0;
                else state.Dropped.Remove(request.ObjectId);
                state.ForgetRefusal(request.ObjectId);
                if (counted)
                    state.CountedItems[countKey] = state.CountedItems.GetValueOrDefault(countKey) + 1;
                else if (slotted)
                    held!.Add(itemSlot, resolvedKind);
                picked = new RoundSupplyPicked(seat.Slot, request.ObjectId)
                    { ItemKind = slotted ? resolvedKind : 0, ItemSlot = itemSlot };
                members = [.. room.Seats];
            }
            foreach (var member in members)
                await _supplyPicked!.SendAsync(member.Game, picked, operationCt).ConfigureAwait(false);
            // cn-324 sub_46DBE0 removes the box; sub_46FED0 separately requests its native effect.
            if (immediate)
                await _countedItemUsed!.SendAsync(session,
                    new(picked.Seat, resolvedKind) { RetainedItemSlot = retainedItemSlot, RetainedItemKind = retainedItemKind },
                    operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleItemUseAsync(ISession session, RoundItemUseRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || CaptureSupplyRequest(room, session) is not { } stamp) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            int slot;
            int kind;
            int itemSlot;
            int nextItemSlot;
            int nextItemKind;
            List<(int PreviousId, RoundSupplyCreated Created)> renewed;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !SupplyRequestIsCurrent(room, session, stamp)
                    || room.FindByGame(session.Id) is not { Life.ServerTimedWait: false } seat
                    || room.Supplies is not { } state || state.Generation != room.StartGeneration
                    || !state.HeldItems.TryGetValue(seat.Slot, out var held) || !held.TryUse(out itemSlot, out kind))
                    return;
                slot = seat.Slot;
                if (_options.AutoAdvanceItemSlotAfterUse && held.TrySelectFirstOccupied(out nextItemSlot, out nextItemKind))
                {
                    // The native gspSwitchItemSlot consumer installs the selected slot and its kind for every peer.
                    // Keep this behind the build capability until that consumer has been read for the build.
                }
                else
                {
                    nextItemSlot = -1;
                    nextItemKind = -1;
                }
                members = [.. room.Seats];
                renewed = RenewAvailableSupplies(room, state);
            }
            foreach (var member in members)
                await _itemUsed!.SendAsync(member.Game, new RoundItemUsed(slot) { Kind = kind, ItemSlot = itemSlot }, operationCt).ConfigureAwait(false);
            if (nextItemSlot >= 0)
                foreach (var member in members)
                    await _itemSlotChanged!.SendAsync(member.Game,
                        new RoundItemSlotChanged(slot, nextItemSlot, nextItemKind), operationCt).ConfigureAwait(false);
            await SendRenewedSuppliesAsync(members, renewed, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleItemSlotSwitchAsync(ISession session, RoundItemSlotSwitchRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
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
                var switched = !seat.Life.ServerTimedWait && held is not null && held.TrySwitch();
                changed = new(seat.Slot, held?.SelectedSlot ?? 0, held?.SelectedKind ?? -1, !switched);
                members = switched ? [.. room.Seats] : [seat];
            }
            foreach (var member in members)
                await _itemSlotChanged!.SendAsync(member.Game, changed, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async ValueTask HandleCountedItemUseAsync(ISession session, RoundCountedItemUseRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundCountedItemUsed used;
            lock (room.Gate)
            {
                if (_options.CountedItemModeId <= 0 || room.ModeId != _options.CountedItemModeId
                    || room.Phase != RoomPhase.Playing || request.ItemSlot <= 0 || request.ItemSlot > _options.CountedItemKinds.Length
                    || room.FindByGame(session.Id) is not { Life.ServerTimedWait: false } seat
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

    private List<(int PreviousId, RoundSupplyCreated Created)> RenewAvailableSupplies(PlayRoom room, RoundSupplyState state)
    {
        List<(int, RoundSupplyCreated)> renewed = [];
        if (!_options.RenewFullSlotSupplies || state.FullSlotRefusals.Count == 0) return renewed;
        HashSet<int> eligible = [];
        foreach (var (seat, ids) in state.FullSlotRefusals)
            if (!room.Seats.Any(member => member.Slot == seat)
                || !state.HeldItems.TryGetValue(seat, out var held) || held.FirstEmptySlot() >= 0)
                eligible.UnionWith(ids);
        foreach (var previousId in eligible.Order())
        {
            var point = state.Points.FirstOrDefault(value => value.ActiveId == previousId);
            if (point is not null)
            {
                point.ActiveId = checked(++state.LastId);
                renewed.Add((previousId, new(point.ActiveId, point.Point)));
            }
            else if (state.Dropped.Remove(previousId, out var dropped))
            {
                var created = dropped with { ObjectId = checked(++state.LastId), Velocity = default };
                state.Dropped.Add(created.ObjectId, created);
                renewed.Add((previousId, created));
            }
            state.ForgetRefusal(previousId);
        }
        return renewed;
    }

    private async ValueTask SendRenewedSuppliesAsync(RoomSeat[] members,
        List<(int PreviousId, RoundSupplyCreated Created)> renewed, CancellationToken ct)
    {
        foreach (var (previousId, created) in renewed)
        {
            // kr-812 sub_515290 removes seat -1 without a pickup event; sub_519480 constructs the fresh id.
            foreach (var member in members)
                await _supplyPicked!.SendAsync(member.Game, new(-1, previousId), ct).ConfigureAwait(false);
            foreach (var member in members)
                await _supplyCreated!.SendAsync(member.Game, created, ct).ConfigureAwait(false);
        }
    }

    // Called on the room executor after the start burst. Only one worker belongs to each round generation.
    private void StartSupplies(PlayRoom room)
    {
        if (_supplyCreated is null || room.Supplies is not { } state || state.Running
            || state.Generation != room.StartGeneration || (state.Points.Length == 0 && !_options.RenewFullSlotSupplies))
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
                    List<(int PreviousId, RoundSupplyCreated Created)> renewed;
                    lock (room.Gate)
                    {
                        if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping || room.StartGeneration != state.Generation
                            || !ReferenceEquals(room.Supplies, state)) return;
                        keepRunning = true;
                        members = [.. room.Seats];
                        renewed = RenewAvailableSupplies(room, state);
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
                    await SendRenewedSuppliesAsync(members, renewed, operationCt).ConfigureAwait(false);
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
    internal SupplyPointState[] Points { get; private set; } = points.Select(point => new SupplyPointState(point)).ToArray();
    internal bool MapReported { get; private set; }
    internal void SetMapPoints(IReadOnlyList<RoundSupplyPoint> reported)
    {
        Points = reported.Select(point => new SupplyPointState(point)).ToArray();
        MapReported = true;
        if (Running)
        {
            var now = Environment.TickCount64;
            foreach (var point in Points)
                point.NextAt = now + Random.Shared.Next(point.Point.MinimumMs, point.Point.MaximumMs + 1);
        }
    }
    internal int LastId { get; set; } = objectIdBase;
    internal Dictionary<int, RoundSupplyCreated> Dropped { get; } = [];
    // Each seat records only still-live, kind/team-validated objects; both object collections are bounded.
    internal Dictionary<int, HashSet<int>> FullSlotRefusals { get; } = [];
    internal void ForgetRefusal(int objectId)
    {
        foreach (var seat in FullSlotRefusals.Keys.ToArray())
        {
            FullSlotRefusals[seat].Remove(objectId);
            if (FullSlotRefusals[seat].Count == 0) FullSlotRefusals.Remove(seat);
        }
    }
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

    internal bool TrySelectFirstOccupied(out int slot, out int kind)
    {
        slot = Array.FindIndex(_kinds, value => value > 0);
        if (slot < 0)
        {
            kind = -1;
            return false;
        }
        SelectedSlot = slot;
        kind = _kinds[slot];
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
