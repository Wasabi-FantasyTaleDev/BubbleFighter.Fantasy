using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    private void PrepareRescues(PlayRoom room)
    {
        room.Rescues = null;
        if (_options?.RescueModeId <= 0 || _options is null || room.ModeId != _options.RescueModeId) return;
        var map = _options.RescueMapEnemyCounts.FirstOrDefault(pair => pair.MapKey.Equals(room.RoundMapKey, StringComparison.OrdinalIgnoreCase));
        if (map is null)
        {
            _logger.LogWarning("Room {Room}: map '{Map}' has no rescue definitions; native map play remains active.", room.Id, room.RoundMapKey);
            return;
        }
        // cn-324 sub_8AE860 creates no mission spawners when MAXINGAMEMISSIONCNT is zero.
        if (map.EnemyCounts.Length == 0) return;
        var missions = new RoundRescueMission[map.EnemyCounts.Length];
        for (var index = 0; index < missions.Length; index++)
        {
            var role = Random.Shared.Next(_options.RescueMissionIds.Length);
            // cn-324 sub_8B3590 resolves these arguments against the map's existing mission spawners.
            missions[index] = new(_options.RescueMissionIds[role], checked(_options.StartTimerMs + _options.RescueStartTimesMs[index]),
                _options.RescueDurationMs, ["killmonster", "1", map.EnemyCounts[index].ToString(CultureInfo.InvariantCulture), _options.RescueCitizenNames[role]]);
        }
        room.Rescues = new(room.StartGeneration, missions, map.EnemyCounts);
    }
}

internal sealed partial class RoundFlow
{
    private readonly IMessageSender<RoundRescueMissionsPrepared>? _rescuePrepared;
    private readonly IMessageSender<RoundRescueMissionStarted>? _rescueStarted;
    private readonly IMessageSender<RoundRescueMissionFailed>? _rescueFailed;
    private readonly IMessageSender<RoundRescueMissionSucceeded>? _rescueSucceeded;

    internal async ValueTask HandleRescueProgressAsync(ISession session, RoundRescueProgressReported report, CancellationToken ct)
    {
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            bool failed;
            int index;
            lock (room.Gate)
            {
                if (_options.RescueModeId <= 0 || room.ModeId != _options.RescueModeId || room.Phase != RoomPhase.Playing
                    || room.Rescues is not { } state || state.Generation != room.StartGeneration
                    || room.FindByGame(session.Id) is not { } reporter || state.ActiveIndex < 0
                    || report.MissionIndex != state.ActiveIndex) return;
                if (report.CharacterType is not (100 or 101 or 102))
                    throw new InvalidOperationException("Cooperative rescue progress has an unsupported actor type.");
                // cn-324 sub_532330 reports the local killer or allied turret; converted players do not rescue humans.
                if (report.CharacterType != 100 && reporter.Team != 2) return;
                index = state.ActiveIndex;
                failed = report.CharacterType == 100 || Environment.TickCount64 >= state.ActiveDeadline;
                if (!failed && ++state.HostilesKilled < state.EnemyCounts[index]) return;
                state.ActiveIndex = -1;
                state.NextStartAt = Environment.TickCount64 + _options.RescueCooldownMs;
                members = [.. room.Seats];
            }
            foreach (var member in members)
                if (failed) await _rescueFailed!.SendAsync(member.Game, new(index + 1), operationCt).ConfigureAwait(false);
                else await _rescueSucceeded!.SendAsync(member.Game, new(index), operationCt).ConfigureAwait(false);
            _logger.LogInformation("Room {Room}: rescue {Index} {Outcome}.", room.Id, index, failed ? "failed" : "succeeded");
        }, ct).ConfigureAwait(false);
    }

    private void StartRescues(PlayRoom room)
    {
        if (_options.RescueModeId <= 0 || room.ModeId != _options.RescueModeId || room.Rescues is not { } state || state.Running) return;
        state.Running = true;
        state.ClockOrigin = Environment.TickCount64 - _options.StartTimerMs;
        state.NextStartAt = state.ClockOrigin + state.Missions[0].StartTimeMs;
        state.Worker = BackgroundWork.Detached(() => RunRescuesAsync(room, state));
    }

    private async Task RunRescuesAsync(PlayRoom room, RoundRescueState state)
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
                    var failedIndex = -1;
                    var startedIndex = -1;
                    lock (room.Gate)
                    {
                        if (room.Phase != RoomPhase.Playing || room.StartGeneration != state.Generation
                            || !ReferenceEquals(room.Rescues, state)) return;
                        var now = Environment.TickCount64;
                        if (state.ActiveIndex >= 0 && now >= state.ActiveDeadline)
                        {
                            failedIndex = state.ActiveIndex;
                            state.ActiveIndex = -1;
                            state.NextStartAt = now + _options.RescueCooldownMs;
                        }
                        // cn-324 ZombiePve map spawners create NameID 31 at native clock 185000.
                        // Leave enough time to finish a mission before that phase replaces its UI.
                        if (_options.RescueStopTimeMs > 0 && state.ActiveIndex < 0
                            && now - state.ClockOrigin + _options.RescueDurationMs >= _options.RescueStopTimeMs)
                            state.NextIndex = state.Missions.Length;
                        if (state.ActiveIndex < 0 && state.NextIndex < state.Missions.Length
                            && now >= state.NextStartAt)
                        {
                            startedIndex = state.NextIndex++;
                            state.ActiveIndex = startedIndex;
                            state.HostilesKilled = 0;
                            state.Missions[startedIndex] = state.Missions[startedIndex] with { StartTimeMs = checked((int)(now - state.ClockOrigin)) };
                            state.ActiveDeadline = now + state.Missions[startedIndex].DurationMs;
                        }
                        keepRunning = state.ActiveIndex >= 0 || state.NextIndex < state.Missions.Length;
                        members = [.. room.Seats];
                    }
                    foreach (var member in members)
                    {
                        if (failedIndex >= 0)
                            await _rescueFailed!.SendAsync(member.Game, new(failedIndex + 1), operationCt).ConfigureAwait(false);
                        if (startedIndex >= 0)
                        {
                            // cn-324 sub_6EE210 replaces timing data; sub_6EE410 then selects the active map spawner.
                            await _rescuePrepared!.SendAsync(member.Game, new(state.Missions), operationCt).ConfigureAwait(false);
                            await _rescueStarted!.SendAsync(member.Game, new(startedIndex), operationCt).ConfigureAwait(false);
                        }
                    }
                    if (startedIndex >= 0) _logger.LogInformation("Room {Room}: rescue {Index} started.", room.Id, startedIndex);
                    if (failedIndex >= 0) _logger.LogInformation("Room {Room}: rescue {Index} timed out.", room.Id, failedIndex);
                }, _lifetime).ConfigureAwait(false);
                if (!keepRunning) return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception failure) { _logger.LogError(failure, "Room {Room}: cooperative rescue scheduling failed.", room.Id); }
    }
}

internal sealed class RoundRescueState(int generation, RoundRescueMission[] missions, int[] enemyCounts)
{
    internal int Generation { get; } = generation;
    internal RoundRescueMission[] Missions { get; } = missions;
    internal int[] EnemyCounts { get; } = [.. enemyCounts];
    internal int ActiveIndex { get; set; } = -1;
    internal int NextIndex { get; set; }
    internal int HostilesKilled { get; set; }
    internal long ClockOrigin { get; set; }
    internal long ActiveDeadline { get; set; }
    internal long NextStartAt { get; set; }
    internal bool Running { get; set; }
    internal Task? Worker { get; set; }
}
