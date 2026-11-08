using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Companion;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class TicketRedemptionTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task OnlySuccessfulPeerRedemptionPreparesItsExactCompanionLaunch()
    {
        var options = Options();
        options.Peers[0].CompanionUrl = "http://worker.example:8092/";
        var federation = new Federation();
        await using var authority = await TicketAuthority.StartAsync(options, new Tickets(), default, federation);
        using var http = new HttpClient { BaseAddress = new(authority.Addresses.Single()) };
        using var remote = new RemoteTickets(Client(authority.Addresses.Single(), "a", options.Peers[0].Key), http);
        var identity = await remote.RedeemIdentityAsync(Token, "cn-324", default);
        Assert.Equal(new string('f', 32), identity!.LaunchId);
        Assert.Equal(("player", "cn-324", new string('f', 32), "a", "http://worker.example:8092/"), Assert.Single(federation.Prepared));
        Assert.Null(await remote.RedeemIdentityAsync(Token, "cn-324", default));
        Assert.Single(federation.Prepared);
    }

    [Fact]
    public async Task CompanionFailureDoesNotInventABindingOrRejectAuthenticatedGameLogin()
    {
        var options = Options();
        options.Peers[0].CompanionUrl = "http://worker.example:8092/";
        var federation = new Federation { Fail = true };
        await using var authority = await TicketAuthority.StartAsync(options, new Tickets(), default, federation);
        using var http = new HttpClient { BaseAddress = new(authority.Addresses.Single()) };
        using var remote = new RemoteTickets(Client(authority.Addresses.Single(), "a", options.Peers[0].Key), http);
        var identity = await remote.RedeemIdentityAsync(Token, "cn-324", default);
        Assert.Equal("player", identity!.Username);
        Assert.Null(identity.LaunchId);
    }

    private sealed class Federation : ICompanionFederation
    {
        internal bool Fail;
        internal bool WaitForCancellation;
        internal List<(string Account, string Build, string Launch, string Node, string Origin)> Prepared { get; } = [];
        public async ValueTask<bool> RedirectAsync(string account, string build, string launchId, string nodeId, Uri origin, string key, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("Unreachable test node.");
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            Prepared.Add((account, build, launchId, nodeId, origin.AbsoluteUri));
            return true;
        }
        public IDisposable TrustAuthority(string key, IReadOnlyList<string> builds) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CompanionPreparationDeadlineStillReturnsTheAuthenticatedIdentity()
    {
        var options = Options();
        options.CompanionTimeoutMilliseconds = 100;
        options.Peers[0].CompanionUrl = "http://worker.example:8092/";
        var federation = new Federation { WaitForCancellation = true };
        await using var authority = await TicketAuthority.StartAsync(options, new Tickets(), default, federation);
        using var http = new HttpClient { BaseAddress = new(authority.Addresses.Single()) };
        using var remote = new RemoteTickets(Client(authority.Addresses.Single(), "a", options.Peers[0].Key), http);
        var identity = await remote.RedeemIdentityAsync(Token, "cn-324", default);
        Assert.Equal("player", identity!.Username);
        Assert.Null(identity.LaunchId);
        Assert.Empty(federation.Prepared);
    }

    [Fact]
    public void CompanionConfigurationRejectsNullOriginsAndAnExhaustedTicketDeadline()
    {
        var options = Options();
        options.Peers[0].CompanionUrl = null!;
        Assert.NotEmpty(options.Validate());
        options.Peers[0].CompanionUrl = "";
        options.CompanionTimeoutMilliseconds = options.TimeoutSeconds * 1000;
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public async Task AuthorityUsesTheAuthenticatedPeerIdentityForScopedRedemption()
    {
        var options = Options();
        var source = new ScopedTickets();
        await using var authority = await TicketAuthority.StartAsync(options, source, default);
        using var http = new HttpClient { BaseAddress = new(authority.Addresses.Single()) };
        using var first = new RemoteTickets(Client(authority.Addresses.Single(), "a", options.Peers[0].Key), http);
        using var second = new RemoteTickets(Client(authority.Addresses.Single(), "b", options.Peers[1].Key), http);
        Assert.Null(await first.RedeemIdentityAsync(Token, "cn-324", default));
        Assert.Equal("player", (await second.RedeemIdentityAsync(Token, "cn-324", default))?.Username);
        Assert.Null(await second.RedeemIdentityAsync(Token, "cn-324", default));
        Assert.Equal(new[] { "a", "b", "b" }, source.Nodes);
    }

    [Fact]
    public async Task TwoWorkersRaceOneAuthorityTicketWithoutDuplicatingIdentity()
    {
        var options = Options();
        var source = new Tickets();
        await using var authority = await TicketAuthority.StartAsync(options, source, default);
        using var http = new HttpClient { BaseAddress = new(authority.Addresses.Single()) };
        using var first = new RemoteTickets(Client(authority.Addresses.Single(), "a", options.Peers[0].Key), http);
        using var second = new RemoteTickets(Client(authority.Addresses.Single(), "b", options.Peers[1].Key), http);
        var results = await Task.WhenAll(first.RedeemIdentityAsync(Token, "cn-324", default).AsTask(),
            second.RedeemIdentityAsync(Token, "cn-324", default).AsTask());
        Assert.Equal(new LauncherTicketIdentity("player", "zh-CN", new string('f', 32)), Assert.Single(results, identity => identity is not null));
        Assert.Null(await first.RedeemIdentityAsync(Token, "cn-324", default));
        Assert.Throws<NotSupportedException>(() => first.Redeem(Token, "cn-324"));
    }

    [Fact]
    public async Task UnauthorizedOrMalformedRequestsNeverConsumeTheTicket()
    {
        var options = Options();
        var source = new Tickets();
        await using var authority = await TicketAuthority.StartAsync(options, source, default);
        using var http = new HttpClient { BaseAddress = new(authority.Addresses.Single()) };
        using var anonymous = await http.PostAsJsonAsync("/v1/nodes/a/tickets/redeem", new TicketRedemption(Token, "cn-324"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Peers[0].Key);
        using var foreign = await http.PostAsJsonAsync("/v1/nodes/a/tickets/redeem", new TicketRedemption(Token, "kr-1256"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var otherNode = await http.PostAsJsonAsync("/v1/nodes/b/tickets/redeem", new TicketRedemption(Token, "cn-324"));
        Assert.Equal(HttpStatusCode.Unauthorized, otherNode.StatusCode);
        foreach (var json in new[] { "{", "{\"ticket\":\"short\",\"build\":\"cn-324\"}",
            "{\"ticket\":\"" + Token + "\",\"build\":\"cn-324\",\"build\":\"cn-324\"}",
            "{\"ticket\":\"" + Token + "\",\"build\":\"cn-324\",\"username\":\"forged\"}" })
        {
            using var invalid = await http.PostAsync("/v1/nodes/a/tickets/redeem", new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var media = await http.PostAsync("/v1/nodes/a/tickets/redeem", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        Assert.Equal(0, source.Calls);
        using var valid = await http.PostAsJsonAsync("/v1/nodes/a/tickets/redeem", new TicketRedemption(Token, "cn-324"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task AuthorityFailureCannotFallBackToAnAccountOrRetryASpentTicket()
    {
        using var http = new HttpClient(new Failure()) { BaseAddress = new("https://authority.example") };
        using var remote = new RemoteTickets(Client("https://authority.example", "a", new string('a', 32)), http);
        Assert.Null(await remote.RedeemIdentityAsync(Token, "kr-1256", default));
        await Assert.ThrowsAsync<HttpRequestException>(() => remote.RedeemIdentityAsync(Token, "cn-324", default).AsTask());
    }

    [Theory]
    [InlineData("{\"username\":\"\"}")]
    [InlineData("{\"username\":\"player\",\"launchId\":\"invalid\"}")]
    [InlineData("{\"username\":\"player\",\"launcherLanguage\":\"invalid\"}")]
    public async Task InvalidAuthorityIdentityIsRejected(string json)
    {
        using var http = new HttpClient(new Response(json)) { BaseAddress = new("https://authority.example") };
        using var remote = new RemoteTickets(Client("https://authority.example", "a", new string('a', 32)), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => remote.RedeemIdentityAsync(Token, "cn-324", default).AsTask());
    }

    [Fact]
    public void TrustConfigurationAcceptsExplicitHttpOriginsButRejectsSharedPeerKeys()
    {
        Assert.Empty(Client("http://authority.example", "a", new string('a', 32)).Validate());
        Assert.Empty(Client("https://authority.example", "a", new string('a', 32)).Validate());
        foreach (var invalid in new[] { "ftp://authority.example", "http://user@authority.example", "http://authority.example/path", "http://authority.example?key=value" })
            Assert.NotEmpty(Client(invalid, "a", new string('a', 32)).Validate());
        var options = Options();
        Assert.Empty(options.Validate());
        options.Peers[1].Key = options.Peers[0].Key;
        Assert.NotEmpty(options.Validate());
    }

    private static TicketAuthorityOptions Options() => new() { Listen = "127.0.0.1:0", Peers =
        [new() { NodeId = "a", Key = new string('a', 32), Builds = ["cn-324"] },
         new() { NodeId = "b", Key = new string('b', 32), Builds = ["cn-324"] }] };
    private static TicketClientOptions Client(string url, string id, string key) =>
        new() { AuthorityUrl = url, NodeId = id, Key = key, Builds = ["cn-324"] };
    private sealed class ScopedTickets : ILauncherTicketService, ILauncherNodeTicketService
    {
        internal List<string> Nodes { get; } = [];
        private bool _used;
        public string? Redeem(string ticket, string build) => throw new InvalidOperationException("A scoped authority must supply its peer identity.");
        public ValueTask<LauncherTicketIdentity?> RedeemForNodeAsync(string ticket, string build, string nodeId, CancellationToken cancellationToken)
        {
            Nodes.Add(nodeId);
            if (nodeId != "b" || _used) return ValueTask.FromResult<LauncherTicketIdentity?>(null);
            _used = true;
            return ValueTask.FromResult<LauncherTicketIdentity?>(new("player"));
        }
    }

    private sealed class Tickets : ILauncherTicketService
    {
        private int _used;
        public int Calls;
        public string? Redeem(string ticket, string build) => throw new NotSupportedException();
        public ValueTask<LauncherTicketIdentity?> RedeemIdentityAsync(string ticket, string build, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult<LauncherTicketIdentity?>(ticket == Token && build == "cn-324" && Interlocked.Exchange(ref _used, 1) == 0
                ? new("player", "zh-CN", new string('f', 32)) : null);
        }
    }
    private sealed class Failure : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
    private sealed class Response(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
