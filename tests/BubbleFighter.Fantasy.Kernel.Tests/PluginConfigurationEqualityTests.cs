using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies structural equality of independently captured configuration graphs.</summary>
public sealed class PluginConfigurationEqualityTests
{
    /// <summary>Mapping insertion order does not change a captured configuration.</summary>
    [Fact]
    public void MappingKeysCompareIndependentlyOfInsertionOrder()
    {
        var left = Snapshot(("first", 1), ("second", "value"));
        var right = Snapshot(("second", "value"), ("first", 1));

        Assert.True(PluginConfigurationEquality.Equivalent(left, right));
        Assert.False(PluginConfigurationEquality.Equivalent(left, Snapshot(("first", 1))));
    }

    /// <summary>Captured keys and string values retain ordinal case-sensitive identity.</summary>
    [Fact]
    public void MappingKeysAndStringValuesAreOrdinal()
    {
        var left = Capture(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "value" });

        Assert.False(PluginConfigurationEquality.Equivalent(left, Snapshot(("name", "value"))));
        Assert.False(PluginConfigurationEquality.Equivalent(left, Snapshot(("Name", "VALUE"))));
        Assert.True(PluginConfigurationEquality.Equivalent(left, Snapshot(("Name", "value"))));
    }

    /// <summary>Sequence values compare in order and require the same length.</summary>
    [Fact]
    public void SequenceOrderingAndLengthRemainSignificant()
    {
        var left = Snapshot(("items", new object?[] { 1, 2 }));

        Assert.True(PluginConfigurationEquality.Equivalent(left, Snapshot(("items", new List<object?> { 1, 2 }))));
        Assert.False(PluginConfigurationEquality.Equivalent(left, Snapshot(("items", new object?[] { 2, 1 }))));
        Assert.False(PluginConfigurationEquality.Equivalent(left, Snapshot(("items", new object?[] { 1 }))));
    }

    /// <summary>Numeric and textual scalar types are not normalized during comparison.</summary>
    [Fact]
    public void ScalarRuntimeTypesAndValuesMustMatch()
    {
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", 1)), Snapshot(("value", 1L))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", 1)), Snapshot(("value", "1"))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", 'a')), Snapshot(("value", "a"))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", 1f)), Snapshot(("value", 1d))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", true)), Snapshot(("value", false))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", 1)), Snapshot(("value", 2))));
    }

    /// <summary>Absent configuration, null values, empty mappings and empty sequences stay distinct.</summary>
    [Fact]
    public void NullAndEmptyContainersRemainDistinct()
    {
        Assert.True(PluginConfigurationEquality.Equivalent(Capture(null), Capture(null)));
        Assert.True(PluginConfigurationEquality.Equivalent(Snapshot(), Snapshot()));
        Assert.False(PluginConfigurationEquality.Equivalent(Capture(null), Snapshot()));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", null)), Snapshot()));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", null)), Snapshot(("value", new Dictionary<string, object?>()))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", new Dictionary<string, object?>())), Snapshot(("value", Array.Empty<object?>()))));
    }

    /// <summary>Date values with equal ticks but different kinds describe different configuration.</summary>
    [Fact]
    public void DateTimeComparisonPreservesTicksAndKind()
    {
        var utc = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.Equal(utc, unspecified);
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", utc)), Snapshot(("value", unspecified))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", utc)), Snapshot(("value", utc.AddTicks(1)))));
        Assert.True(PluginConfigurationEquality.Equivalent(Snapshot(("value", utc)), Snapshot(("value", new DateTime(utc.Ticks, utc.Kind)))));
    }

    /// <summary>Equal instants do not erase a configured offset or local tick representation.</summary>
    [Fact]
    public void DateTimeOffsetComparisonPreservesTicksAndOffset()
    {
        var value = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.FromHours(8));
        var sameInstant = value.ToOffset(TimeSpan.FromHours(9));
        var sameLocalTime = new DateTimeOffset(value.Ticks, TimeSpan.FromHours(9));

        Assert.Equal(value, sameInstant);
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", value)), Snapshot(("value", sameInstant))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", value)), Snapshot(("value", sameLocalTime))));
        Assert.False(PluginConfigurationEquality.Equivalent(Snapshot(("value", value)), Snapshot(("value", value.AddTicks(1)))));
        Assert.True(PluginConfigurationEquality.Equivalent(Snapshot(("value", value)), Snapshot(("value", new DateTimeOffset(value.Ticks, value.Offset)))));
    }

    /// <summary>All admitted immutable scalar kinds compare by their original typed values.</summary>
    [Fact]
    public void CapturedImmutableScalarKindsCompareEqually()
    {
        object?[] values = [null, "text", true, 'x', (sbyte)-1, (byte)1, (short)-2, (ushort)2, -3, 3u, -4L, 4UL,
            (nint)(-5), (nuint)5, (Int128)(-6), (UInt128)6, 1.25f, 1.25d, 1.25m, float.NaN, double.NaN, TimeSpan.FromTicks(7), Guid.Empty];

        foreach (var value in values)
            Assert.True(PluginConfigurationEquality.Equivalent(Snapshot(("value", value)), Snapshot(("value", value))));
    }

    /// <summary>Nested containers compare structurally across independent snapshot allocations.</summary>
    [Fact]
    public void IndependentNestedGraphsCompareStructurally()
    {
        var left = Snapshot(("nested", new Dictionary<string, object?>
        {
            ["sequence"] = new object?[] { new Dictionary<string, object?> { ["b"] = 2L, ["a"] = null }, "tail" },
            ["enabled"] = true,
        }));
        var right = Snapshot(("nested", new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["sequence"] = new List<object?> { new Dictionary<string, object?> { ["a"] = null, ["b"] = 2L }, "tail" },
        }));

        Assert.True(PluginConfigurationEquality.Equivalent(left, right));
        Assert.True(PluginConfigurationEquality.Equivalent(right, left));
    }

    private static IReadOnlyDictionary<string, object?> Snapshot(params (string Key, object? Value)[] entries) =>
        Capture(entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal))!;

    private static IReadOnlyDictionary<string, object?>? Capture(IReadOnlyDictionary<string, object?>? config) =>
        PluginRowSnapshot.Capture(new PluginRow { Id = "equality", Plugin = "test.equality", Source = "test", Config = config }).Config;
}
