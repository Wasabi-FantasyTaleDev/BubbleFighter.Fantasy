using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private IClientDataStore? _racingMapStateData;
    private IMessageSender<RoundExtremeRacingMapState>? _racingMapStateSender;

    internal void ConfigureExtremeRacingMapState(IClientDataStore store, IMessageSender<RoundExtremeRacingMapState> sender)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sender);
        if (!_options.ExtremeRacingMapState || _extremeRacing is null)
            throw new ConfigException("extreme_racing_map_state requires the enabled racing exchange.");
        if (_options.ValidateExtremeRacingMapState().FirstOrDefault() is { } invalid)
            throw new ConfigException(invalid.ErrorMessage!);
        _racingMapStateData = store;
        _racingMapStateSender = sender;
    }

    private int[] ReadExtremeRacingMapState(string stage) => ExtremeRacingMapData.ReadVanishRouteCounts(
        _racingMapStateData ?? throw new InvalidOperationException("Racing map state has no client-data store."), stage, _options);

    private void PrepareExtremeRacingMapState(PlayRoom room, string stage)
    {
        if (_options.ExtremeRacingMapState && room.ExtremeRacing.MapState is null)
            room.ExtremeRacing.MapState = ExtremeRacingMapData.SelectMapState(ReadExtremeRacingMapState(stage), _options);
    }

    private async ValueTask PublishExtremeRacingMapStateAsync(PlayRoom room, RoomSeat[] members, int generation, CancellationToken ct)
    {
        if (!_options.ExtremeRacingMapState || !IsExtremeRacingMode(room)) return;
        ExtremeRacingStamp stamp;
        RoundExtremeRacingMapState message;
        lock (room.Gate)
        {
            stamp = new(generation, room.ExtremeRacing);
            message = stamp.Race.MapState ?? throw new InvalidOperationException("Racing map state was not prepared.");
        }
        try
        {
            foreach (var member in members)
            {
                lock (room.Gate)
                {
                    if (!Current()) throw new InvalidOperationException("Racing membership changed during map-state publication.");
                    if (stamp.Race.MapStateRecipients.Contains(member.Game)) continue;
                }
                // kr-1256 sub_67B860 builds stone emitters one stage early; publish their seed before any stage advance.
                await _racingMapStateSender!.SendAsync(member.Game, message, ct).ConfigureAwait(false);
                lock (room.Gate)
                {
                    if (!Current()) throw new InvalidOperationException("Racing state changed while map state was sent.");
                    stamp.Race.MapStateRecipients.Add(member.Game);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            await CancelExtremeRacingAsync(room, stamp).ConfigureAwait(false);
            throw;
        }

        bool Current() => OwnsExtremeRacing(room, stamp) && room.Phase == RoomPhase.Starting
            && !room.RoundCancelled && !room.IsStopping && room.Seats.Count == members.Length
            && members.All(member => ReferenceEquals(room.FindByGame(member.Game.Id), member)
                && !member.Game.Connection.IsClosed && member.SceneReady
                && (!_options.NativeMatchRelay || NativeMatchReady(room, member)));
    }
}
