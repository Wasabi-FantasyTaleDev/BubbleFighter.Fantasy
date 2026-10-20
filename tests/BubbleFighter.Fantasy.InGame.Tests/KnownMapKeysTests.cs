using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins for the map-key set sourced from the shipped client data, so the set
/// cannot drift from the CSV it was read out of.
/// </summary>
public sealed class KnownMapKeysTests
{
    /// <summary>The eight keys the CSV binds, in the order it lists them.</summary>
    [Fact]
    public void AllHoldsTheEightShippedKeys()
    {
        Assert.Equal(
            new[]
            {
                "rel_Desert00_beguy",
                "rel_Forest00_floopy",
                "rel_pirate08_island",
                "rel_space04_64room",
                "rel_Sudden00_Survival",
                "rel_Sudden07_Apartment",
                "rel_Village00_boomhill",
                "rel_zombieSurvival01_Apartment",
            },
            KnownMapKeys.All);
    }

    /// <summary>The training key is a ninth and is not one of the eight.</summary>
    [Fact]
    public void TrainingIsANinthKey()
    {
        Assert.Equal("rel_trainingRoom01", KnownMapKeys.Training);
        Assert.DoesNotContain(KnownMapKeys.Training, KnownMapKeys.All);
        Assert.True(KnownMapKeys.Set.Contains(KnownMapKeys.Training));
        Assert.Equal(KnownMapKeys.All.Count + 1, KnownMapKeys.Set.Count);
    }

    /// <summary>Membership is exact; keys outside the supported map set are reported as unknown.</summary>
    [Fact]
    public void ContainsIsExact()
    {
        Assert.True(KnownMapKeys.Contains("rel_Sudden07_Apartment"));
        Assert.False(KnownMapKeys.Contains("rel_Sudden07_Apartmen"));
        Assert.False(KnownMapKeys.Contains("wgtFinalZone-rel_Sudden07_Apartment"));
        Assert.False(KnownMapKeys.Contains("Random"));
        Assert.False(KnownMapKeys.Contains("Random_Hot"));
        Assert.False(KnownMapKeys.Contains(string.Empty));
        Assert.False(KnownMapKeys.Contains(null));

        // rel_S315 and rel_Village07_Easy02 are keys a client
        // created rooms with, and neither is in the shipped data - the gap that
        // makes the "not in shipped data" branch worth logging at all.
        Assert.False(KnownMapKeys.Contains("rel_S315"));
        Assert.False(KnownMapKeys.Contains("rel_Village07_Easy02"));
    }
}
