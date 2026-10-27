using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Admin.Http;
using BubbleFighter.Fantasy.Contracts.Listeners;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

/// <summary>Runs the real admin row and the web interface row in a kernel host and reads them over HTTP.</summary>
public sealed partial class AdminHttpEndpointTests : IAsyncLifetime
{
    private PluginHost _host = null!;
    private HttpClient _http = null!;

    [Plugin("test.builds", Requires = [typeof(IClientProfileRegistry)], ProvidesMany = [typeof(IListenerEndpoint)])]
    public sealed class TestBuilds : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var profiles = context.Services.Get<IClientProfileRegistry>();
            context.Effect(profiles.Register(new ClientProfile("kr-1256", "kr", 1256, 0, 0, "Korea 2015")));
            context.Effect(profiles.Register(new ClientProfile("cn-774", "cn", 774, 0, 0, "China 2014")));
            context.Effect(context.Services.RegisterMany<IListenerEndpoint>(new Endpoint("g1", "kr-1256/game", 7100)));
            context.Effect(context.Services.RegisterMany<IListenerEndpoint>(new Endpoint("r1", "kr-1256/relay", 7101)));
            return ValueTask.CompletedTask;
        }

        private sealed record Endpoint(string RowId, string Protocol, int Port) : IListenerEndpoint
        {
            public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, Port);
        }
    }

    public async Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = id, Plugin = plugin, Source = "test", Config = config };
        var catalog = PluginCatalog.ScanAssemblies([
            typeof(PluginHost).Assembly, typeof(AdminHttpPlugin).Assembly, typeof(WebUiPlugin).Assembly, typeof(AdminHttpEndpointTests).Assembly]);
        _host = new PluginHost([
            Row("core.session-registry", "core.session-registry"),
            Row("core.client-profiles", "core.client-profiles"),
            Row("test.builds", "test.builds"),
            Row("webui", "webui", new()
            {
                ["releases"] = new List<object?> { new Dictionary<string, object?> { ["build"] = "kr-1256", ["release"] = "KR final" } },
            }),
            Row("admin.http", "admin.http", new() { ["listen"] = $"127.0.0.1:{port}", ["log_buffer"] = 32 }),
        ], catalog, NullLoggerFactory.Instance);
        await _host.StartAsync(CancellationToken.None);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IndexIsServedAtTheRootWithoutCaching()
    {
        var response = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Contains("id=\"app\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BuildAssetsAreServedWithTheirTypeAndLongCaching()
    {
        var index = await _http.GetStringAsync("/index.html");
        var script = AssetReference().Match(index);
        Assert.True(script.Success, "index.html references no built asset");
        var response = await _http.GetAsync("/" + script.Value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/%2e%2e/%2e%2e/appsettings.json")]
    [InlineData("/assets/..%2f..%2findex.html")]
    [InlineData("/assets/%5c..%5cindex.html")]
    [InlineData("/missing.js")]
    [InlineData("/api/nothing")]
    public async Task TraversalAndUnknownPathsNeverReturnContent(string path)
    {
        var response = await _http.GetAsync(path);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OverviewReportsBuildsPortsAndRows()
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("/api/overview"));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("startedUtc").GetDateTime() <= DateTime.UtcNow);
        Assert.Equal(0, root.GetProperty("players").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rooms").ValueKind);
        var rows = root.GetProperty("rows");
        Assert.True(rows.GetProperty("total").GetInt32() >= 5);
        Assert.Equal(0, rows.GetProperty("failed").GetInt32());
        Assert.Equal(rows.GetProperty("total").GetInt32(), rows.GetProperty("active").GetInt32());
        Assert.Equal(0, rows.GetProperty("disabled").GetInt32());

        var builds = root.GetProperty("builds").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetString()!);
        var kr = builds["kr-1256"];
        Assert.Equal("kr", kr.GetProperty("region").GetString());
        Assert.Equal("Korea 2015", kr.GetProperty("description").GetString());
        Assert.Equal("KR final", kr.GetProperty("release").GetString());
        Assert.Equal(1256, kr.GetProperty("clientVersionMajor").GetInt32());
        Assert.Equal(7100, kr.GetProperty("gamePort").GetInt32());
        Assert.Equal(7101, kr.GetProperty("relayPort").GetInt32());
        Assert.True(kr.GetProperty("listening").GetBoolean());
        Assert.Equal(0, kr.GetProperty("players").GetInt32());
        var cn = builds["cn-774"];
        Assert.Equal(JsonValueKind.Null, cn.GetProperty("release").ValueKind);
        Assert.Equal(JsonValueKind.Null, cn.GetProperty("gamePort").ValueKind);
        Assert.Equal(JsonValueKind.Null, cn.GetProperty("relayPort").ValueKind);
        Assert.False(cn.GetProperty("listening").GetBoolean());
    }

    [Fact]
    public async Task SettingsReadAsPendingAndRefuseWrites()
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("/api/settings"));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("crossVersionPlay").ValueKind);

        var response = await _http.PutAsJsonAsync("/api/settings", new { crossVersionPlay = true });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("not supported", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task LogsPageForwardByCursor()
    {
        using var first = JsonDocument.Parse(await _http.GetStringAsync("/api/logs"));
        var entries = first.RootElement.GetProperty("entries");
        Assert.True(entries.GetArrayLength() >= 1);
        var entry = entries[0];
        Assert.True(entry.GetProperty("seq").GetInt64() >= 1);
        Assert.Contains(entry.GetProperty("level").GetString(), new[] { "debug", "info", "warn", "error" });
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("category").GetString()));
        Assert.True(entry.GetProperty("utc").GetDateTime() <= DateTime.UtcNow);
        Assert.Contains(entries.EnumerateArray(), e => e.GetProperty("message").GetString()!.StartsWith("Admin API listening", StringComparison.Ordinal));

        var next = first.RootElement.GetProperty("next").GetInt64();
        using var again = JsonDocument.Parse(await _http.GetStringAsync($"/api/logs?after={next}"));
        // The first request itself is logged by the server, so lines may follow; none may repeat.
        Assert.All(again.RootElement.GetProperty("entries").EnumerateArray(), e => Assert.True(e.GetProperty("seq").GetInt64() > next));
        Assert.True(again.RootElement.GetProperty("next").GetInt64() >= next);
    }

    [Fact]
    public async Task PluginStatesTravelAsNamesAndSessionsStartEmpty()
    {
        using var plugins = JsonDocument.Parse(await _http.GetStringAsync("/api/plugins"));
        var admin = plugins.RootElement.EnumerateArray().Single(p => p.GetProperty("rowId").GetString() == "admin.http");
        Assert.Equal("Active", admin.GetProperty("state").GetString());
        Assert.Equal("admin.http", admin.GetProperty("plugin").GetString());

        var sessions = await _http.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        Assert.Equal("[]", (await sessions.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task OperationalControlsRequireAnAdministrationIdentity()
    {
        using var health = JsonDocument.Parse(await _http.GetStringAsync("/api/health"));
        Assert.True(health.RootElement.GetProperty("startedUtc").GetDateTime() <= DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.GetAsync("/api/maintenance")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.GetAsync("/api/rooms/kr-1256/1")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.PostAsync("/api/plugins/test/load", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.PostAsync("/api/plugins/test/unload", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.PostAsync("/api/plugins/test/disable", content: null)).StatusCode);
    }

    [GeneratedRegex(@"assets/[^""']+\.js")]
    private static partial Regex AssetReference();
}
