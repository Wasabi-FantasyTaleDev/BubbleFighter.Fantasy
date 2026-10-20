using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies real revision ownership without inferring code identity from coordination or value equality.</summary>
public sealed class PluginModuleOwnershipTests
{
    /// <summary>Code identity never calls a backend object's Equals or GetHashCode implementations.</summary>
    [Fact]
    public void CodeIdentityUsesReferencesInsteadOfBackendEquality()
    {
        var ledger = new PluginModuleOwnership([], 2);
        using var first = ledger.Reserve("first");
        using var second = ledger.Reserve("second");
        first.Bind(Definition(new("module", new HostileIdentity(), "test")));
        second.Bind(Definition(new("module", new HostileIdentity(), "test")));
        Assert.NotEqual(first.RevisionId, second.RevisionId);
        Assert.Equal(2, ledger.Snapshot().Count);
        Assert.All(ledger.Snapshot(), revision => Assert.Equal(1, revision.Activations));
    }

    /// <summary>Module identifiers compare ordinally even when two sources use the same opaque object.</summary>
    [Fact]
    public void ModuleNamesRemainOrdinalAndIndependentOfCoordination()
    {
        var identity = new object();
        var ledger = new PluginModuleOwnership([], 2);
        using var first = ledger.Reserve("first");
        using var second = ledger.Reserve("second");
        first.Bind(Definition(new("Module", identity, "test")));
        second.Bind(Definition(new("module", identity, "test")));
        Assert.NotEqual(first.RevisionId, second.RevisionId);
        Assert.Equal(["Module", "module"], ledger.Snapshot().Select(revision => revision.ModuleId));
    }

    /// <summary>Catalog references remain explicit after executor and entry-cache holders have all released.</summary>
    [Fact]
    public void SharedCatalogRevisionRemainsPinnedAfterEntryRelease()
    {
        var code = new PluginModuleCode("module", new object(), "test");
        var first = Definition(code, "first-definition");
        var second = Definition(code, "second-definition");
        var ledger = new PluginModuleOwnership([first, second], 1);
        Assert.Equal(4, ledger.Capacity);
        var ticket = ledger.Reserve("entry");
        ticket.Bind(first);
        var cache = ticket.RetainDefinition();
        var before = Assert.Single(ledger.Snapshot());
        Assert.Equal(2, before.CatalogPins);
        Assert.Equal(1, before.Activations);
        Assert.Equal(1, before.DefinitionCaches);
        ticket.Dispose();
        ticket.Dispose();
        cache.Dispose();
        cache.Dispose();
        var after = Assert.Single(ledger.Snapshot());
        Assert.Equal(before.RevisionId, after.RevisionId);
        Assert.Equal(2, after.CatalogPins);
        Assert.Equal(0, after.Activations);
        Assert.Equal(0, after.DefinitionCaches);
        Assert.Equal(1, before.Activations);
    }

    /// <summary>A cache of an older revision and a freshly acquired revision coexist until their own holders release.</summary>
    [Fact]
    public void CacheAndExecutorKeepTheirOriginalRevisionAcrossReplacement()
    {
        var ledger = new PluginModuleOwnership([], 1);
        var old = ledger.Reserve("entry");
        old.Bind(Definition(new("module", new object(), "test")));
        var oldRevision = old.RevisionId;
        var oldCache = old.RetainDefinition();
        old.Dispose();
        var current = ledger.Reserve("entry");
        Assert.Equal(1, ledger.PendingCount);
        current.Bind(Definition(new("module", new object(), "test")));
        var newCache = current.RetainDefinition();
        Assert.NotEqual(oldRevision, current.RevisionId);
        Assert.Equal(oldRevision, old.RevisionId);
        Assert.Equal(0, ledger.PendingCount);
        var revisions = ledger.Snapshot();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(0, revisions.Single(revision => revision.RevisionId == oldRevision).Activations);
        Assert.Equal(1, revisions.Single(revision => revision.RevisionId == oldRevision).DefinitionCaches);
        oldCache.Dispose();
        Assert.Equal(current.RevisionId, Assert.Single(ledger.Snapshot()).RevisionId);
        current.Dispose();
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).DefinitionCaches);
        newCache.Dispose();
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>Failure flags overlay pending or active ownership rather than allocating another reservation.</summary>
    [Fact]
    public void RetainedFailureMovesWithTheSamePendingReservation()
    {
        var ledger = new PluginModuleOwnership([], 1);
        var ticket = ledger.Reserve("entry");
        Assert.Null(ticket.RevisionId);
        Assert.Throws<InvalidOperationException>(() => ledger.Reserve("entry"));
        Assert.Throws<ConfigException>(() => ledger.Reserve("other"));
        ticket.MarkRetainedFailure();
        ticket.MarkRetainedFailure();
        Assert.Equal(1, ledger.PendingCount);
        Assert.Equal(1, ledger.PendingRetainedCount);
        Assert.Throws<ArgumentNullException>(() => ticket.Bind(null!));
        Assert.Throws<InvalidOperationException>(() => ticket.RetainDefinition());
        Assert.Equal(1, ledger.PendingCount);
        ticket.Bind(Definition(new("module", new object(), "test")));
        ticket.MarkRetainedFailure();
        Assert.Equal(0, ledger.PendingCount);
        Assert.Equal(0, ledger.PendingRetainedCount);
        var revision = Assert.Single(ledger.Snapshot());
        Assert.Equal(1, revision.Activations);
        Assert.Equal(1, revision.RetainedFailures);
        ticket.Dispose();
        Assert.True(ticket.IsReleased);
        Assert.Empty(ledger.Snapshot());
        using var replacement = ledger.Reserve("entry");
        Assert.Equal(1, ledger.PendingCount);
        ticket.Dispose();
        Assert.Equal(1, ledger.PendingCount);
    }

    /// <summary>Canceling an unbound retained acquisition releases its pending allocation exactly once.</summary>
    [Fact]
    public void PendingReleaseAndPostReleaseOperationsCannotReviveOwnership()
    {
        var ledger = new PluginModuleOwnership([], 1);
        var ticket = ledger.Reserve("entry");
        ticket.MarkRetainedFailure();
        ticket.Dispose();
        ticket.Dispose();
        Assert.Equal(0, ledger.PendingCount);
        Assert.Equal(0, ledger.PendingRetainedCount);
        Assert.Throws<ObjectDisposedException>(() => ticket.Bind(Definition(new("module", new object(), "test"))));
        Assert.Throws<ObjectDisposedException>(() => ticket.RetainDefinition());
        Assert.Throws<ObjectDisposedException>(() => ticket.MarkRetainedFailure());
        Assert.Empty(ledger.Snapshot());
    }

    /// <summary>A rejected provenance conflict leaves the original reservation pending and the catalog unchanged.</summary>
    [Fact]
    public void InvalidBindingDoesNotConsumePendingOwnership()
    {
        var identity = new object();
        var initial = Definition(new("module", identity, "original"));
        var ledger = new PluginModuleOwnership([initial], 1);
        using var ticket = ledger.Reserve("entry");
        var before = Assert.Single(ledger.Snapshot());
        Assert.Throws<ConfigException>(() => ticket.Bind(Definition(new("module", identity, "conflicting"))));
        Assert.Null(ticket.RevisionId);
        Assert.Equal(1, ledger.PendingCount);
        Assert.Equal(before, Assert.Single(ledger.Snapshot()));
        ticket.Bind(initial);
        Assert.Throws<InvalidOperationException>(() => ticket.Bind(initial));
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).Activations);
    }

    /// <summary>A conflicting nonnull lifetime anchor rejects binding without consuming the pending reservation or changing existing holders.</summary>
    [Fact]
    public void ConflictingLifetimeAnchorLeavesAcquisitionPending()
    {
        var identity = new object();
        var anchor = new HostileIdentity();
        var initial = Definition(new("module", identity, "test", anchor));
        var ledger = new PluginModuleOwnership([initial], 1);
        using var ticket = ledger.Reserve("entry");
        var before = Assert.Single(ledger.Snapshot());
        var error = Assert.Throws<ConfigException>(() =>
            ticket.Bind(Definition(new("module", identity, "test", new HostileIdentity()))));
        Assert.Contains("lifetime anchor", error.Message);
        Assert.Null(ticket.RevisionId);
        Assert.False(ticket.IsReleased);
        Assert.Equal(1, ledger.PendingCount);
        Assert.Equal(before, Assert.Single(ledger.Snapshot()));

        ticket.Bind(Definition(new("module", identity, "test", anchor)));
        Assert.Equal(before.RevisionId, ticket.RevisionId);
        var cache = ticket.RetainDefinition();
        ticket.Dispose();
        ticket.Dispose();
        Assert.True(ticket.IsReleased);
        Assert.Equal(0, ledger.PendingCount);
        Assert.Equal(0, Assert.Single(ledger.Snapshot()).Activations);
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).DefinitionCaches);
        cache.Dispose();
        cache.Dispose();
        Assert.Equal(before, Assert.Single(ledger.Snapshot()));
    }

    /// <summary>An initially unknown lifetime may acquire its first anchor, and later unknown data cannot erase that retained association.</summary>
    [Fact]
    public void NullLifetimeAnchorPromotesOnceAndLaterNullCannotEraseIt()
    {
        var identity = new object();
        var anchor = new object();
        var unknown = Definition(new("module", identity, "test"));
        var known = Definition(new("module", identity, "test", anchor));
        var ledger = new PluginModuleOwnership([unknown], 1);
        var baseline = Assert.Single(ledger.Snapshot());
        var promoted = ledger.Reserve("entry");
        promoted.Bind(known);
        Assert.Equal(baseline.RevisionId, promoted.RevisionId);
        promoted.Dispose();

        var omitted = ledger.Reserve("entry");
        omitted.Bind(unknown);
        Assert.Equal(baseline.RevisionId, omitted.RevisionId);
        omitted.Dispose();

        using var next = ledger.Reserve("entry");
        Assert.Throws<ConfigException>(() => next.Bind(Definition(new("module", identity, "test", new object()))));
        Assert.Null(next.RevisionId);
        Assert.Equal(1, ledger.PendingCount);
        Assert.Equal(baseline, Assert.Single(ledger.Snapshot()));
        next.Bind(known);
        Assert.Equal(baseline.RevisionId, next.RevisionId);
        next.Dispose();
        next.Dispose();
        Assert.Equal(0, ledger.PendingCount);
        Assert.Equal(baseline, Assert.Single(ledger.Snapshot()));
    }

    /// <summary>Known lifetime drift fails atomically before an incoming anchor or ownership counter can be committed.</summary>
    [Theory]
    [InlineData(PluginCodeLifetime.ProcessLifetime, PluginCodeLifetime.Collectible)]
    [InlineData(PluginCodeLifetime.Collectible, PluginCodeLifetime.ProcessLifetime)]
    public void LifetimeDriftLeavesOriginalRevisionAndPendingAcquisitionUnchanged(PluginCodeLifetime known, PluginCodeLifetime conflicting)
    {
        var identity = new object();
        var original = Definition(new("module", identity, "test", Lifetime: known));
        var ledger = new PluginModuleOwnership([original], 1);
        var baseline = Assert.Single(ledger.Snapshot());
        using var ticket = ledger.Reserve("entry");
        var error = Assert.Throws<ConfigException>(() =>
            ticket.Bind(Definition(new("module", identity, "test", new object(), conflicting))));
        Assert.Contains("code lifetime", error.Message);
        Assert.Null(ticket.RevisionId);
        Assert.Equal(1, ledger.PendingCount);
        Assert.Equal(baseline, Assert.Single(ledger.Snapshot()));

        ticket.Bind(Definition(new("module", identity, "test", new object(), known)));
        Assert.Equal(known, Assert.Single(ledger.Snapshot()).Lifetime);
        ticket.Dispose();
        Assert.Equal(baseline, Assert.Single(ledger.Snapshot()));
        Assert.Equal(0, ledger.PendingCount);
    }

    /// <summary>Unknown capability may become known once, and later omitted capability cannot erase the established lifetime.</summary>
    [Theory]
    [InlineData(PluginCodeLifetime.ProcessLifetime)]
    [InlineData(PluginCodeLifetime.Collectible)]
    public void UnknownLifetimePromotesWithoutBeingErasedByLaterUnknownMetadata(PluginCodeLifetime known)
    {
        var identity = new object();
        var original = Definition(new("module", identity, "test"));
        var ledger = new PluginModuleOwnership([original], 1);
        var revisionId = Assert.Single(ledger.Snapshot()).RevisionId;
        using (var promoted = ledger.Reserve("entry"))
            promoted.Bind(Definition(new("module", identity, "test", Lifetime: known)));
        using (var unknown = ledger.Reserve("entry"))
        {
            unknown.Bind(original);
            Assert.Equal(revisionId, unknown.RevisionId);
            Assert.Equal(known, Assert.Single(ledger.Snapshot()).Lifetime);
        }
        var retained = Assert.Single(ledger.Snapshot());
        Assert.Equal(known, retained.Lifetime);
        Assert.Equal(1, retained.CatalogPins);
        Assert.Equal(0, retained.Activations);
        Assert.Equal(0, ledger.PendingCount);
    }

    /// <summary>A conflicting anchor cannot partially promote the lifetime before rejecting the acquisition.</summary>
    [Fact]
    public void AnchorConflictDoesNotPromoteUnknownLifetime()
    {
        var identity = new object();
        var anchor = new object();
        var ledger = new PluginModuleOwnership([Definition(new("module", identity, "test", anchor))], 1);
        using var ticket = ledger.Reserve("entry");
        Assert.Throws<ConfigException>(() => ticket.Bind(Definition(new("module", identity, "test",
            new object(), PluginCodeLifetime.ProcessLifetime))));
        Assert.Equal(PluginCodeLifetime.Unknown, Assert.Single(ledger.Snapshot()).Lifetime);
        ticket.Bind(Definition(new("module", identity, "test", anchor, PluginCodeLifetime.Collectible)));
        Assert.Equal(PluginCodeLifetime.Collectible, Assert.Single(ledger.Snapshot()).Lifetime);
    }

    /// <summary>Undefined numeric lifetime values cannot enter either the catalog or a pending acquisition.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void UndefinedLifetimeCapabilityIsRejectedWithoutLosingPendingOwnership(int invalid)
    {
        var invalidDefinition = Definition(new("module", new object(), "test", Lifetime: (PluginCodeLifetime)invalid));
        Assert.Throws<ConfigException>(() => new PluginModuleOwnership([invalidDefinition], 1));
        var ledger = new PluginModuleOwnership([], 1);
        using var ticket = ledger.Reserve("entry");
        Assert.Throws<ConfigException>(() => ticket.Bind(invalidDefinition));
        Assert.Equal(1, ledger.PendingCount);
        Assert.Empty(ledger.Snapshot());
        Assert.Null(ticket.RevisionId);
        Assert.False(ticket.IsReleased);
    }

    /// <summary>Native metadata reports the actual assembly capability even for collectible assemblies in a shared runtime context.</summary>
    [Theory]
    [InlineData(false, PluginCodeLifetime.ProcessLifetime)]
    [InlineData(true, PluginCodeLifetime.Collectible)]
    public void NativeLifetimeUsesActualAssemblyCapability(bool collectible, PluginCodeLifetime expected)
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName("LifetimeFixture_" + Guid.NewGuid().ToString("N")),
            collectible ? System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect : System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("fixture").DefineType("LifetimeFixture").CreateType()!;
        var code = NativePluginExecutionFactory.ModuleCodeFor(type, null);
        Assert.Same(type.Assembly, code.Identity);
        Assert.Equal(expected, code.Lifetime);
        Assert.Same(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly), code.LifetimeAnchor);
    }

    /// <summary>Factories without provenance remain separately attributed even when descriptor values and coordination keys match.</summary>
    [Fact]
    public void UnattributedDescriptorsUseTheirOwnReferenceIdentity()
    {
        var original = Definition(null);
        var equalCopy = original with { };
        var ledger = new PluginModuleOwnership([original], 2);
        using var first = ledger.Reserve("first");
        using var second = ledger.Reserve("second");
        first.Bind(original);
        second.Bind(equalCopy);
        Assert.NotEqual(first.RevisionId, second.RevisionId);
        Assert.Equal(2, ledger.Snapshot().Count);
        Assert.All(ledger.Snapshot(), revision =>
        {
            Assert.Equal("unattributed:definition", revision.ModuleId);
            Assert.Equal("unattributed", revision.Attribution);
        });
    }

    /// <summary>Pending reservations and retained cache revisions share the fixed combined record budget.</summary>
    [Fact]
    public void CacheRevisionsCanExhaustCapacityBeforeAnotherFactoryIsAdmitted()
    {
        var ledger = new PluginModuleOwnership([], 1);
        var first = ledger.Reserve("entry");
        first.Bind(Definition(new("module", new object(), "test")));
        var firstCache = first.RetainDefinition();
        first.Dispose();
        var second = ledger.Reserve("entry");
        second.Bind(Definition(new("module", new object(), "test")));
        var secondCache = second.RetainDefinition();
        second.Dispose();
        Assert.Equal(ledger.Capacity, ledger.Snapshot().Count);
        Assert.Throws<ConfigException>(() => ledger.Reserve("entry"));
        Assert.Equal(0, ledger.PendingCount);
        firstCache.Dispose();
        using var next = ledger.Reserve("entry");
        Assert.Equal(ledger.Capacity, ledger.Snapshot().Count + ledger.PendingCount);
        secondCache.Dispose();
    }

    /// <summary>Cache references allow one commit's overlap but cannot accumulate an unbounded lease history.</summary>
    [Fact]
    public void CacheLeaseCountHasAnExplicitCommitOverlapBound()
    {
        var ledger = new PluginModuleOwnership([], 1);
        using var ticket = ledger.Reserve("entry");
        ticket.Bind(Definition(new("module", new object(), "test")));
        var first = ticket.RetainDefinition();
        using var overlap = ticket.RetainDefinition();
        Assert.Throws<ConfigException>(() => ticket.RetainDefinition());
        Assert.Equal(2, Assert.Single(ledger.Snapshot()).DefinitionCaches);
        first.Dispose();
        first.Dispose();
        using var replacement = ticket.RetainDefinition();
        Assert.Equal(2, Assert.Single(ledger.Snapshot()).DefinitionCaches);
    }

    /// <summary>Repeated replacement prunes zero-holder revisions instead of retaining cumulative history.</summary>
    [Fact]
    public void RepeatedTurnoverRetainsOnlyCatalogAndLastCommittedCode()
    {
        var catalog = Definition(new("module", new object(), "test"));
        var ledger = new PluginModuleOwnership([catalog], 1);
        IDisposable? cache = null;
        for (var cycle = 0; cycle < 128; cycle++)
        {
            using var ticket = ledger.Reserve("entry");
            ticket.Bind(Definition(new("module", new object(), "test")));
            var next = ticket.RetainDefinition();
            cache?.Dispose();
            cache = next;
            Assert.Equal(0, ledger.PendingCount);
            Assert.Equal(2, ledger.Snapshot().Count);
            Assert.Equal(1, ledger.Snapshot().Sum(revision => revision.Activations));
        }
        Assert.Equal(0, ledger.Snapshot().Sum(revision => revision.Activations));
        cache!.Dispose();
        var baseline = Assert.Single(ledger.Snapshot());
        Assert.Equal(1, baseline.CatalogPins);
        Assert.Equal(0, baseline.DefinitionCaches);
    }

    /// <summary>Concurrent admission for one row creates one owner, and concurrent release cannot decrement twice.</summary>
    [Fact]
    public async Task ConcurrentAdmissionAndReleasePreserveUniqueOwnership()
    {
        var ledger = new PluginModuleOwnership([], 1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winners = new ConcurrentBag<PluginModuleOwnership.Acquisition>();
        var attempts = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try { winners.Add(ledger.Reserve("entry")); }
            catch (InvalidOperationException) { }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(attempts);
        var ticket = Assert.Single(winners);
        Assert.Equal(1, ledger.PendingCount);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(ticket.Dispose)));
        Assert.True(ticket.IsReleased);
        Assert.Equal(0, ledger.PendingCount);
    }

    /// <summary>Invalid or overflowing startup budgets fail before admitting records.</summary>
    [Fact]
    public void StartupBudgetAndEmptyHostBoundsAreValidated()
    {
        Assert.Throws<ConfigException>(() => new PluginModuleOwnership([], -1));
        Assert.Throws<ConfigException>(() => new PluginModuleOwnership([], int.MaxValue));
        var empty = new PluginModuleOwnership([], 0);
        Assert.Equal(0, empty.Capacity);
        Assert.Throws<ConfigException>(() => empty.Reserve("entry"));
        Assert.Throws<ConfigException>(() => empty.Reserve(" "));
        Assert.Empty(empty.Snapshot());
    }

    /// <summary>A factory claiming provenance cannot silently downgrade a null declaration to opaque ownership.</summary>
    [Fact]
    public void DeclaredNullProvenanceIsRejectedBeforeCatalogCapture()
    {
        var error = Assert.Throws<ConfigException>(() => new PluginDescriptor(new PluginAttribute("invalid-source"),
            new AttributedFactory(null!), "module-ownership-test", "1.0.0", new Dictionary<string, string>()));
        Assert.Contains("returned no provenance", error.Message, StringComparison.Ordinal);
    }

    private static PluginDescriptor Definition(PluginModuleCode? code, string id = "definition") =>
        new(new PluginAttribute(id), code is null ? new Factory() : new AttributedFactory(code),
            "module-ownership-test", "1.0.0", new Dictionary<string, string>());

    private class Factory : IPluginExecutionFactory
    {
        public string? CoordinationKey => "shared-coordination";
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Ledger unit tests must not execute a factory.");
    }

    private sealed class AttributedFactory(PluginModuleCode code) : Factory, IPluginModuleSource
    {
        public PluginModuleCode ModuleCode => code;
    }

    private sealed class HostileIdentity
    {
        public override bool Equals(object? other) => throw new InvalidOperationException("Code identity must not use value equality.");
        public override int GetHashCode() => throw new InvalidOperationException("Code identity must not use a backend hash implementation.");
    }
}
