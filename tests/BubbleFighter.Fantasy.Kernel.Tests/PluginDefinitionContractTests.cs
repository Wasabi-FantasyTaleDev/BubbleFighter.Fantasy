using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PluginDefinitionContractTests
{
    public interface IValue { }
    public interface IChanged { }

    private static IReadOnlyDictionary<string, string> Versions() => new Dictionary<string, string>
    { [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    private sealed class Factory : IPluginExecutionFactory, IPluginModuleSource
    {
        public string? CoordinationKey { get; set; } = "definition-contract-module";
        public PluginModuleCode ModuleCode { get; set; } = new("definition-contract-module", new object(), "test-code");
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Contract snapshot tests must not acquire executions.");
    }

    private static PluginDefinition Definition(PluginAttribute? declaration = null, Factory? factory = null,
        IReadOnlyDictionary<string, string>? versions = null) =>
        new(declaration ?? new PluginAttribute("definition"), factory ?? new Factory(), "contract-test", "1.2.3", versions ?? Versions());

    private static PluginAttribute CompleteDeclaration() => new("definition")
    {
        Description = "definition description", SupportedBuilds = ["kr:1256"], Overrides = ["provider:example.Message"],
        Requires = [typeof(IValue)], RequiresMany = [typeof(IValue)], Optional = [typeof(IValue)], OptionalMany = [typeof(IValue)],
        Provides = [typeof(IValue)], ProvidesMany = [typeof(IValue)], ConditionalProvides = [typeof(IValue)],
        ConditionalProvidesMany = [typeof(IValue)], Observes = [typeof(IValue)], Decorates = [typeof(IValue)],
    };

    private static void MutateArrays(PluginAttribute declaration)
    {
        declaration.SupportedBuilds[0] = "cn:279";
        declaration.Overrides[0] = "other:example.Message";
        foreach (var property in typeof(PluginAttribute).GetProperties().Where(property => property.PropertyType == typeof(Type[])))
            ((Type[])property.GetValue(declaration)!)[0] = typeof(IChanged);
    }

    private static void AssertOriginalArrays(PluginAttribute declaration)
    {
        Assert.Equal(["kr:1256"], declaration.SupportedBuilds);
        Assert.Equal(["provider:example.Message"], declaration.Overrides);
        foreach (var property in typeof(PluginAttribute).GetProperties().Where(property => property.PropertyType == typeof(Type[])))
            Assert.Equal([typeof(IValue)], (Type[])property.GetValue(declaration)!);
    }

    [Fact]
    public void InputDeclarationArraysCannotChangeCapturedDefinition()
    {
        var original = CompleteDeclaration();
        var definition = Definition(original);
        MutateArrays(original);
        AssertOriginalArrays(definition.Declaration);
        Assert.Equal("definition description", definition.Declaration.Description);
    }

    [Fact]
    public void EveryReturnedDeclarationHasIndependentArrays()
    {
        var definition = Definition(CompleteDeclaration());
        var first = definition.Declaration;
        var second = definition.Declaration;
        MutateArrays(first);
        AssertOriginalArrays(second);
        AssertOriginalArrays(definition.Declaration);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ContractVersionMappingIsCopiedAndRejectsMutationThroughDictionaryInterface()
    {
        var versions = new Dictionary<string, string> { ["backend.contract"] = "1.2.3" };
        var definition = Definition(versions: versions);
        versions["backend.contract"] = "9.0.0";
        versions["another.contract"] = "2.0.0";
        Assert.Single(definition.ContractVersions);
        Assert.Equal("1.2.3", definition.ContractVersions["backend.contract"]);
        var mutableView = Assert.IsAssignableFrom<IDictionary<string, string>>(definition.ContractVersions);
        Assert.Throws<NotSupportedException>(() => mutableView["backend.contract"] = "changed");
        Assert.Throws<NotSupportedException>(() => mutableView.Add("other", "1.0.0"));
    }

    [Fact]
    public void SourceDiagnosticCopiesExactlyTheSuppliedIdentitySnapshot()
    {
        var ids = new List<string> { "one", "two" };
        var info = new PluginDefinitionSourceInfo("source", 17, "owner", PluginDefinitionSourceState.Published, ids);
        ids[0] = "changed"; ids.Add("three");
        Assert.Equal(["one", "two"], info.DefinitionIds);
        Assert.Equal("source", info.SourceId); Assert.Equal(17, info.Revision); Assert.Equal("owner", info.OwnerId);
        Assert.Equal(PluginDefinitionSourceState.Published, info.State); Assert.Null(info.Error);
        var mutableView = Assert.IsAssignableFrom<IList<string>>(info.DefinitionIds);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = "changed");
        Assert.Throws<NotSupportedException>(() => mutableView.Add("three"));
        Assert.Empty(new PluginDefinitionSourceInfo("empty", 18, "owner", PluginDefinitionSourceState.Preparing, []).DefinitionIds);
    }

    [Fact]
    public void NullSourceDiagnosticMetadataIsRejectedInsteadOfReplaced()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginDefinitionSourceInfo(null!, 1, "owner", PluginDefinitionSourceState.Preparing, []));
        Assert.Throws<ArgumentNullException>(() => new PluginDefinitionSourceInfo("source", 1, null!, PluginDefinitionSourceState.Preparing, []));
        Assert.Throws<ArgumentNullException>(() => new PluginDefinitionSourceInfo("source", 1, "owner", PluginDefinitionSourceState.Preparing, null!));
    }

    [Fact]
    public void NullDefinitionMetadataIsRejectedInsteadOfDefaulted()
    {
        var declaration = new PluginAttribute("definition"); var factory = new Factory();
        Assert.Throws<ArgumentNullException>(() => new PluginDefinition(null!, factory, "origin", "1.0.0", Versions()));
        Assert.Throws<ArgumentNullException>(() => new PluginDefinition(declaration, null!, "origin", "1.0.0", Versions()));
        Assert.Throws<ArgumentNullException>(() => new PluginDefinition(declaration, factory, null!, "1.0.0", Versions()));
        Assert.Throws<ArgumentNullException>(() => new PluginDefinition(declaration, factory, "origin", null!, Versions()));
        Assert.Throws<ArgumentNullException>(() => new PluginDefinition(declaration, factory, "origin", "1.0.0", null!));
        Assert.Throws<ArgumentException>(() => new PluginDefinition(declaration, factory, "origin", " ", Versions()));
        Assert.Throws<ArgumentException>(() => new PluginDefinition(declaration, factory, "origin", "1.0.0",
            new Dictionary<string, string> { ["contract"] = null! }));
        var error = Assert.Throws<ConfigException>(() => Definition(new PluginAttribute("definition") { Requires = null! }));
        Assert.Contains(nameof(PluginAttribute.Requires), error.Message);
        Assert.Throws<ConfigException>(() => Definition(new PluginAttribute("definition") { SupportedBuilds = null! }));
        Assert.Throws<ConfigException>(() => Definition(new PluginAttribute("definition") { Overrides = null! }));
    }

    [Fact]
    public void DescriptorBridgeCapturesBackendIdentityAndProvenanceWithoutAcquiringExecution()
    {
        var factory = new Factory(); var code = factory.ModuleCode;
        var definition = Definition(new PluginAttribute("definition") { Provides = [typeof(IValue)] }, factory);
        var descriptor = new PluginDescriptor(definition);
        factory.ModuleCode = new("replacement", new object(), "new-code");
        factory.CoordinationKey = "replacement";
        Assert.Equal("definition", descriptor.Id); Assert.Equal("contract-test", descriptor.Origin);
        Assert.Equal("1.2.3", descriptor.Version); Assert.Null(descriptor.Type);
        Assert.Same(factory, descriptor.ExecutionFactory); Assert.Same(code, descriptor.ModuleCode);
        Assert.Equal("definition-contract-module", descriptor.CoordinationKey);
        Assert.Equal([typeof(IValue)], descriptor.Declaration.Provides);
        Assert.Equal(definition.ContractVersions, descriptor.ContractVersions);
    }

    [Fact]
    public void DescriptorBridgeUsesExistingDeclarationShapeValidation()
    {
        var definition = Definition(new PluginAttribute("definition") { Provides = [typeof(int)] });
        var error = Assert.Throws<ConfigException>(() => new PluginDescriptor(definition));
        Assert.Contains("Provides[0]", error.Message);
        Assert.Throws<ArgumentNullException>(() => new PluginDescriptor((PluginDefinition)null!));
    }

    [Fact]
    public void DescriptorBridgeLeavesContractVersionCompatibilityToCatalogAdmission()
    {
        var invalid = new Dictionary<string, string> { [typeof(IPlugin).Assembly.GetName().Name!] = "not-a-version" };
        var descriptor = new PluginDescriptor(Definition(versions: invalid));
        var error = Assert.Throws<ConfigException>(() => PluginCatalog.FromDefinitions([descriptor]));
        Assert.Contains("invalid shared contract version", error.Message);
        var accepted = new PluginDescriptor(Definition());
        Assert.Same(accepted, Assert.Single(PluginCatalog.FromDefinitions([accepted]).Plugins));
    }

    [Fact]
    public void DescriptorBridgePreservesFactoryMetadataAdmissionFailures()
    {
        Assert.Throws<ConfigException>(() => new PluginDescriptor(Definition(factory: new Factory { CoordinationKey = " " })));
        Assert.Throws<ConfigException>(() => new PluginDescriptor(Definition(factory: new Factory { ModuleCode = null! })));
    }
}
