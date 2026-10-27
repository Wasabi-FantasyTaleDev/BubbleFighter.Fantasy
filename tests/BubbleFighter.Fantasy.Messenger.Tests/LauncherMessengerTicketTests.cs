using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Messenger;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Messenger.Tests;

/// <summary>A launch returns a messenger ticket beside the game ticket; each is redeemed on its own path.</summary>
public sealed class LauncherMessengerTicketTests
{
    [Fact]
    public void AnEmptyPublicUrlSelectsTheDefaultListenerOnTheClientsHost()
    {
        var options = new MessengerTicketOptions { Tenant = "local", SigningKey = "ephemeral", PublicUrl = "" };
        Assert.Empty(options.Validate());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuer = new MessengerTicketIssuer(options, key, TimeProvider.System);
        Assert.Equal("http://{host}:8091", issuer.PublicUrl);
    }

    [Fact]
    public async Task LaunchCarriesAnIndependentMessengerTicket()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuer = new MessengerTicketIssuer(new MessengerTicketOptions
        {
            Tenant = "local", SigningKey = "ephemeral", PublicUrl = "http://{host}:8091",
            GameCodes = [new() { Build = "kr-812", Code = NexonGameCode.bubblefighter }],
        }, key, TimeProvider.System);
        var options = new LauncherOptions
        {
            Listen = "127.0.0.1:0", PasswordIterations = 100000, AllowLegacyPasswordEnrollment = true,
            Instances = [new() { Id = "kr-812", Build = "kr-812", Name = [new() { Language = "en", Text = "KR 2015" }], Port = 7110 }],
        };
        var accounts = new MemoryLauncherAccounts(10);
        Assert.True(await accounts.RegisterAsync("player1", "", default));
        var sessions = new LauncherSessions(options, new MemoryLauncherSessions(10));
        await using var server = await LauncherServer.StartAsync(options, accounts, sessions, players: null, messenger: issuer);
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };

        using var login = await http.PostAsJsonAsync("v1/auth/login", new { username = "player1", password = "Local-test-123" });
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        using var launch = new HttpRequestMessage(HttpMethod.Post, "v1/launch") { Content = JsonContent.Create(new { build = "kr-812" }) };
        launch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await http.SendAsync(launch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var messenger = body.GetProperty("messenger");
        Assert.Equal("http://127.0.0.1:8091", messenger.GetProperty("url").GetString());

        using var verify = ECDsa.Create();
        verify.ImportSubjectPublicKeyInfo(issuer.PublicKey.Span, out _);
        var service = new MessengerService(new MessengerOptions(), new MemoryMessengerStore(8), new Dictionary<string, ECDsa> { ["local"] = verify });
        var session = await service.LoginAuthAsync(messenger.GetProperty("ticket").GetString()!, null, default);
        Assert.Equal(("local", "player1", MessengerFixture.BubbleFighterKr), (session!.User.Tenant, session.User.Subject, session.User.GameCode));
        Assert.Equal("player1", sessions.Redeem(body.GetProperty("ticket").GetString()!, "kr-812"));
    }
}
