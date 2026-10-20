using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Contracts.Lobby;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Tests of the bounded line reader, definition parsing, loader config and session map without a child process.</summary>
public sealed class TransportUnitTests
{
    private static async Task<List<(string Text, bool Truncated)>> ReadAll(string input, int max, bool truncate)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var lines = new List<(string, bool)>();
        await foreach (var line in BoundedLineReader.ReadAsync(stream, max, truncate))
            lines.Add((Encoding.UTF8.GetString(line.Bytes.Span), line.Truncated));
        return lines;
    }

    /// <summary>Lines split on newline, a carriage return is dropped and the stream end is clean.</summary>
    [Fact]
    public async Task SplitsLinesAndStripsCarriageReturns()
    {
        var lines = await ReadAll("one\r\ntwo\n\nthree\n", 16, truncate: false);
        Assert.Equal(["one", "two", "", "three"], lines.Select(line => line.Text));
    }

    /// <summary>A line over the bound is an error, not silent truncation.</summary>
    [Fact]
    public async Task OversizeLineFailsLoud()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll("short\n" + new string('x', 11) + "\n", 10, truncate: false));
    }

    /// <summary>A line exactly at the bound is accepted.</summary>
    [Fact]
    public async Task LineAtTheBoundIsAccepted()
    {
        var lines = await ReadAll(new string('x', 10) + "\n", 10, truncate: false);
        Assert.Equal(10, Assert.Single(lines).Text.Length);
    }

    /// <summary>stderr mode cuts the excess and flags it, then keeps reading later lines.</summary>
    [Fact]
    public async Task TruncationModeCutsAndContinues()
    {
        var lines = await ReadAll(new string('y', 25) + "\nok\n", 10, truncate: true);
        Assert.Equal((new string('y', 10), true), lines[0]);
        Assert.Equal(("ok", false), lines[1]);
    }

    /// <summary>Output that ends without a newline is treated as a broken frame.</summary>
    [Fact]
    public async Task UnterminatedFinalLineFails()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll("done\npartial", 64, truncate: false));
    }

    /// <summary>A definition requesting an unknown host contract is rejected with plugin and field in the message.</summary>
    [Fact]
    public void DefinitionRejectsUnknownContracts()
    {
        var element = JsonSerializer.SerializeToElement(new { id = "p.one", version = "1.0.0", requires = new[] { "IFoo" } });
        var error = Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(element, "src"));
        Assert.Contains("p.one", error.Message);
        Assert.Contains("requires", error.Message);
        Assert.Contains("IFoo", error.Message);
    }

    /// <summary>provides must be empty in protocol v1.</summary>
    [Fact]
    public void DefinitionRejectsProvides()
    {
        var element = JsonSerializer.SerializeToElement(new { id = "p.one", version = "1.0.0", provides = new[] { "IMessageRouter" } });
        Assert.Contains("provides", Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(element, "src")).Message);
    }

    /// <summary>The declaration pins IMessageRouter, one sender per sends entry and one binding per handles entry.</summary>
    [Fact]
    public void DeclarationFollowsTheDefinition()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            id = "p.one", version = "1.0.0", supported_builds = new[] { "kr:812" },
            handles = new[] { "LoginRequested" }, sends = new[] { "LoginAccepted", "LoginRejected" },
        });
        var declaration = RemoteDefinition.Parse(element, "src").Declaration;
        Assert.Equal([typeof(IMessageRouter)], declaration.Requires);
        Assert.Equal(2, declaration.RequiresMany.Length);
        Assert.All(declaration.RequiresMany, type => Assert.Equal(typeof(IMessageSender<>), type.GetGenericTypeDefinition()));
        Assert.Single(declaration.OptionalMany);
        Assert.Equal(["kr:812"], declaration.SupportedBuilds);
    }

    /// <summary>Handler replacement targets are carried into the native declaration exactly as written.</summary>
    [Fact]
    public void DeclarationCarriesExplicitMessageOverrides()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            id = "p.override", version = "1.0.0",
            handles = new[] { "LoginRequested" },
            overrides = new[] { "base:BubbleFighter.Fantasy.Contracts.Lobby.LoginRequested" },
        });
        var declaration = RemoteDefinition.Parse(element, "src").Declaration;
        Assert.Equal(["base:BubbleFighter.Fantasy.Contracts.Lobby.LoginRequested"], declaration.Overrides);
    }

    /// <summary>Message middleware pins the same inbound binding as a native message route.</summary>
    [Fact]
    public void MiddlewareDeclarationPinsItsInboundBinding()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            id = "p.middleware", version = "1.0.0", middleware = new[] { "LoginRequested" },
        });
        var declaration = RemoteDefinition.Parse(element, "src", protocolVersion: 2).Declaration;
        Assert.Contains(typeof(IInboundMessageBinding<LoginRequested>), declaration.OptionalMany);
        Assert.Equal(["LoginRequested"], declaration.Middleware);
    }

    /// <summary>Config binding applies defaults, rejects unknown keys and checks types.</summary>
    [Fact]
    public void ConfigSchemaAppliesDefaultsAndRejectsBadValues()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            id = "p.one", version = "1.0.0",
            config = new Dictionary<string, object>
            {
                ["protocol"] = new { type = "string", required = true },
                ["greeting"] = new { type = "string", @default = "hello" },
                ["retries"] = new { type = "int", @default = 3 },
            },
        });
        var definition = RemoteDefinition.Parse(element, "src");
        var row = new PluginRow { Id = "r", Plugin = "p.one", Source = "t", Config = new Dictionary<string, object?> { ["protocol"] = "x/game" } };
        var bound = definition.BindConfig(row);
        Assert.Equal("hello", bound.GetProperty("greeting").GetString());
        Assert.Equal(3, bound.GetProperty("retries").GetInt32());

        var bad = row with { Config = new Dictionary<string, object?> { ["retries"] = "many", ["extra"] = 1 } };
        var error = Assert.Throws<ConfigException>(() => definition.BindConfig(bad));
        Assert.Contains("retries", error.Message);
        Assert.Contains("extra", error.Message);
        Assert.Contains("protocol", error.Message);
    }

    /// <summary>Loader config rejects unknown keys and out-of-range bounds, reporting all failures at once.</summary>
    [Fact]
    public void LoaderConfigReportsEveryProblem()
    {
        var row = new PluginRow
        {
            Id = "remote", Plugin = "loader.process", Source = "t",
            Config = new Dictionary<string, object?> { ["max_in_flight"] = 0, ["max_reentry_depth"] = 65, ["surprise"] = true, ["plugins"] = new List<object?>() },
        };
        var error = Assert.Throws<ConfigException>(() => RemoteLoaderConfig.Parse(row));
        Assert.Contains("max_in_flight", error.Message);
        Assert.Contains("max_reentry_depth", error.Message);
        Assert.Contains("surprise", error.Message);
        Assert.Contains("plugins", error.Message);
    }

    /// <summary>Documented defaults apply when nothing is configured.</summary>
    [Fact]
    public void LoaderConfigDefaultsMatchTheSpec()
    {
        var row = new PluginRow
        {
            Id = "remote", Plugin = "loader.process", Source = "t",
            Config = new Dictionary<string, object?>
            {
                ["plugins"] = new List<object?> { new Dictionary<string, object?> { ["command"] = "python" } },
            },
        };
        var config = RemoteLoaderConfig.Parse(row);
        Assert.Equal(1_048_576, config.Transport.MaxLineBytes);
        Assert.Equal(64, config.Transport.MaxInFlight);
        Assert.Equal(8, config.MaxReentryDepth);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), config.Transport.RequestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(3_000), config.Transport.ShutdownTimeout);
        Assert.Equal("process-0", Assert.Single(config.Entries).SourceId);
    }

    /// <summary>The map refuses new sessions at capacity and forgets a closed connection's sessions.</summary>
    [Fact]
    public void SessionMapIsBoundedAndForgetsClosedConnections()
    {
        var map = new SessionMap(1);
        var id = map.Add(new FakeSession(1));
        Assert.Equal(id, map.Add(new FakeSession(1)));
        Assert.Throws<InvalidOperationException>(() => map.Add(new FakeSession(2)));
        Assert.True(map.TryGet(id, out _));
        map.Clear();
        Assert.False(map.TryGet(id, out _));
    }
}
