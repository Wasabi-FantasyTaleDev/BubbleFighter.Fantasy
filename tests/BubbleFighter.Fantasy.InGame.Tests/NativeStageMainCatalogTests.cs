using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The numbered tutorial selector has eight categories with three levels in each target build.</summary>
public sealed class NativeStageMainCatalogTests
{
    /// <summary>Checks the actual map table, including keys that differ from the game's physical map directory.</summary>
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void StageMainTutorialAdmitsTwentyFourNumberedChoicesFromItsOwnMapTable(string build)
    {
        var meta = MapMetaTable.Parse(LocalClientData.Bytes(build, MapMetaTable.Path));
        var catalog = new StageMainTutorialTable(meta);
        var accepted = new List<(int Number, string Map)>();
        foreach (var map in meta.Maps)
        {
            if (!int.TryParse(meta.Get(map, "Difficulty"), NumberStyles.None, CultureInfo.InvariantCulture, out var difficulty)
                || !int.TryParse(meta.Get(map, "TimeAttackTimeLimit"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0 || seconds > int.MaxValue / 1000) continue;
            var request = new InitialTutorialRequested(15, map, seconds * 1000, default)
                { StageDirectory = meta.Get(map, "Directory"), Difficulty = difficulty };
            if (catalog.Accepts(request)) accepted.Add((int.Parse(meta.Get(map, "stageNum"), CultureInfo.InvariantCulture), map));
        }
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, 17, 18, 21, 22, 23, 24, 25, 26, 27, 28 },
            accepted.Select(stage => stage.Number).Order());
        Assert.Contains((26, "sin_mission_wall_lv_03"), accepted);
        Assert.False(catalog.Accepts(new(15, "sin_mission_wall_lv_03", 50_000, default)
            { StageDirectory = "sin_mission_wall_lv_03", Difficulty = 3 }));
    }
}
