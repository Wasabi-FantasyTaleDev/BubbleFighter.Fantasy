using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Launcher.Http;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherLaunchMetadataTests
{
    [Theory]
    [InlineData("release_version", "null")]
    [InlineData("release_version", "123")]
    [InlineData("release_version", "\"\"")]
    [InlineData("release_version", "\"line\\nnext\"")]
    [InlineData("patch_version", "false")]
    [InlineData("patch_version", "\"   \"")]
    [InlineData("patch_version", "\"patch\\u0000\"")]
    [InlineData("launcher_language", "null")]
    [InlineData("launcher_language", "\"EN\"")]
    [InlineData("native_language", "[]")]
    [InlineData("native_language", "\"ja\"")]
    [InlineData("client_language", "{}")]
    [InlineData("client_language", "\"en-US\"")]
    public async Task MalformedKnownMetadataIsRejectedBeforeTicketCapacityIsConsumed(string field, string json)
    {
        await using var fixture = await Fixture.Start();
        using var request = new StringContent("{\"build\":\"kr-812\",\"" + field + "\":" + json + "}", Encoding.UTF8, "application/json");
        using var invalid = await fixture.Http.PostAsync("v1/launch", request);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_request", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(fixture.Logger.Messages);
        // One ticket slot makes any mutation by the rejected request observable here.
        using var valid = await fixture.Http.PostAsJsonAsync("v1/launch", new { build = "kr-812" });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Theory]
    [InlineData("release_version")]
    [InlineData("patch_version")]
    public async Task OverlongDistributionLabelIsRejected(string field)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Http.PostAsJsonAsync("v1/launch", new Dictionary<string, object>
        {
            ["build"] = "kr-812", [field] = new string('x', 65),
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Logger.Messages);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("ko")]
    [InlineData("en")]
    [InlineData("id")]
    [InlineData("vi")]
    public async Task LanguageContextAndDescriptiveLabelsPreserveBuildBoundOneUseTickets(string language)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Http.PostAsJsonAsync("v1/launch", new
        {
            build = "kr-812", release_version = "2015.01", patch_version = new string('p', 64),
            launcher_language = language, native_language = "ko", client_language = "en",
            future_context = new { enabled = true },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
        Assert.Null(fixture.Sessions.Redeem(ticket, "kr-1256"));
        var identity = fixture.Sessions.RedeemIdentity(ticket, "kr-812");
        Assert.NotNull(identity);
        Assert.Equal("tester", identity.Username);
        Assert.Equal(language, identity.LauncherLanguage);
        Assert.Null(fixture.Sessions.Redeem(ticket, "kr-812"));
        var log = Assert.Single(fixture.Logger.Messages);
        Assert.Contains("2015.01", log);
        Assert.Contains(new string('p', 64), log);
        Assert.Contains(language + "/ko/en", log);
        Assert.DoesNotContain(ticket, log);
        Assert.DoesNotContain(fixture.AccessToken, log);
        Assert.DoesNotContain("tester", log);
    }

    [Fact]
    public async Task LegacyRequestStillIgnoresUnknownFieldsButRejectsDuplicateKeys()
    {
        await using var fixture = await Fixture.Start();
        using var duplicate = new StringContent("{\"build\":\"kr-812\",\"client_language\":\"en\",\"client_language\":\"en\"}", Encoding.UTF8, "application/json");
        using var invalid = await fixture.Http.PostAsync("v1/launch", duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var response = await fixture.Http.PostAsJsonAsync("v1/launch", new { build = "kr-812", future_field = new { data = 7 } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(fixture.Logger.Messages);
    }

    private sealed class Fixture(LauncherServer server, LauncherSessions sessions, string accessToken, RecordingLogger logger) : IAsyncDisposable
    {
        internal HttpClient Http { get; } = new() { BaseAddress = new Uri(server.Addresses.Single()), DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) } };
        internal LauncherSessions Sessions { get; } = sessions;
        internal string AccessToken { get; } = accessToken;
        internal RecordingLogger Logger { get; } = logger;

        internal static async Task<Fixture> Start()
        {
            var options = new LauncherOptions
            {
                Listen = "127.0.0.1:0", PasswordIterations = 100000, MaxSessions = 1,
                Instances = [new() { Id = "kr-812", Build = "kr-812", Port = 7110,
                    ReleaseVersion = "different-advertised-label", PatchVersion = "different-patch",
                    Name = [new() { Language = "en", Text = "Test" }] }],
            };
            var accounts = new MemoryLauncherAccounts(2);
            await accounts.RegisterAsync("tester", "", default);
            var sessions = new LauncherSessions(options, new MemoryLauncherSessions(2));
            var grant = await sessions.SignInAsync("tester", default);
            var logger = new RecordingLogger();
            var server = await LauncherServer.StartAsync(options, accounts, sessions, players: null, logger: logger);
            return new(server, sessions, grant.AccessToken, logger);
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await server.DisposeAsync(); }
    }

    private sealed class RecordingLogger : ILogger
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}
