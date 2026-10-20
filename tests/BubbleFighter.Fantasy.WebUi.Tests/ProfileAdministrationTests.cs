using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Admin.Http;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

/// <summary>
/// A server composed from a profile (<c>run --profile</c>) has no installation and therefore no administrator
/// accounts. Its administration listener is reachable on loopback only and needs no sign-in; anything else is
/// refused at activation, and the retired bearer token is an unknown key.
/// </summary>
public sealed class ProfileAdministrationTests
{
    [Fact]
    public async Task LoopbackAdministrationWithoutAnInstallationNeedsNoSignIn()
    {
        var port = FreePort();
        await using var host = await StartAsync(new() { ["listen"] = $"127.0.0.1:{port}" });
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var status = await http.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.Equal("none", status.GetProperty("mode").GetString());
        Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/overview")).StatusCode);
        var index = await http.GetStringAsync("/index.html");
        var asset = System.Text.RegularExpressions.Regex.Match(index, @"assets/[\w.-]+\.js");
        Assert.True(asset.Success);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/" + asset.Value)).StatusCode);
    }

    [Fact]
    public async Task RemoteAdministrationWithoutAnInstallationIsRefused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(new() { ["listen"] = $"0.0.0.0:{FreePort()}" }));
        Assert.Contains("listens on loopback only", Flatten(error));
    }

    [Fact]
    public async Task TheRetiredBearerTokenIsAnUnknownKey()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            StartAsync(new() { ["listen"] = $"127.0.0.1:{FreePort()}", ["bearer_token"] = new string('t', 40) }));
        Assert.Contains("bearer_token", Flatten(error));
    }

    private static async Task<Started> StartAsync(Dictionary<string, object?> admin)
    {
        PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = id, Plugin = plugin, Source = "test", Config = config };
        var catalog = PluginCatalog.ScanAssemblies([typeof(PluginHost).Assembly, typeof(AdminHttpPlugin).Assembly, typeof(WebUiPlugin).Assembly]);
        var host = new PluginHost([
            Row("core.session-registry", "core.session-registry"),
            Row("core.client-profiles", "core.client-profiles"),
            Row("webui", "webui"),
            Row("admin.http", "admin.http", admin),
        ], catalog, NullLoggerFactory.Instance);
        try { await host.StartAsync(CancellationToken.None); }
        catch { await host.ShutdownAsync(CancellationToken.None); throw; }
        return new Started(host);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Flatten(Exception error)
    {
        var text = "";
        for (Exception? current = error; current is not null; current = current.InnerException) text += current.Message + "\n";
        return text;
    }

    private sealed class Started(PluginHost host) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => host.ShutdownAsync(CancellationToken.None);
    }
}
