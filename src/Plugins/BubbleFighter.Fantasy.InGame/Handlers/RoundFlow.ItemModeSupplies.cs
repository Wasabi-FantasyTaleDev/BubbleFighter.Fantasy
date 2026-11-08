using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private IClientDataStore? _itemModeMapData;

    internal void ConfigureItemModeFallback(IClientDataStore data) => _itemModeMapData = data;

    private IReadOnlyList<RoundSupplyPoint> AddItemModeFallback(PlayRoom room, IReadOnlyList<RoundSupplyPoint> points)
    {
        if (!IsItemMode(room) || _options.ItemModeFallbackPointCount == 0 || points.Any(point => point.Kind == 0))
            return points;
        var count = Math.Min(_options.ItemModeFallbackPointCount, _options.MaxSupplyPoints - points.Count);
        if (count == 0) return points;
        var data = _itemModeMapData ?? throw new InvalidOperationException("Item-mode fallback requires client map data.");
        var stage = string.IsNullOrEmpty(room.RoundStageKey) ? room.RoundMapKey : room.RoundStageKey;
        var positions = ItemModeMapPoints.Load(data, stage, _options.ItemModeFallbackMaximumFileBytes,
            _options.ItemModeFallbackMaximumCandidates, count);
        var fallback = positions.Select(point => new RoundSupplyPoint(point.X, point.Y, point.Z, 0,
            _options.SupplyDefaultMinMs, _options.SupplyDefaultMaxMs)).ToArray();
        _logger.LogInformation("Room {Room}: item-mode server fallback schedules {Count} neutral boxes from {Stage}/track.wb.",
            room.Id, fallback.Length, stage);
        return [.. points, .. fallback];
    }
}
