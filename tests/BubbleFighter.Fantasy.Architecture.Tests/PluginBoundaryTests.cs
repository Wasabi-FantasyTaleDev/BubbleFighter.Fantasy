using BubbleFighter.Fantasy.Tests;
using System.Reflection;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

public sealed class PluginBoundaryTests
{
    private static string PluginRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    internal static PluginCatalog Discover() => PluginCatalog.Discover([PluginRoot]);

    [Fact]
    public void DomainAndKernelDependenciesPointInOneDirection()
    {
        var abstractions = typeof(IPlugin).Assembly;
        var contracts = typeof(IInventoryRepository).Assembly;
        Assert.DoesNotContain(abstractions.GetReferencedAssemblies(), a => a.Name == contracts.GetName().Name);
        Assert.DoesNotContain(typeof(PluginHost).Assembly.GetReferencedAssemblies(), a => a.Name == contracts.GetName().Name);
        Assert.Equal([abstractions.GetName().Name], contracts.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("BubbleFighter.", StringComparison.Ordinal)).Select(a => a.Name));
        Assert.DoesNotContain(abstractions.GetExportedTypes(), t => t.Name is "IInventoryRepository" or "IMatchConnectionTracker" or "IRoomStartService");
        Assert.All(contracts.GetExportedTypes(), t => Assert.StartsWith("BubbleFighter.Fantasy.Contracts.", t.Namespace));
        Assert.DoesNotContain(contracts.GetReferencedAssemblies(), a => IsDatabaseAssembly(a.Name!));
    }

    [Fact]
    public void ServiceDeclarationsKeepSharedIdentityAcrossIndependentDiscoveries()
    {
        var first = Discover();
        var second = Discover().Plugins.ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        var properties = typeof(PluginAttribute).GetProperties().Where(property => property.PropertyType == typeof(Type[])).ToArray();
        var unstable = new List<string>();
        foreach (var plugin in first.Plugins)
        {
            var declaration = plugin.Declaration;
            var other = second[plugin.Id].Declaration;
            var currentTypes = properties.SelectMany(property => (Type[])property.GetValue(declaration)!).Distinct().ToArray();
            var nextTypes = properties.SelectMany(property => (Type[])property.GetValue(other)!).Distinct()
                .ToDictionary(type => type.AssemblyQualifiedName!, StringComparer.Ordinal);
            foreach (var contract in currentTypes)
                if (!nextTypes.TryGetValue(contract.AssemblyQualifiedName!, out var next) || !ReferenceEquals(contract, next) ||
                    AssemblyLoadContext.GetLoadContext(contract.Assembly) != AssemblyLoadContext.Default)
                    unstable.Add($"{plugin.Id}: {contract.FullName} ({contract.Assembly.GetName().Name})");
        }
        Assert.True(unstable.Count == 0, string.Join(Environment.NewLine, unstable));
    }

    [Fact]
    public void PluginAssembliesDoNotReferenceEachOtherAndDatabaseDebtCannotGrow()
    {
        var descriptors = Discover().Plugins.Where(p => p.MainAssemblyPath is not null).ToArray();
        Assert.NotEmpty(descriptors);
        var pluginNames = descriptors.Select(p => Assert.IsAssignableFrom<Type>(p.Type).Assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        var databaseDebt = new List<string>();
        foreach (var plugin in descriptors)
        {
            var references = Assert.IsAssignableFrom<Type>(plugin.Type).Assembly.GetReferencedAssemblies();
            Assert.DoesNotContain(references, a => pluginNames.Contains(a.Name!));
            if (!plugin.Id.StartsWith("persistence.", StringComparison.Ordinal))
                databaseDebt.AddRange(references.Where(a => IsDatabaseAssembly(a.Name!)).Select(a => $"{plugin.Id}:{a.Name}"));
        }
        // Remove entries as SQL moves behind storage contracts; never add a new business dependency.
        Assert.Equal(new[] { "lobby:Dapper" }, databaseDebt.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void UnprovenContractFieldsHaveAnExactMigrationBaseline()
    {
        var actual = typeof(IInventoryRepository).Assembly.GetExportedTypes().SelectMany(t =>
            t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is PropertyInfo or FieldInfo && m.Name.Contains("Unknown", StringComparison.Ordinal))
                .Select(m => $"{t.FullName}.{m.Name}"));
        var expected = new Dictionary<string, int[]>
        {
            ["Inventory.EquippedSlotRecord"] = [10, 11, 12, 13],
            ["Inventory.WeaponPresetChangeRequested"] = [0],
        }.SelectMany(pair => pair.Value.Select(n => $"BubbleFighter.Fantasy.Contracts.{pair.Key}.Unknown{n}"));
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(IInventoryRepository).Assembly.GetExportedTypes(), t => t.Name.Contains("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsolatedProviderSharesContractsAndCanUnloadAndReload()
    {
        var catalog = Discover();
        var descriptor = catalog.Find("persistence.memory")!;
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(Assert.IsAssignableFrom<Type>(descriptor.Type).Assembly));
        var provided = descriptor.Declaration.Provides;
        Assert.Contains(typeof(IPlayerWalletRepository), provided);
        Assert.All(provided, t => Assert.Same(typeof(IInventoryRepository).Assembly, t.Assembly));
        var id = Guid.NewGuid().ToString("N");
        var host = new PluginHost([
            Row("memory", "persistence.memory"), Row(id, "test.contract-consumer")
        ], catalog, NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var first = ContractConsumer.Repositories[id];
            Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(first.GetType().Assembly));
            var wallet = new PlayerWallet(1, 100, 200);
            await first.UpsertAsync(wallet, CancellationToken.None);
            Assert.Equal(wallet, await first.FindByUserNumAsync(1, CancellationToken.None));
            var reload = await host.ReloadAsync("memory", CancellationToken.None);
            Assert.Equal(new[] { "memory", id }, reload.ReloadedRowIds);
            var second = ContractConsumer.Repositories[id];
            Assert.NotSame(first.GetType().Assembly, second.GetType().Assembly);
            Assert.Null(await second.FindByUserNumAsync(1, CancellationToken.None));
            await host.UnloadAsync(id, CancellationToken.None);
            await host.UnloadAsync("memory", CancellationToken.None);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
            await host.LoadAsync("memory", CancellationToken.None);
            await host.LoadAsync(id, CancellationToken.None);
            Assert.NotSame(second, ContractConsumer.Repositories[id]);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
        Assert.False(ContractConsumer.Repositories.ContainsKey(id));
    }

    [Fact]
    public void RepeatedDiscoveryDoesNotRescanIsolatedPluginContexts()
    {
        var first = Discover().Plugins.Select(p => p.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(first, Discover().Plugins.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DiskPluginsPublishReleaseAndCurrentContractVersions()
    {
        var plugins = Discover().Plugins.Where(plugin => plugin.MainAssemblyPath is not null).ToArray();
        var contractsVersion = typeof(IInventoryRepository).Assembly.GetName().Version!.ToString();
        Assert.NotEmpty(plugins);
        Assert.All(plugins, plugin =>
        {
            Assert.StartsWith(plugin.Id == "fighters" ? "2.0.0" : "1.0.0", plugin.Version);
            Assert.Equal("8.13.0.0", plugin.ContractVersions["BubbleFighter.Fantasy.Abstractions"]);
            Assert.All(plugin.ContractVersions, pair => Assert.Equal(
                pair.Key == "BubbleFighter.Fantasy.Contracts" ? contractsVersion : "8.13.0.0", pair.Value));
        });
        // Shared plugins name no client builds: a build is added with profile and packet-table data alone.
        Assert.All(new[] { "protocol", "dialect", "lobby", "ingame", "shop", "equipment", "inventoryview", "centers",
            "dialect.messages", "dialect.rooms", "dialect.match", "dialect.match-offers",
            "dialect.match-replies", "dialect.scene-seeds" },
            id => Assert.Empty(plugins.Single(plugin => plugin.Id == id).SupportedBuilds));
    }

    internal static PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
        new() { Id = id, Plugin = plugin, Source = "architecture-test", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };

    [Theory]
    [InlineData("kr-1256", "kr", 1256, 1304)]
    [InlineData("kr-812", "kr", 812, 458)]
    [InlineData("id-400", "id", 400, 113)]
    public async Task IsolatedDialectRowPublishesOnlyItsConfiguredStream(string profileId, string region, int major, int resource)
    {
        var row = Row("dialect", "dialect", TestWireDialects.RowConfig(profileId)) with { ClientBuild = new(region, major, resource) };
        var probe = Guid.NewGuid().ToString("N");
        var host = new PluginHost([Row("core.wire-dialects", "core.wire-dialects"), row, Row(probe, "test.dialect-registry-probe")],
            Discover(), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.All(host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
            var dialect = Assert.Single(DialectRegistryProbe.Registries[probe].All);
            Assert.Equal(profileId, dialect.Id);
            Assert.Equal(new Abstractions.Protocol.ClientBuild(region, major, resource),
                new Abstractions.Protocol.ClientBuild(dialect.Profile.Region, dialect.Profile.ClientVersionMajor, dialect.Profile.ResourceVersion));
            Assert.Same(typeof(IPlugin).Assembly, typeof(Abstractions.Protocol.IWireDialect).Assembly);
            Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(dialect.GetType().Assembly));
            Assert.False(dialect is Wire.WireDialect);
            Assert.Equal(Wire.BootstrapGreeting.Build(4, ""), dialect.BuildServerPreamble(4, ""));
            var encrypted = dialect.EncodeFrame(2, [1, 2, 3]);
            dialect.CreateEncryptor(Abstractions.Protocol.PacketDirection.ServerToClient).Apply(encrypted);
            Wire.SimpleCipher.ServerToClient().DecryptInPlace(encrypted);
            Assert.Equal(major == 1256 ? Wire.WireFrame.Encode(2, [1, 2, 3]) : Wire.LegacyFrame.Encode(2, [1, 2, 3]), encrypted);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    private static bool IsDatabaseAssembly(string name) => name == "Dapper" || name == "MySqlConnector"
        || name.StartsWith("SqlSugar", StringComparison.Ordinal) || name.StartsWith("Microsoft.Data.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || name == "Npgsql";

    [Plugin("test.dialect-registry-probe", Requires = [typeof(Abstractions.Protocol.IWireDialectRegistry)])]
    public sealed class DialectRegistryProbe : IPlugin
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Abstractions.Protocol.IWireDialectRegistry> Registries = new();

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Registries[context.Row.Id] = context.Services.Get<Abstractions.Protocol.IWireDialectRegistry>();
            context.Effect(() => Registries.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("test.contract-consumer", Requires = [typeof(IPlayerWalletRepository)])]
    public sealed class ContractConsumer : IPlugin
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPlayerWalletRepository> Repositories = new();

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Repositories[context.Row.Id] = context.Services.Get<IPlayerWalletRepository>();
            context.Effect(() => Repositories.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }
}
