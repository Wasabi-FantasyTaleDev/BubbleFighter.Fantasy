using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks immutable progression maps and build-specific state validation for memory persistence.</summary>
public sealed class PetProgressionsTests
{
    private static readonly PetProgression Legacy = new(1, 10, DateTimeOffset.UnixEpoch.AddDays(1),
        DateTimeOffset.UnixEpoch, new(1, 4, 0, 0, 0), null);
    private static readonly PetProgression Modern = new(0, 19, DateTimeOffset.UnixEpoch.AddDays(2), null, null, null);

    /// <summary>Progression maps compare structurally across JSON round trips and different insertion orders.</summary>
    [Fact]
    public void JsonRoundTripAndInsertionOrderHaveStructuralEquality()
    {
        var first = PetProgressions.Empty.With("kr-812", Legacy).With("kr-1256", Modern);
        var reordered = PetProgressions.Empty.With("kr-1256", Modern).With("kr-812", Legacy with { FirstSkill = new(1, 4, 0, 0, 0) });
        var restored = JsonSerializer.Deserialize<PetProgressions>(JsonSerializer.Serialize(first));
        Assert.Equal(first, reordered);
        Assert.Equal(first.GetHashCode(), reordered.GetHashCode());
        Assert.Equal(first, restored);
        Assert.NotSame(first, restored);
        Assert.NotSame(Legacy.FirstSkill, restored!.Find("kr-812")!.FirstSkill);
        Assert.Equal(2, restored.Count);
    }

    /// <summary>Progression updates and returned records cannot mutate earlier snapshot vectors.</summary>
    [Fact]
    public void UpdatesAndReturnedRecordCopiesCannotMutatePriorSnapshots()
    {
        var original = PetProgressions.Empty.With("kr-812", Legacy);
        var changed = original.With("kr-1256", Modern);
        var copied = changed.Find("kr-812")! with { Experience = 99, FirstSkill = new(2, 8, 0, 0, 0) };
        var replacement = changed.With("kr-812", copied);
        Assert.Null(original.Find("kr-1256"));
        Assert.Equal(Legacy, original.Find("kr-812"));
        Assert.Equal(Legacy, changed.Find("kr-812"));
        Assert.Equal(copied, replacement.Find("kr-812"));
        Assert.Equal(Modern, replacement.Find("kr-1256"));
        Assert.Equal(0, PetProgressions.Empty.Count);
    }

    /// <summary>Progression maps reject duplicate build keys, null state and capacity overflow.</summary>
    [Fact]
    public void DuplicateBuildNullStateAndConfiguredCapacityAreRejected()
    {
        var json = JsonSerializer.Serialize(Legacy);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PetProgressions>("{\"kr-812\":" + json + ",\"kr-812\":" + json + "}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PetProgressions>("{\"kr-812\":null}"));
        Assert.Throws<ArgumentException>(() => PetProgressions.Empty.With("KR-812", Legacy));
        var states = PetProgressions.Empty.With("kr-812", Legacy).With("kr-1256", Modern);
        Assert.Throws<InvalidDataException>(() => states.Validate(1));
        for (var index = states.Count; index < 256; index++) states = states.With("build-" + index, Legacy);
        Assert.Equal(256, states.Count);
        Assert.Throws<InvalidDataException>(() => states.With("overflow", Legacy));
        Assert.Equal(256, states.With("kr-812", Modern).Count);
    }
}
