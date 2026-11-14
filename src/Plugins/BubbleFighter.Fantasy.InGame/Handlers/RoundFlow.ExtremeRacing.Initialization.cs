using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private IMessageSender<RoundExtremeRacingInitialized>? _racingInitialized;
    private IMessageSender<RoundExtremeRacingCannonCommand>? _racingCannonCommand;
    private IReadOnlyDictionary<string, ExtremeRacingMapData>? _racingMaps;

    private void ValidateExtremeRacingMap(PlayRoom room, string stage)
    {
        if (!room.ExtremeRacingCleanupTask.IsCompleted)
            throw new InvalidOperationException("A match cannot start while its previous racing connection cleanup is pending.");
        if (!IsExtremeRacingMode(room)) return;
        if (_options.ExtremeRacingInitialization && _racingMaps?.ContainsKey(stage) != true)
            throw new InvalidOperationException($"Racing stage '{stage}' has no validated initialization policy.");
    }

    internal void ConfigureExtremeRacingInitialization(IClientDataStore store,
        IMessageSender<RoundExtremeRacingInitialized> sender, IMessageSender<RoundExtremeRacingCannonCommand>? cannonCommand = null)
    {
        if (!_options.ExtremeRacingInitialization || _extremeRacing is null)
            throw new ConfigException("Racing initialization requires the enabled racing round exchange.");
        if (_options.ValidateExtremeRacingInitialization().FirstOrDefault() is { } invalid)
            throw new ConfigException(invalid.ErrorMessage!);
        var limits = new ExtremeRacingMapLimits(_options.ExtremeRacingMaximumFileBytes,
            _options.ExtremeRacingMaximumHelpers, _options.ExtremeRacingMaximumObjects);
        var maps = new Dictionary<string, ExtremeRacingMapData>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in _options.ExtremeRacingStages)
        {
            var policy = new ExtremeRacingMapPolicy(stage.FirstCannonIdentity, stage.FirstCoinIdentity,
                stage.FirstStoneDisposerIdentity, stage.CoinVariant!.Value, stage.CoinRounds);
            var map = ExtremeRacingMapData.Load(store, stage.StageDirectory, policy, limits,
                Enumerable.Range(0, 16).Select(RoomRegistry.SceneEntity).ToArray());
            if (map.StageCount != _options.ExtremeRacingRounds)
                throw new ConfigException($"Racing stage '{stage.StageDirectory}' provides {map.StageCount} rounds; extreme_racing_rounds is {_options.ExtremeRacingRounds}.");
            if (_options.ExtremeRacingCannon)
                map = map with
                {
                    Cannons = ExtremeRacingCannonSchedule.Load(store, stage.StageDirectory, map.CannonPairCount, map.StageCount,
                        limits.MaximumFileBytes, limits.MaximumObjects),
                };
            maps.Add(stage.StageDirectory, map);
        }
        if (_options.ExtremeRacingCannonFire && cannonCommand is null)
            throw new ConfigException("extreme_racing_cannon_fire requires the build's cannon command sender.");
        _racingMaps = maps;
        _racingInitialized = sender;
        _racingCannonCommand = cannonCommand;
    }

    private void PrepareExtremeRacingInitialization(PlayRoom room)
    {
        if (!IsExtremeRacingMode(room) || !_options.ExtremeRacingInitialization) return;
        lock (room.Gate)
        {
            if (room.RacingInitialization?.Generation == room.StartGeneration) return;
            var stage = room.RoundStageKey.Length > 0 ? room.RoundStageKey : room.RoundMapKey;
            if (_racingMaps is null || !_racingMaps.TryGetValue(stage, out var map))
                throw new InvalidOperationException($"Racing stage '{stage}' has no validated initialization policy.");
            room.RacingInitialization = new(room.StartGeneration, map);
        }
    }

    private async ValueTask PublishExtremeRacingInitializationAsync(PlayRoom room, RoomSeat[] members,
        int generation, CancellationToken ct)
    {
        if (!IsExtremeRacingMode(room) || !_options.ExtremeRacingInitialization) return;
        ExtremeRacingInitializationState state;
        ExtremeRacingStamp stamp;
        lock (room.Gate)
        {
            state = room.RacingInitialization ?? throw new InvalidOperationException("Racing initialization was not prepared.");
            stamp = new(generation, room.ExtremeRacing, state);
        }
        try
        {
            foreach (var member in members)
            {
                lock (room.Gate)
                {
                    if (!RacingInitializationIsCurrent(room, state, members, stamp))
                        throw new InvalidOperationException("Racing membership changed during initialization.");
                    if (state.Recipients.Contains(member.Game)) continue;
                }
                // kr-1256 sub_CE6F80 allocates coin objects immediately and appends their identities on every call.
                await _racingInitialized!.SendAsync(member.Game, state.Map.Initialization, ct).ConfigureAwait(false);
                lock (room.Gate)
                {
                    if (!RacingInitializationIsCurrent(room, state, members, stamp))
                        throw new InvalidOperationException("Racing round changed while its initialization was sent.");
                    state.Recipients.Add(member.Game);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            await CancelExtremeRacingAsync(room, stamp).ConfigureAwait(false);
            throw;
        }
    }

    private bool RacingInitializationIsCurrent(PlayRoom room, ExtremeRacingInitializationState state,
        RoomSeat[] members, ExtremeRacingStamp stamp) => ReferenceEquals(room.RacingInitialization, state)
        && state.Generation == stamp.Generation && OwnsExtremeRacing(room, stamp)
        && room.Phase == RoomPhase.Starting && !room.RoundCancelled && !room.IsStopping
        && room.Seats.Count == members.Length && members.All(member =>
            ReferenceEquals(room.FindByGame(member.Game.Id), member) && !member.Game.Connection.IsClosed
            && member.SceneReady && (!_options.NativeMatchRelay || NativeMatchReady(room, member)));
}
