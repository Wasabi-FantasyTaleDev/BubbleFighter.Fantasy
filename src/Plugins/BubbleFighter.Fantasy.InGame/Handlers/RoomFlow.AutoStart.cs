using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    // cn-324 sub_5AD610 runs the native room countdown for five seconds after gspRepAutoStart.
    private const int NativeAutoStartMs = 5000;

    private bool IsAutoStartMode(int modeId) => _options is { } options && options.AutoStartModeIds.Contains(modeId);

    // An automatic-start room has no start button: once no seat is left open, every seat taken or closed by the
    // master, the clients run their native five-second countdown (cn-324 sub_5AD610, id-400 sub_627C60) and the
    // master's client asks to enter with its resolved map. Runs on the room's executor.
    private async ValueTask ArmWhenFullAsync(PlayRoom room, CancellationToken ct)
    {
        lock (room.Gate)
        {
            if (!IsAutoStartMode(room.ModeId) || room.Phase != RoomPhase.Waiting || room.AutoStartArmed
                || room.OccupantCount + room.ClosedSlots.Count < room.MaxSeats)
                return;
            if (room.AutoStartRematchAfter is { } after
                && (_autoStartNow() < after || room.Seats.Any(seat => seat.Slot != room.MasterSlot && !seat.Ready)))
                return;
            room.AutoStartCancellation?.Cancel();
        }
        await ArmNativeAutoStartAsync(room, ct).ConfigureAwait(false);
    }

    // Back from a round, the room holds before it may start again, so a full room does not restart while its
    // players are still leaving the result; when the hold is over the full-room check runs once more.
    private void HoldRematch(PlayRoom room)
    {
        int generation;
        lock (room.Gate)
        {
            if (!IsAutoStartMode(room.ModeId))
                return;
            room.AutoStartRematchAfter = _autoStartNow() + _options!.AutoStartRematchHoldMs;
            generation = room.StartGeneration;
        }
        _ = BackgroundWork.Detached(async () =>
        {
            try
            {
                await _autoStartDelay(TimeSpan.FromMilliseconds(_options.AutoStartRematchHoldMs), _lifetime).ConfigureAwait(false);
                // An early timer wake must retry the deadline rather than lose the only arm attempt.
                // Keep the wait outside the executor so ready and leave requests can still run.
                while (true)
                {
                    long remaining;
                    lock (room.Gate)
                    {
                        if (!ReferenceEquals(_registry.Find(room.Id), room) || room.StartGeneration != generation)
                            return;
                        remaining = room.AutoStartRematchAfter is { } deadline
                            ? deadline - _autoStartNow()
                            : 0;
                    }
                    if (remaining <= 0)
                        break;
                    await _autoStartDelay(TimeSpan.FromMilliseconds(remaining), _lifetime).ConfigureAwait(false);
                }
                await room.Executor.ExecuteAsync(async ct =>
                {
                    lock (room.Gate)
                        if (!ReferenceEquals(_registry.Find(room.Id), room) || room.StartGeneration != generation)
                            return;
                    await ArmWhenFullAsync(room, ct).ConfigureAwait(false);
                }, _lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Room {Room} automatic-start hold after the round failed.", room.Id);
            }
        });
    }

    private ValueTask ScheduleAutoStartAsync(PlayRoom room, CancellationToken ct) =>
        new(room.Executor.ExecuteAsync(operationCt => ScheduleAutoStartCoreAsync(room, operationCt), ct));

    private async ValueTask ScheduleAutoStartCoreAsync(PlayRoom room, CancellationToken ct)
    {
        if (_options is not { AutoStartTimer: true } options || !IsAutoStartMode(room.ModeId))
            return;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Waiting
                || (room.AutoStartTask is { IsCompleted: false } && room.AutoStartCancellation is { IsCancellationRequested: false }))
                return;
        }
        await SendAutoStartNoticeAsync(room, "room.auto_start.created", (options.AutoStartDelayMs - NativeAutoStartMs) / 1000, ct)
                .ConfigureAwait(false);
        lock (room.Gate)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            room.AutoStartCancellation = cancellation;
            room.AutoStartTask = BackgroundWork.Detached(() => NotifyAutoStartAfterWaitAsync(room, cancellation));
        }
    }

    private async Task NotifyAutoStartAfterWaitAsync(PlayRoom room, CancellationTokenSource cancellation)
    {
        try
        {
            var remainingMs = _options!.AutoStartDelayMs - NativeAutoStartMs;
            foreach (var seconds in _options.AutoStartNoticeSeconds.OrderDescending().Append(0))
            {
                var nextMs = checked(seconds * 1000);
                if (nextMs >= remainingMs && seconds != 0)
                    continue;
                await Task.Delay(remainingMs - nextMs, cancellation.Token).ConfigureAwait(false);
                remainingMs = nextMs;
                await room.Executor.ExecuteAsync(async ct =>
                {
                    lock (room.Gate)
                    {
                        if (!ReferenceEquals(_registry.Find(room.Id), room) || room.Phase != RoomPhase.Waiting
                            || !IsAutoStartMode(room.ModeId) || room.AutoStartArmed)
                            return;
                    }
                    if (seconds == 0)
                        await ArmNativeAutoStartAsync(room, ct).ConfigureAwait(false);
                    else
                        await SendAutoStartNoticeAsync(room, "room.auto_start.remaining", seconds, ct).ConfigureAwait(false);
                }, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_registry.Find(room.Id) is null) { }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room} automatic-start notification failed.", room.Id);
        }
        finally
        {
            lock (room.Gate)
                if (ReferenceEquals(room.AutoStartCancellation, cancellation))
                    room.AutoStartCancellation = null;
            cancellation.Dispose();
        }
    }

    private async ValueTask SendAutoStartNoticeAsync(PlayRoom room, string key, int seconds, CancellationToken ct)
    {
        RoomSeat[] members;
        lock (room.Gate)
            members = [.. room.Seats];
        var arguments = new Dictionary<string, string>
        {
            ["seconds"] = seconds.ToString(CultureInfo.InvariantCulture),
            ["minutes"] = (seconds / 60.0).ToString("0.##", CultureInfo.InvariantCulture),
        };
        foreach (var member in members)
            await _notices!.SendAsync(member.Game, new ServerNotice(_texts!.Format(member.Game, key, arguments)), ct)
                .ConfigureAwait(false);
    }

    private async ValueTask ArmNativeAutoStartAsync(PlayRoom room, CancellationToken ct)
    {
        RoomSeat[] members;
        lock (room.Gate)
        {
            if (room.AutoStartArmed || room.Phase != RoomPhase.Waiting || !IsAutoStartMode(room.ModeId))
                return;
            room.AutoStartArmed = true;
            members = [.. room.Seats];
        }
        foreach (var member in members)
            await _autoStart!.SendAsync(member.Game, new RoomAutoStartNotified(), ct).ConfigureAwait(false);
        // cn-324 sub_5AD610 calls sub_5AE9A0 on the master after resolving the selected map.
        // The client start request owns map selection; a competing server timer can enter an unresolved map.
        _logger.LogInformation("Room {Room} native automatic-start countdown armed for {Count} members; awaiting the master's selected map.", room.Id, members.Length);
    }

    private async ValueTask StartFromCommandAsync(ISession session, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room)
            return;
        if (IsAutoStartMode(room.ModeId))
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                lock (room.Gate)
                {
                    var seat = room.FindByGame(session.Id);
                    if (seat is null || (!_commands.SharedDispatch && seat.Slot != room.MasterSlot)
                        || room.Phase != RoomPhase.Waiting)
                        return;
                    room.AutoStartCancellation?.Cancel();
                }
                await ArmNativeAutoStartAsync(room, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        else
            await room.Executor.ExecuteAsync(operationCt =>
            {
                RoomStartRequested request;
                lock (room.Gate)
                    request = new(room.MapKey) { Force = true, MapSelection = room.MapKey };
                return StartRoomCoreAsync(room, session, request, operationCt, allowMemberStart: _commands.SharedDispatch);
            }, ct).ConfigureAwait(false);
    }
}
