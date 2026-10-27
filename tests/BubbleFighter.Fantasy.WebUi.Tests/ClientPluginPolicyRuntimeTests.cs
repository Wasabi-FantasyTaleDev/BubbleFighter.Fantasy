using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Installation;
using BubbleFighter.Fantasy.Host;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed class ClientPluginPolicyRuntimeTests
{
    [Fact]
    public async Task ActualProfileApplicationPublishesOverridesClearsAndRestoresInheritedPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "bff-policy-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "management"));
        Directory.CreateDirectory(Path.Combine(root, "bundles"));
        File.WriteAllText(Path.Combine(root, "management", "profile.yaml"), "bundles: [core]\n");
        File.WriteAllText(Path.Combine(root, "bundles", "core.yaml"), """
            - id: persistence.memory
              plugin: policy-test.storage
              disabled: true
            - id: persistence.mysql
              plugin: policy-test.storage
              disabled: true
            - id: persistence.sqlite
              plugin: policy-test.storage
              disabled: true
            - id: launcher.http
              plugin: launcher.http
              config:
                ticket_seconds: 600
                plugin_recommendations:
                  - id: inherited.plugin
                    builds: []
                    required: false
            """);
        var session = new SetupProfileSession(root, root, []);
        PluginRow[] shell = [new() { Id = "adapter", Plugin = "host.profiles", Source = "fixture",
            Lifecycle = new() { EntryReserve = 8, ServiceMetadataReserve = 65536 } }];
        session.CaptureShell(shell);
        var launcher = new Recorder("launcher.http");
        var storage = new Recorder("policy-test.storage");
        var host = new PluginHost(shell, PluginCatalog.FromDefinitions([session.Definition, launcher.Definition, storage.Definition]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            var settings = new InstallationSettings { Clients = [], DatabaseKind = "sqlite", PlayerLogin = "open",
                ClientPlugins = [new("selected.plugin", ["build-a"], Disabled: true)] };
            await session.ApplyAsync(settings, new Dictionary<string, object?>(), default);
            var rule = Assert.Single(Rules());
            Assert.Equal("selected.plugin", rule["id"]);
            Assert.Equal(true, rule["disabled"]);
            Assert.Equal("600", launcher.Configuration!["ticket_seconds"]?.ToString());
            await session.ApplyAsync(settings with { ClientPlugins = [] }, new Dictionary<string, object?>(), default);
            Assert.Empty(Rules());
            await session.ApplyAsync(settings with { ClientPlugins = null }, new Dictionary<string, object?>(), default);
            Assert.Equal("inherited.plugin", Assert.Single(Rules())["id"]);
        }
        finally
        {
            await host.ShutdownAsync(default);
            Directory.Delete(root, recursive: true);
        }

        IReadOnlyDictionary<string, object?>[] Rules() =>
            Assert.IsAssignableFrom<IEnumerable<object?>>(launcher.Configuration!["plugin_recommendations"])
                .Select(value => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value)).ToArray();
    }

    private sealed class Recorder(string id) : IPluginExecutionFactory
    {
        internal IReadOnlyDictionary<string, object?>? Configuration;
        internal PluginDescriptor Definition => new(new PluginAttribute(id), this, "fixture", "1.0.0",
            new Dictionary<string, string> { [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly.GetName().Version!.ToString() });
        public string? CoordinationKey => null;
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) => ValueTask.FromResult<IPluginExecution>(new Execution(this));
        private sealed class Execution(Recorder recorder) : IPluginExecution
        {
            public PluginAttribute Declaration => recorder.Definition.Declaration;
            public string Version => "1.0.0";
            public IReadOnlyDictionary<string, string> ContractVersions => recorder.Definition.ContractVersions;
            public Type? ImplementationType => null;
            public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
            {
                recorder.Configuration = context.Row.Config;
                return ValueTask.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
