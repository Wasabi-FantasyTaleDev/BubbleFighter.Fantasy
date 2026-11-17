using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks the map-state parser against the developer's own native KR racing resources.</summary>
public sealed class NativeExtremeRacingMapStateTests
{
    /// <summary>Both native tracks provide complete bounded state, including every active vanish group.</summary>
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("rel_Racing01_Extreme", false)]
    [InlineData("rel_Racing02_Extreme", true)]
    public void NativeTracksProvideCompleteMapState(string stage, bool hasVanishGroups)
    {
        var store = new NativeStore();
        var options = new InGameOptions();
        Assert.Equal(5, ExtremeRacingMapData.ReadStageCount(store, stage, options.ExtremeRacingMaximumFileBytes));
        Assert.Equal(hasVanishGroups ? new[] { 20, 20, 20 } : Array.Empty<int>(),
            ExtremeRacingMapData.ReadVanishRouteCounts(store, stage, options));
    }

    private sealed class NativeStore : IClientDataStore
    {
        public bool Exists(string path) => path is
            "Maps/rel_Racing01_Extreme/option.ini" or "Maps/rel_Racing01_Extreme/track.wb" or
            "Maps/rel_Racing02_Extreme/option.ini" or "Maps/rel_Racing02_Extreme/track.wb";

        public byte[] Read(string path) => LocalClientData.Bytes("kr-1256", path);

        public byte[] Read(string path, int maximumBytes)
        {
            var bytes = Read(path);
            return bytes.Length <= maximumBytes ? bytes : throw new InvalidDataException("Native racing fixture exceeds its configured byte bound.");
        }
    }
}
