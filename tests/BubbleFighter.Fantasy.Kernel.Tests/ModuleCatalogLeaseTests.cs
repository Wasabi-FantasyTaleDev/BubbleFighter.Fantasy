using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies bounded source catalog pins and coordination-key retirement without claiming physical code reclamation.</summary>
public sealed class ModuleCatalogLeaseTests
{
    /// <summary>A source releases only its own pins and can reuse capacity after every holder is gone.</summary>
    [Fact]
    public void CatalogBatchLeaseReleasesExactlyItsPins()
    {
        var bootstrap = Definition("bootstrap", Code());
        var ledger = new PluginModuleOwnership([bootstrap], 0, 3);
        var shared = Code();
        var source = ledger.RetainCatalogBatch([Definition("a", shared), Definition("b", shared)]);
        Assert.Equal(3, ledger.Capacity);
        Assert.Equal(2, ledger.Snapshot().Count);
        Assert.Contains(ledger.Snapshot(), row => row.CatalogPins == 2);
        Assert.Throws<ConfigException>(() => ledger.RetainCatalogBatch([Definition("overflow", shared)]));
        source.Dispose();
        source.Dispose();
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).CatalogPins);
        using var replacement = ledger.RetainCatalogBatch([Definition("next", Code())]);
        Assert.Equal(2, ledger.Snapshot().Count);
    }

    /// <summary>Revoking source pins cannot remove an activation, retained failure or independently committed cache.</summary>
    [Fact]
    public void RetiredCatalogKeepsActualCodeHolders()
    {
        var ledger = new PluginModuleOwnership([], 1, 1);
        var definition = Definition("dynamic", Code());
        var source = ledger.RetainCatalogBatch([definition]);
        var active = ledger.Reserve("entry");
        active.Bind(definition);
        var cached = active.RetainDefinition();
        active.MarkRetainedFailure();
        source.Dispose();
        var retained = Assert.Single(ledger.Snapshot());
        Assert.Equal(0, retained.CatalogPins);
        Assert.Equal(1, retained.Activations);
        Assert.Equal(1, retained.DefinitionCaches);
        Assert.Equal(1, retained.RetainedFailures);
        active.Dispose();
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).DefinitionCaches);
        cached.Dispose();
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>A late provenance conflict rejects the entire batch without promoting earlier metadata or opening revisions.</summary>
    [Fact]
    public void ConflictingBatchDoesNotPromoteOrPinAPrefix()
    {
        var firstCode = Code();
        var secondCode = Code();
        var ledger = new PluginModuleOwnership([Definition("first", firstCode), Definition("second", secondCode)], 0, 5);
        var before = ledger.Snapshot().ToArray();
        Assert.Throws<ConfigException>(() => ledger.RetainCatalogBatch([
            Definition("promote", firstCode with { Lifetime = PluginCodeLifetime.Collectible, LifetimeAnchor = new object() }),
            Definition("new", Code()),
            Definition("conflict", secondCode with { Attribution = "different" }),
        ]));
        Assert.Equal(before, ledger.Snapshot().ToArray());
        using var replacement = ledger.RetainCatalogBatch([Definition("valid", Code())]);
        Assert.Equal(3, ledger.Snapshot().Count);
    }

    /// <summary>Conflicting provenance within one new batch is detected before even its first revision becomes visible.</summary>
    [Fact]
    public void IntraBatchConflictLeavesNoRecords()
    {
        var code = Code();
        var ledger = new PluginModuleOwnership([], 0, 2);
        Assert.Throws<ConfigException>(() => ledger.RetainCatalogBatch([
            Definition("a", code with { LifetimeAnchor = new object() }),
            Definition("b", code with { LifetimeAnchor = new object() }),
        ]));
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>Pending execution reservations still consume combined capacity during catalog admission.</summary>
    [Fact]
    public void CatalogAdmissionChargesPendingAcquisitionsAndOldCaches()
    {
        var ledger = new PluginModuleOwnership([], 1, 2);
        var old = ledger.Reserve("entry");
        old.Bind(Definition("old", Code()));
        using var oldCache = old.RetainDefinition();
        old.Dispose();
        var current = ledger.Reserve("entry");
        current.Bind(Definition("current", Code()));
        using var currentCache = current.RetainDefinition();
        current.Dispose();
        using var pending = ledger.Reserve("entry");
        Assert.Equal(1, ledger.PendingCount);
        Assert.Throws<ConfigException>(() => ledger.RetainCatalogBatch([Definition("a", Code()), Definition("b", Code())]));
        Assert.Equal(2, ledger.Snapshot().Count);
        Assert.Equal(1, ledger.PendingCount);
    }

    /// <summary>Duplicate definition identity and undersized catalog bounds fail before a source can retain ownership.</summary>
    [Fact]
    public void InvalidBatchAndCatalogBoundsAreRejected()
    {
        var definition = Definition("same", Code());
        Assert.Throws<ConfigException>(() => new PluginModuleOwnership([definition], 0, 0));
        Assert.Throws<ConfigException>(() => new PluginModuleOwnership([], 0, -1));
        var ledger = new PluginModuleOwnership([], 0, 2);
        Assert.Throws<ConfigException>(() => ledger.RetainCatalogBatch([definition, definition]));
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>Final source release frees its keys and a later source receives a fresh coordination incarnation.</summary>
    [Fact]
    public void RecreatedSourceKeyStartsAFreshGeneration()
    {
        var ledger = new PluginModules(2);
        var source = ledger.RetainSourceKeys(["key", "key"]);
        Assert.True(ledger.TryBeginReload("key"));
        Assert.True(ledger.CompleteReload("key", true));
        Assert.Equal(2, Assert.Single(ledger.Snapshot()).Generation);
        source.Dispose();
        source.Dispose();
        Assert.Empty(ledger.Snapshot());
        using var next = ledger.RetainSourceKeys(["key"]);
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).Generation);
        source.Dispose();
        Assert.Single(ledger.Snapshot());
    }

    /// <summary>Each real lease source independently prevents coordination-key reclamation after source withdrawal.</summary>
    [Theory]
    [InlineData("scheduled")]
    [InlineData("activation")]
    [InlineData("cache")]
    [InlineData("failure")]
    public void SourceReleaseWaitsForEveryCounter(string name)
    {
        var kind = name switch
        {
            "scheduled" => LeaseKind.Scheduled, "activation" => LeaseKind.Activation,
            "cache" => LeaseKind.DefinitionCache, _ => LeaseKind.RetainedFailure,
        };
        var ledger = new PluginModules(1);
        var source = ledger.RetainSourceKeys(["key"]);
        ledger.AddLease("key", kind);
        source.Dispose();
        Assert.Single(ledger.Snapshot());
        Assert.Throws<ConfigException>(() => ledger.RetainSourceKeys(["new"]));
        ledger.RemoveLease("key", kind);
        Assert.Empty(ledger.Snapshot());
        using var next = ledger.RetainSourceKeys(["new"]);
        Assert.Single(ledger.Snapshot());
    }

    /// <summary>A retained failure marker and an in-progress refresh both outlive source withdrawal.</summary>
    [Fact]
    public void SourceReleaseWaitsForFailureMarkerAndActualReloadCompletion()
    {
        var ledger = new PluginModules(1);
        var source = ledger.RetainSourceKeys(["key"]);
        Assert.True(ledger.TryBeginReload("key"));
        ledger.MarkRetainedFailure("key", true);
        source.Dispose();
        Assert.Single(ledger.Snapshot());
        ledger.MarkRetainedFailure("key", false);
        Assert.Single(ledger.Snapshot());
        Assert.False(ledger.CompleteReload("key", false));
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>Source-key admission validates the complete batch and never increments a prefix on refusal.</summary>
    [Fact]
    public void FailedKeyBatchDoesNotLeakPrefixPins()
    {
        var ledger = new PluginModules(2);
        var source = ledger.RetainSourceKeys(["existing"]);
        Assert.Throws<ConfigException>(() => ledger.RetainSourceKeys(["existing", "new", "overflow"]));
        Assert.Throws<ConfigException>(() => ledger.RetainSourceKeys(["existing", " " ]));
        source.Dispose();
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>Legacy opened keys remain stable when a temporary source happens to reference them.</summary>
    [Fact]
    public void SourceLeaseDoesNotChangeLegacyRecordLifetime()
    {
        var ledger = new PluginModules(1);
        ledger.AddLease("legacy", LeaseKind.Activation);
        using (ledger.RetainSourceKeys(["legacy"])) { }
        ledger.RemoveLease("legacy", LeaseKind.Activation);
        Assert.Single(ledger.Snapshot());
    }

    /// <summary>Two independent sources sharing a key retain independent pins even when one is withdrawn twice.</summary>
    [Fact]
    public void SharedKeyRemainsUntilEverySourceReleases()
    {
        var ledger = new PluginModules(1);
        var first = ledger.RetainSourceKeys(["shared"]);
        var second = ledger.RetainSourceKeys(["shared"]);
        first.Dispose();
        first.Dispose();
        Assert.Single(ledger.Snapshot());
        second.Dispose();
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>A completed scheduled batch releases the last source-managed records only after every reservation settles.</summary>
    [Fact]
    public void RetiredSourceWaitsForScheduledBatchRelease()
    {
        var ledger = new PluginModules(2);
        var source = ledger.RetainSourceKeys(["a", "b"]);
        var keys = new HashSet<string>(["a", "b"], StringComparer.Ordinal);
        ledger.AddScheduledLeases(keys);
        source.Dispose();
        Assert.Equal(2, ledger.Snapshot().Count);
        ledger.RemoveScheduledLeases(keys);
        Assert.Empty(ledger.Snapshot());
    }

    private static PluginModuleCode Code() => new("module", new object(), "test");
    private static PluginDescriptor Definition(string id, PluginModuleCode code) => new(new PluginAttribute(id),
        new Factory(code), "catalog-lease-test", "1.0.0", new Dictionary<string, string>());

    private sealed class Factory(PluginModuleCode code) : IPluginExecutionFactory, IPluginModuleSource
    {
        public string? CoordinationKey => "shared";
        public PluginModuleCode ModuleCode => code;
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Ownership ledger tests must not invoke an execution factory.");
    }
}
