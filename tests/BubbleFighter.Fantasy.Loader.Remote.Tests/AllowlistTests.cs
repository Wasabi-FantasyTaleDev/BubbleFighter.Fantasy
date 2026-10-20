using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks the per-entry <c>allowed_services</c> and <c>allowed_events</c> lists of both loaders.</summary>
public sealed class AllowlistTests
{
    private static readonly HashSet<string> NoNames = new(StringComparer.Ordinal);

    private static JsonElement Definition() => JsonSerializer.SerializeToElement(new
    {
        id = "p.allow",
        version = "1.0.0",
        requires = new[] { "IMessageRouter" },
        provides = new[] { "IFighterRepository" },
        subscribes = new[] { new { type = "EquipmentSelectionApplying", mode = "async" } },
        publishes = new[] { new { type = "EquipmentSelectionReadRequested", mode = "emit" } },
    });

    /// <summary>Declarations inside both lists are accepted; absent lists allow every declaration.</summary>
    [Fact]
    public void AllowedDeclarationsAndAbsentListsAreAccepted()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "IMessageRouter", "IFighterRepository" };
        var allowedEvents = new HashSet<string>(StringComparer.Ordinal) { "EquipmentSelectionApplying", "EquipmentSelectionReadRequested" };
        var listed = RemoteDefinition.Parse(Definition(), "src", 2, allowed, allowedEvents);
        Assert.Equal("p.allow", listed.Id);
        Assert.Single(listed.Subscribes);
        Assert.Single(listed.Publishes);

        var unlisted = RemoteDefinition.Parse(Definition(), "src", 2);
        Assert.Equal("p.allow", unlisted.Id);
    }

    /// <summary>A required service outside the entry's list is refused, naming the plugin, item and list.</summary>
    [Fact]
    public void ServiceOutsideTheAllowlistIsRefused()
    {
        var error = Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(Definition(), "src", 2, NoNames));
        Assert.Contains("p.allow", error.Message);
        Assert.Contains("requires", error.Message);
        Assert.Contains("IMessageRouter", error.Message);
        Assert.Contains("allowed_services", error.Message);
    }

    /// <summary>A provided service outside the entry's list is refused, naming the field and the service.</summary>
    [Fact]
    public void ProvidedServiceOutsideTheAllowlistIsRefused()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "IMessageRouter" };
        var error = Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(Definition(), "src", 2, allowed));
        Assert.Contains("provides", error.Message);
        Assert.Contains("IFighterRepository", error.Message);
        Assert.Contains("allowed_services", error.Message);
    }

    /// <summary>A subscribed event outside the entry's list is refused, naming the plugin, item and list.</summary>
    [Fact]
    public void SubscribedEventOutsideTheAllowlistIsRefused()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "IMessageRouter", "IFighterRepository" };
        var error = Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(Definition(), "src", 2, allowed, NoNames));
        Assert.Contains("p.allow", error.Message);
        Assert.Contains("subscribes", error.Message);
        Assert.Contains("EquipmentSelectionApplying", error.Message);
        Assert.Contains("allowed_events", error.Message);
    }

    /// <summary>A published event outside the entry's list is refused, naming the field and the event.</summary>
    [Fact]
    public void PublishedEventOutsideTheAllowlistIsRefused()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "IMessageRouter", "IFighterRepository" };
        var events = new HashSet<string>(StringComparer.Ordinal) { "EquipmentSelectionApplying" };
        var error = Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(Definition(), "src", 2, allowed, events));
        Assert.Contains("publishes", error.Message);
        Assert.Contains("EquipmentSelectionReadRequested", error.Message);
        Assert.Contains("allowed_events", error.Message);
    }

    /// <summary>Both loaders accept the optional lists per entry and expose them as sets.</summary>
    [Fact]
    public void LoaderConfigParsesPerEntryAllowlists()
    {
        var row = ConfigRow("loader.process", new Dictionary<string, object?>
        {
            ["command"] = "python",
            ["allowed_services"] = new List<object?> { "IMessageRouter" },
            ["allowed_events"] = new List<object?> { "EquipmentSelectionApplying" },
        });
        var entry = Assert.Single(RemoteLoaderConfig.Parse(row).Entries);
        Assert.Equal(["IMessageRouter"], entry.AllowedServices);
        Assert.Equal(["EquipmentSelectionApplying"], entry.AllowedEvents);

        var absent = Assert.Single(RemoteLoaderConfig.Parse(ConfigRow("loader.process", new Dictionary<string, object?> { ["command"] = "python" })).Entries);
        Assert.Null(absent.AllowedServices);
        Assert.Null(absent.AllowedEvents);
    }

    /// <summary>A loader.wasm entry accepts the same optional lists.</summary>
    [Fact]
    public void WasmEntryAcceptsTheSameAllowlists()
    {
        var module = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wasm");
        File.WriteAllBytes(module, []);
        try
        {
            var row = ConfigRow("loader.wasm", new Dictionary<string, object?>
            {
                ["module"] = module,
                ["allowed_services"] = new List<object?> { "IMessageRouter" },
            }, wasm: true);
            var entry = Assert.Single(RemoteLoaderConfig.Parse(row, wasm: true).Entries);
            Assert.Equal(["IMessageRouter"], entry.AllowedServices);
            Assert.Null(entry.AllowedEvents);
        }
        finally { File.Delete(module); }
    }

    /// <summary>Malformed lists are refused with the entry path, the list and every problem.</summary>
    [Fact]
    public void LoaderConfigRejectsMalformedAllowlists()
    {
        var malformed = Assert.Throws<ConfigException>(() => RemoteLoaderConfig.Parse(ConfigRow("loader.process", new Dictionary<string, object?>
        {
            ["command"] = "python",
            ["allowed_services"] = "IMessageRouter",
            ["allowed_events"] = new List<object?> { "EquipmentSelectionApplying", "", "EquipmentSelectionApplying" },
        })));
        Assert.Contains("allowed_services", malformed.Message);
        Assert.Contains("allowed_events", malformed.Message);
        Assert.Contains("twice", malformed.Message);

        var tooMany = Enumerable.Range(0, 257).Select(index => (object?)$"I{index}").ToList();
        var oversized = Assert.Throws<ConfigException>(() => RemoteLoaderConfig.Parse(ConfigRow("loader.process", new Dictionary<string, object?>
        {
            ["command"] = "python",
            ["allowed_services"] = tooMany,
        })));
        Assert.Contains("256-entry limit", oversized.Message);
    }

    /// <summary>A real loader row whose entry forbids the echo plugin's router dependency fails loudly.</summary>
    [PythonFact]
    public async Task LoaderEntryAllowlistRefusesAnUndeclaredService()
    {
        var entry = RemoteHarness.EchoExample();
        entry["allowed_services"] = new List<object?>();
        await using var harness = new RemoteHarness([entry]);

        var error = await Assert.ThrowsAnyAsync<Exception>(harness.StartAsync);
        var config = FindConfigException(error);
        Assert.Contains("example.echo-lobby", config.Message);
        Assert.Contains("IMessageRouter", config.Message);
        Assert.Contains("allowed_services", config.Message);
    }

    /// <summary>A real loader row inside its entry's lists publishes and activates the plugin.</summary>
    [PythonFact]
    public async Task LoaderEntryAllowlistAllowsDeclaredServices()
    {
        var entry = RemoteHarness.EchoExample();
        entry["allowed_services"] = new List<object?> { "IMessageRouter" };
        entry["allowed_events"] = new List<object?>();
        await using var harness = new RemoteHarness([entry]);
        await harness.StartAsync();

        await harness.AddRowAsync("echo", "example.echo-lobby",
            new Dictionary<string, object?> { ["protocol"] = TestDialect.Protocol, ["greeting"] = "hi" });
        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
        Assert.Null(await harness.ShutdownAsync());
    }

    private static PluginRow ConfigRow(string plugin, Dictionary<string, object?> entry, bool wasm = false) => new()
    {
        Id = "remote", Plugin = plugin, Source = "test",
        Config = new Dictionary<string, object?> { ["plugins"] = new List<object?> { entry } },
    };

    private static ConfigException FindConfigException(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException!)
            if (current is ConfigException config)
                return config;
        throw new Xunit.Sdk.XunitException("No ConfigException in the chain: " + error);
    }
}
