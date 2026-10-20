using System.Collections;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies fixed startup capacity and retained ownership accounting across membership changes.</summary>
public sealed class PluginRuntimeBudgetTests
{
    /// <summary>Zero reserve defaults preserve the original per-row metadata allowance exactly.</summary>
    [Fact]
    public void DefaultsMatchLegacyCapacity()
    {
        PluginRow[] rows = [Row("first"), Row("second")];
        var budget = PluginRuntimeBudget.Capture(rows);

        Assert.Equal(2, budget.EntryCapacity);
        Assert.Equal(8197, budget.ServiceMetadataCapacity);
        budget.Validate(rows, [], []);
        Assert.Throws<ConfigException>(() => budget.Validate([.. rows, Row("extra")], [], []));
    }

    /// <summary>An empty bootstrap has only the three host metadata slots and no implicit entry headroom.</summary>
    [Fact]
    public void EmptyBootstrapHasNoEntryHeadroom()
    {
        var budget = PluginRuntimeBudget.Capture([]);

        Assert.Equal(0, budget.EntryCapacity);
        Assert.Equal(3, budget.ServiceMetadataCapacity);
        budget.Validate([], [], []);
        Assert.Throws<ConfigException>(() => budget.Validate([Row("new", effects: 1)], [], []));
    }

    /// <summary>Explicit entry and metadata reserves admit a candidate exactly at both fixed limits.</summary>
    [Fact]
    public void ExplicitHeadroomAdmitsTheExactBoundary()
    {
        var donor = Row("donor", effects: 2, entries: 2, metadata: 6);
        var budget = PluginRuntimeBudget.Capture([donor]);

        Assert.Equal(3, budget.EntryCapacity);
        Assert.Equal(12, budget.ServiceMetadataCapacity);
        budget.Validate([donor, Row("second", 2), Row("third", 2)], [], []);
        Assert.Contains("service metadata capacity", Assert.Throws<ConfigException>(() =>
            budget.Validate([donor, Row("second", 2), Row("third", 3)], [], [])).Message);
    }

    /// <summary>Retiring entries continue consuming entry slots even with abundant service metadata headroom.</summary>
    [Fact]
    public void RetiringRowsRemainChargedToEntryCapacity()
    {
        var donor = Row("donor", effects: 1, entries: 1, metadata: 100);
        var budget = PluginRuntimeBudget.Capture([donor]);

        Assert.Contains("entry capacity", Assert.Throws<ConfigException>(() =>
            budget.Validate([donor, Row("new", 1)], [Row("retired", 1)], [])).Message);
    }

    /// <summary>Retiring metadata is charged without counting its startup grant for a second time.</summary>
    [Fact]
    public void RetiringMetadataConsumesTheFixedPool()
    {
        var donor = Row("donor", effects: 1, entries: 1, metadata: 2);
        var budget = PluginRuntimeBudget.Capture([donor]);

        Assert.Equal(7, budget.ServiceMetadataCapacity);
        budget.Validate([Row("new", 1)], [donor], []);
        Assert.Contains("service metadata capacity", Assert.Throws<ConfigException>(() =>
            budget.Validate([Row("new", 2)], [donor], [])).Message);
    }

    /// <summary>Reducing the desired effect limit cannot fund another entry while the larger activation is owned.</summary>
    [Fact]
    public void OwnedLargerRevisionPreventsPrematureCapacityReuse()
    {
        var old = Row("entry", effects: 10, entries: 1);
        var smaller = Row("entry", effects: 1, entries: 1);
        var added = Row("new", effects: 8);
        var budget = PluginRuntimeBudget.Capture([old]);

        budget.Validate([smaller, added], [], []);
        var error = Assert.Throws<ConfigException>(() => budget.Validate([smaller, added], [], [old]));

        Assert.Contains("entry", error.Message);
        Assert.Contains("service metadata capacity", error.Message);
    }

    /// <summary>The released owner's exact excess becomes available without changing the fixed host pool.</summary>
    [Fact]
    public void ReleasingOwnedRevisionFreesOnlyItsExactExcess()
    {
        var old = Row("entry", effects: 10, entries: 1);
        var smaller = Row("entry", effects: 1, entries: 1);
        var budget = PluginRuntimeBudget.Capture([old]);

        Assert.Throws<ConfigException>(() => budget.Validate([smaller, Row("new", 8)], [], [old]));
        budget.Validate([smaller, Row("new", 8)], [], []);
        Assert.Throws<ConfigException>(() => budget.Validate([smaller, Row("new", 9)], [], []));
        Assert.Equal(2, budget.EntryCapacity);
        Assert.Equal(14, budget.ServiceMetadataCapacity);
    }

    /// <summary>Repeated ownership snapshots for one identity charge one slot and its maximum effect limit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateOwnedSnapshotsChargeOnlyTheirPeak(bool reverse)
    {
        var old = Row("entry", effects: 10);
        var smaller = Row("entry", effects: 1);
        var budget = PluginRuntimeBudget.Capture([old]);
        PluginRow[] owned = [smaller, Row("entry", 5), old, old, smaller];

        budget.Validate([smaller], [], reverse ? owned.Reverse().ToArray() : owned);
        Assert.Equal(1, budget.EntryCapacity);
        Assert.Equal(14, budget.ServiceMetadataCapacity);
    }

    /// <summary>Owned identities require a current or retiring declaration even when they were present at startup.</summary>
    [Theory]
    [InlineData("removed")]
    [InlineData("never-admitted")]
    public void OwnedIdentityCannotBeMissingFromTheAdmittedUnion(string id)
    {
        var active = Row("active", effects: 1);
        var budget = PluginRuntimeBudget.Capture([active, Row("removed", 1)]);
        var error = Assert.Throws<ConfigException>(() => budget.Validate([active], [], [Row(id, 1)]));

        Assert.Contains(id, error.Message);
        Assert.Contains("candidate or retiring", error.Message);
    }

    /// <summary>A larger desired limit is charged once rather than added to the smaller owned revision.</summary>
    [Fact]
    public void GrowingTargetChargesTheDeclaredPeak()
    {
        var old = Row("entry", effects: 2, metadata: 3);
        var budget = PluginRuntimeBudget.Capture([old]);

        budget.Validate([Row("entry", effects: 5, metadata: 3)], [], [old]);
        Assert.Throws<ConfigException>(() => budget.Validate([Row("entry", effects: 6, metadata: 3)], [], [old]));
        Assert.Equal(9, budget.ServiceMetadataCapacity);
    }

    /// <summary>A reduced retiring declaration cannot hide the larger capacity of its still-owned activation.</summary>
    [Fact]
    public void RetiringDeclarationRetainsItsLargerOwnedCapacity()
    {
        var old = Row("retiring", effects: 10, entries: 1);
        var smaller = Row("retiring", effects: 1, entries: 1);
        var added = Row("new", effects: 8);
        var budget = PluginRuntimeBudget.Capture([old]);

        Assert.Throws<ConfigException>(() => budget.Validate([added], [smaller], [old]));
        budget.Validate([added], [smaller], []);
    }

    /// <summary>Owned collection counts bound enumeration even when duplicate identities would otherwise be valid.</summary>
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void OwnedEnumerationMustMatchItsReportedCount(int reported, int actual)
    {
        var row = Row("entry", effects: 1);
        var budget = PluginRuntimeBudget.Capture([row]);
        var owned = new ReportedRows(Enumerable.Repeat(row, actual).ToArray(), reported);

        Assert.Contains("owned", Assert.Throws<ConfigException>(() => budget.Validate([row], [], owned)).Message);
    }

    /// <summary>A candidate cannot reuse an identity whose prior row is still retiring.</summary>
    [Fact]
    public void RetiringIdentityCannotBeReused()
    {
        var donor = Row("donor", effects: 1, entries: 1, metadata: 2);
        var budget = PluginRuntimeBudget.Capture([donor]);
        var error = Assert.Throws<ConfigException>(() => budget.Validate([donor], [donor], []));

        Assert.Contains("donor", error.Message);
        Assert.Contains("still retiring", error.Message);
    }

    /// <summary>Only bootstrap identities may carry their original nonzero capacity grants.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void NewEntriesCannotMintStartupGrants(int entries, int metadata)
    {
        var budget = PluginRuntimeBudget.Capture([Row("donor", effects: 1, entries: 1, metadata: 20)]);
        var error = Assert.Throws<ConfigException>(() => budget.Validate([Row("new", 1, entries, metadata)], [], []));

        Assert.Contains("new", error.Message);
        Assert.Contains("startup grants", error.Message);
    }

    /// <summary>Neither increasing nor decreasing an existing identity's grant changes its startup contract.</summary>
    [Theory]
    [InlineData(2, 20)]
    [InlineData(0, 20)]
    [InlineData(1, 21)]
    [InlineData(1, 19)]
    public void ExistingStartupGrantsCannotChange(int entries, int metadata)
    {
        var budget = PluginRuntimeBudget.Capture([Row("donor", effects: 1, entries: 1, metadata: 20)]);

        Assert.Contains("startup grants", Assert.Throws<ConfigException>(() =>
            budget.Validate([Row("donor", 1, entries, metadata)], [], [])).Message);
    }

    /// <summary>Removing and later restoring a donor neither shrinks nor grows the shared capacity.</summary>
    [Fact]
    public void DonorRemovalAndReadditionPreserveTheCapturedPool()
    {
        var donor = Row("donor", effects: 2, entries: 1, metadata: 3);
        var initial = new List<PluginRow> { donor };
        var budget = PluginRuntimeBudget.Capture(initial);
        initial.Clear();

        budget.Validate([Row("first", 2), Row("second", 2)], [], []);
        budget.Validate([donor, Row("first", 2)], [], []);
        Assert.Equal(2, budget.EntryCapacity);
        Assert.Equal(9, budget.ServiceMetadataCapacity);
        Assert.Throws<ConfigException>(() => budget.Validate([donor, Row("first", 2), Row("second", 2)], [], []));
    }

    /// <summary>Aggregate capacity overflow is a configuration error even when every individual grant is valid.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AggregateCapacityOverflowFailsAsConfiguration(bool entries)
    {
        var rows = Enumerable.Range(0, entries ? 2148 : 215)
            .Select(index => Row($"entry-{index}", effects: 1, entries: entries ? 1000000 : 0, metadata: entries ? 0 : 10000000)).ToArray();
        var error = Assert.Throws<ConfigException>(() => PluginRuntimeBudget.Capture(rows));

        Assert.Contains(entries ? "entry capacity" : "service metadata capacity", error.Message);
        Assert.Contains("supported range", error.Message);
    }

    /// <summary>Capture and validation reject invalid lifecycle fields on current, retiring and owned rows.</summary>
    [Theory]
    [InlineData("effect")]
    [InlineData("entry-low")]
    [InlineData("entry-high")]
    [InlineData("metadata-low")]
    [InlineData("metadata-high")]
    [InlineData("null")]
    public void EveryRowLifecycleIsValidated(string invalid)
    {
        var lifecycle = invalid switch
        {
            "effect" => new PluginLifecycleOptions { EffectCapacity = 0 },
            "entry-low" => new PluginLifecycleOptions { EntryReserve = -1 },
            "entry-high" => new PluginLifecycleOptions { EntryReserve = 1000001 },
            "metadata-low" => new PluginLifecycleOptions { ServiceMetadataReserve = -1 },
            "metadata-high" => new PluginLifecycleOptions { ServiceMetadataReserve = 10000001 },
            _ => null,
        };
        var row = Row("entry") with { Lifecycle = lifecycle! };
        var budget = PluginRuntimeBudget.Capture([Row("entry")]);

        Assert.Throws<ConfigException>(() => PluginRuntimeBudget.Capture([row]));
        Assert.Throws<ConfigException>(() => budget.Validate([row], [], []));
        Assert.Throws<ConfigException>(() => budget.Validate([], [row], []));
        Assert.Throws<ConfigException>(() => budget.Validate([Row("entry")], [], [row]));
    }

    /// <summary>Current and retiring declarations remain disjoint even though owned snapshots may repeat identities.</summary>
    [Fact]
    public void DuplicateIdentitiesAreRejected()
    {
        var row = Row("same", effects: 1, entries: 1, metadata: 2);
        var budget = PluginRuntimeBudget.Capture([row]);

        Assert.Throws<ConfigException>(() => PluginRuntimeBudget.Capture([row, row]));
        Assert.Throws<ConfigException>(() => budget.Validate([row, row], [], []));
        Assert.Throws<ConfigException>(() => budget.Validate([], [row, row], []));
    }

    /// <summary>Null rows and missing identities fail with configuration diagnostics instead of null dereferences.</summary>
    [Fact]
    public void InvalidRowObjectsAreRejected()
    {
        var budget = PluginRuntimeBudget.Capture([Row("entry")]);
        foreach (var row in new PluginRow[] { null!, Row(""), Row(" "), Row("entry") with { Id = null! } })
        {
            Assert.Throws<ConfigException>(() => PluginRuntimeBudget.Capture([row]));
            Assert.Throws<ConfigException>(() => budget.Validate([row], [], []));
            Assert.Throws<ConfigException>(() => budget.Validate([], [row], []));
            Assert.Throws<ConfigException>(() => budget.Validate([Row("entry")], [], [row]));
        }
    }

    /// <summary>Concurrent owned raises are summed before admission so the pool cannot fund one of them by ignoring the other.</summary>
    [Fact]
    public void SimultaneousOwnedRaisesMustFitThePoolTogether()
    {
        var donor = Row("donor", effects: 4);
        var first = Row("first", effects: 2, metadata: 2);
        var second = Row("second", effects: 2, metadata: 2);
        var budget = PluginRuntimeBudget.Capture([donor, first, second]);

        Assert.Equal(18, budget.ServiceMetadataCapacity);
        budget.Validate([donor, first, second], [], [Row("first", 6)]);
        budget.Validate([donor, first, second], [], [Row("second", 6)]);
        Assert.Contains("service metadata capacity", Assert.Throws<ConfigException>(() =>
            budget.Validate([donor, first, second], [], [Row("first", 6), Row("second", 6)])).Message);
    }

    /// <summary>Candidate and retiring enumerations are bounded by their reported counts just like owned ones.</summary>
    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 1, 0)]
    public void CandidateAndRetiringEnumerationMustMatchTheirReportedCount(bool retiring, int reported, int actual)
    {
        var row = Row("entry", effects: 1);
        var budget = PluginRuntimeBudget.Capture([row]);
        var error = Assert.Throws<ConfigException>(() =>
        {
            if (retiring) budget.Validate([], new ReportedRows(Enumerable.Repeat(row, actual).ToArray(), reported), []);
            else budget.Validate(new ReportedRowList(Enumerable.Repeat(row, actual).ToArray(), reported), [], []);
        });

        Assert.Contains(retiring ? "retiring" : "candidate", error.Message);
    }

    /// <summary>An in-place downsizing admits the new row only once the larger old generation has drained.</summary>
    [Fact]
    public void InPlaceDownsizingNeedsTheOldGenerationToDrain()
    {
        var old = Row("entry", effects: 10, entries: 1);
        var smaller = Row("entry", effects: 5, entries: 1);
        var added = Row("new", effects: 3);
        var budget = PluginRuntimeBudget.Capture([old]);

        Assert.Equal(14, budget.ServiceMetadataCapacity);
        Assert.Contains("declared and owned entry capacities", Assert.Throws<ConfigException>(() =>
            budget.Validate([smaller, added], [], [old])).Message);
        budget.Validate([smaller, added], [], []);
        budget.Validate([smaller, added], [], [added]);
    }

    private sealed class ReportedRows(IReadOnlyList<PluginRow> rows, int reported) : IReadOnlyCollection<PluginRow>
    {
        /// <summary>Reports the deliberately supplied count for ownership enumeration validation.</summary>
        public int Count => reported;

        /// <summary>Enumerates actual snapshots independently of the reported count.</summary>
        public IEnumerator<PluginRow> GetEnumerator() => rows.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ReportedRowList(IReadOnlyList<PluginRow> rows, int reported) : IReadOnlyList<PluginRow>
    {
        /// <summary>Reports the deliberately supplied count for candidate enumeration validation.</summary>
        public int Count => reported;

        /// <summary>Returns the actual snapshot at its real position, independent of the reported count.</summary>
        public PluginRow this[int index] => rows[index];

        /// <summary>Enumerates actual snapshots independently of the reported count.</summary>
        public IEnumerator<PluginRow> GetEnumerator() => rows.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static PluginRow Row(string id, int effects = 4096, int entries = 0, int metadata = 0) => new()
    {
        Id = id, Plugin = "budget-test", Source = "test",
        Lifecycle = new() { EffectCapacity = effects, EntryReserve = entries, ServiceMetadataReserve = metadata },
    };
}
