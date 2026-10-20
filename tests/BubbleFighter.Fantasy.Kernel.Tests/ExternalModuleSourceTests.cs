using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Uses an independently compiled backend to verify the public module-provenance seam through real host ownership.</summary>
public sealed class ExternalModuleSourceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Type> ExternalType = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "external-module-source", "ExternalModuleSourceFixture.dll");
        Assert.True(File.Exists(path), $"External fixture output is missing: {path}");
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
            .GetType("BubbleFighter.Fantasy.TestFixtures.ExternalModuleSource.ExternalFactory", throwOnError: true)!;
    });

    /// <summary>The independently built factory has no kernel reference or friend-assembly grant.</summary>
    [Fact]
    public void FixtureDependsOnPublishedAbstractionsWithoutKernelAccess()
    {
        var assembly = ExternalType.Value.Assembly;
        var references = assembly.GetReferencedAssemblies();
        Assert.Contains(references, reference => reference.Name == "BubbleFighter.Fantasy.Abstractions");
        Assert.DoesNotContain(references, reference => reference.Name == "BubbleFighter.Fantasy.Kernel");
        Assert.Empty(assembly.GetCustomAttributes<InternalsVisibleToAttribute>());
        Assert.True(typeof(IPluginModuleSource).IsAssignableFrom(ExternalType.Value));
    }

    /// <summary>A neutral execution activates under real host ownership and releases its effects before its executor.</summary>
    [Fact]
    public async Task ExternalBackendParticipatesInHostActivationAndDisposal()
    {
        var anchor = new object();
        var code = new PluginModuleCode("external-module", Identity(), "fixture-backend", anchor, PluginCodeLifetime.Collectible);
        var factory = Factory("external-one", code);
        var definition = Definition("external-one", factory);
        Assert.Null(definition.Type);
        Assert.Same(anchor, definition.ModuleCode!.LifetimeAnchor);
        var host = Host(definition);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
            Assert.Equal(1, Read<int>(factory, "Activations"));
            var revision = Assert.Single(host.ModuleOwnership.Snapshot());
            Assert.Equal("external-module", revision.ModuleId);
            Assert.Equal("fixture-backend", revision.Attribution);
            Assert.Equal(PluginCodeLifetime.Collectible, revision.Lifetime);
            Assert.Equal(1, revision.Activations);
            Assert.Equal(1, revision.DefinitionCaches);
            Assert.Equal(1, revision.CatalogPins);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(1, Read<int>(factory, "Disposals"));
        Assert.Equal(1, Read<int>(factory, "EffectsDisposed"));
        Assert.True(Read<bool>(factory, "ExecutionDisposeSawEffectCleanup"));
        Assert.Equal(0, Assert.Single(host.ModuleOwnership.Snapshot()).Activations);
    }

    /// <summary>Revision grouping uses opaque reference identity despite backend-defined equality methods that throw.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReferenceIdentityDeterminesSharedRevision(bool sharedIdentity)
    {
        var firstIdentity = Identity();
        var first = Definition("external-first", Factory("external-first", new("same-module", firstIdentity, "fixture-backend")));
        var second = Definition("external-second", Factory("external-second",
            new("same-module", sharedIdentity ? firstIdentity : Identity(), "fixture-backend")));
        var host = Host(first, second);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            var snapshots = host.ModuleOwnership.Snapshot();
            Assert.Equal(sharedIdentity ? 1 : 2, snapshots.Count);
            Assert.Equal(2, snapshots.Sum(item => item.Activations));
            Assert.Equal(2, snapshots.Sum(item => item.CatalogPins));
            Assert.All(host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>Catalog and ledger diagnostics use the captured provenance without calling the external getter again.</summary>
    [Fact]
    public void DiagnosticInspectionNeverReentersCapturedSource()
    {
        var factory = Factory("snapshot", new("module", Identity(), "fixture-backend"));
        var definition = Definition("snapshot", factory);
        Assert.Equal(1, Read<int>(factory, "SourceReads"));
        ExternalType.Value.GetProperty("ThrowOnSourceRead")!.SetValue(factory, true);
        var host = Host(definition);
        for (var iteration = 0; iteration < 4; iteration++)
        {
            Assert.Single(host.ModuleOwnership.Snapshot());
            Assert.Single(host.Instances);
        }
        Assert.Equal(1, Read<int>(factory, "SourceReads"));
    }

    /// <summary>An advertised provenance source cannot silently return null.</summary>
    [Fact]
    public void NullSourceFailsAtDefinitionCapture()
    {
        var error = Assert.Throws<ConfigException>(() => Definition("null-source", Factory("null-source", null)));
        Assert.Contains("no provenance", error.Message);
    }

    /// <summary>Malformed public provenance cannot enter host ownership as a valid revision.</summary>
    [Theory]
    [InlineData("module")]
    [InlineData("identity")]
    [InlineData("attribution")]
    [InlineData("lifetime")]
    public void InvalidPublicProvenanceFailsHostAdmission(string invalidField)
    {
        var code = new PluginModuleCode(invalidField == "module" ? " " : "module",
            invalidField == "identity" ? null! : Identity(), invalidField == "attribution" ? " " : "fixture-backend",
            Lifetime: invalidField == "lifetime" ? (PluginCodeLifetime)int.MaxValue : PluginCodeLifetime.Unknown);
        var definition = Definition("invalid", Factory("invalid", code));
        Assert.Throws<ConfigException>(() => Host(definition));
    }

    /// <summary>Changing a shared factory's attribution during acquisition rejects the returned execution before plugin effects can publish.</summary>
    [Theory]
    [InlineData("identity")]
    [InlineData("module")]
    [InlineData("attribution")]
    [InlineData("anchor")]
    [InlineData("lifetime")]
    public async Task AcquisitionCannotRewritePreviouslyCapturedFactoryProvenance(string changedField)
    {
        var accepted = new PluginModuleCode("accepted-module", Identity(), "fixture-backend", new object(), PluginCodeLifetime.Unknown);
        var replacement = changedField switch
        {
            "identity" => accepted with { Identity = Identity() },
            "module" => accepted with { ModuleId = "other-module" },
            "attribution" => accepted with { Attribution = "other-backend" },
            "anchor" => accepted with { LifetimeAnchor = new object() },
            _ => accepted with { Lifetime = PluginCodeLifetime.Collectible },
        };
        var factory = Factory("drifting", accepted);
        var definition = Definition("drifting", factory);
        var host = Host(definition);
        var acceptedRevision = Assert.Single(host.ModuleOwnership.Snapshot()).RevisionId;
        ExternalType.Value.GetProperty("CodeOnAcquire")!.SetValue(factory, replacement);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.LoadAsync("drifting", default).AsTask().WaitAsync(Limit));
            Assert.Contains("changed its factory code provenance", error.ToString());
            Assert.Equal(0, Read<int>(factory, "Activations"));
            Assert.Equal(0, Read<int>(factory, "EffectsDisposed"));
            Assert.Equal(1, Read<int>(factory, "Disposals"));
            Assert.Equal(0, host.ModuleOwnership.PendingCount);
            var remaining = Assert.Single(host.ModuleOwnership.Snapshot());
            Assert.Equal(acceptedRevision, remaining.RevisionId);
            Assert.Equal("accepted-module", remaining.ModuleId);
            Assert.Equal(0, remaining.Activations);
            Assert.Equal(0, remaining.DefinitionCaches);
            Assert.Equal(1, remaining.CatalogPins);
            Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>A new record containing the same immutable code references remains valid without requiring record object identity.</summary>
    [Fact]
    public async Task AcquisitionAcceptsEquivalentRecordWithStableOpaqueReferences()
    {
        var accepted = new PluginModuleCode("stable-module", Identity(), "fixture-backend", new object(), PluginCodeLifetime.Collectible);
        var equivalent = accepted with { };
        Assert.NotSame(accepted, equivalent);
        var factory = Factory("equivalent", accepted);
        var host = Host(Definition("equivalent", factory));
        var acceptedRevision = Assert.Single(host.ModuleOwnership.Snapshot()).RevisionId;
        ExternalType.Value.GetProperty("CodeOnAcquire")!.SetValue(factory, equivalent);
        try
        {
            await host.LoadAsync("equivalent", default).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
            Assert.Equal(1, Read<int>(factory, "Activations"));
            var revision = Assert.Single(host.ModuleOwnership.Snapshot());
            Assert.Equal(acceptedRevision, revision.RevisionId);
            Assert.Equal(1, revision.Activations);
            Assert.Equal(1, revision.DefinitionCaches);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(1, Read<int>(factory, "Disposals"));
        Assert.Equal(1, Read<int>(factory, "EffectsDisposed"));
    }

    private static object Identity() => ExternalType.Value.GetMethod("CreateHostileIdentity")!.Invoke(null, null)!;
    private static IPluginExecutionFactory Factory(string id, PluginModuleCode? code) =>
        (IPluginExecutionFactory)Activator.CreateInstance(ExternalType.Value, [id, code])!;
    private static T Read<T>(IPluginExecutionFactory factory, string property) =>
        (T)ExternalType.Value.GetProperty(property)!.GetValue(factory)!;
    private static PluginDescriptor Definition(string id, IPluginExecutionFactory factory) =>
        new(new PluginAttribute(id), factory, "external-fixture", "1.0.0",
            new Dictionary<string, string> { [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly.GetName().Version!.ToString() });
    private static PluginHost Host(params PluginDescriptor[] definitions) =>
        new(definitions.Select(definition => new PluginRow
        { Id = definition.Id, Plugin = definition.Id, Source = "external-fixture" }).ToArray(),
            PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
}
