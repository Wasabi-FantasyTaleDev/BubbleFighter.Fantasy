using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Companion;
using BubbleFighter.Fantasy.Contracts.Launcher;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipelines;
using Xunit;

namespace BubbleFighter.Fantasy.Companion.Tests;

/// <summary>Drives the companion listener over a real loopback WebSocket the way the launcher does.</summary>
public sealed class CompanionLoopbackTests : IAsyncLifetime
{
    private readonly ManualClock _clock = new();
    private CompanionOptions _options = null!;
    private CompanionLaunches _launches = null!;
    private CompanionHub _hub = null!;
    private CompanionServer _server = null!;
    private Uri _endpoint = null!;

    public async Task InitializeAsync() => await StartAsync(new CompanionOptions { Listen = "127.0.0.1:0" });

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task StartAsync(CompanionOptions options)
    {
        _options = options;
        _launches = new CompanionLaunches(options, _clock, NullLogger.Instance);
        _hub = new CompanionHub(_launches);
        _server = await CompanionServer.StartAsync(options, _launches, _hub, NullLogger.Instance, CancellationToken.None);
        var address = new Uri(_server.Addresses.First());
        _endpoint = new Uri($"ws://127.0.0.1:{address.Port}{CompanionServer.Path}");
    }

    [Fact]
    public async Task ALaunchNegotiatesBindsAndCarriesEventsRequestsAndHeartbeats()
    {
        using var show = _hub.Offer("ui.notification.show", 1);
        using var echo = _hub.Route("test.echo", 2, (caller, payload, _) =>
            ValueTask.FromResult<object?>(new { Account = caller.Account, Locale = caller.Locale, Value = payload.GetProperty("value").GetString() }));
        var grant = _launches.Open("alice", "kr-812");
        // Port zero only binds a free test port; a deployment hands out its fixed listen port.
        Assert.Equal("ws://{host}:0/companion/v1", grant.Url);

        await using var client = await Client.ConnectAsync(_endpoint, grant.Ticket);
        var welcome = await client.HelloAsync("kr-812", grant.LaunchId, new() { ["ui.notification.show"] = 1, ["test.echo"] = 2, ["test.unknown"] = 1 });
        Assert.Equal("welcome", welcome.GetProperty("kind").GetString());
        var payload = welcome.GetProperty("payload");
        Assert.Equal(1, payload.GetProperty("protocol").GetInt32());
        Assert.Equal("kr-812", payload.GetProperty("build").GetString());
        Assert.Equal(grant.LaunchId, payload.GetProperty("launch_id").GetString());
        Assert.Equal("pending_game", payload.GetProperty("state").GetString());
        Assert.Equal(["test.echo", "ui.notification.show"], payload.GetProperty("capabilities").EnumerateObject().Select(p => p.Name).Order());
        var epoch = welcome.GetProperty("epoch").GetString()!;

        var session = new Session(7, "kr-812");
        Assert.False(_hub.Send(session, "ui.notification.show", 1, new { Title = "t", Text = "x" }));
        _launches.Bind(new LauncherLaunchRedeemed(grant.LaunchId, "alice", "kr-812", session));
        var state = await client.ReceiveAsync();
        Assert.Equal("state", state.GetProperty("kind").GetString());
        Assert.Equal("connected", state.GetProperty("payload").GetProperty("state").GetString());
        Assert.Equal(epoch, state.GetProperty("epoch").GetString());
        Assert.Equal([session], _hub.BoundSessions());

        await client.SendAsync(new { kind = "heartbeat", id = "hb1", type = "companion.heartbeat", version = 1, payload = (object?)null, epoch });
        var ack = await client.ReceiveAsync();
        Assert.Equal("heartbeat_ack", ack.GetProperty("kind").GetString());
        Assert.Equal("hb1", ack.GetProperty("reply_to").GetString());

        Assert.True(_hub.Supports(session, "ui.notification.show", 1));
        Assert.False(_hub.Supports(session, "ui.notification.show", 2));
        Assert.True(_hub.Send(session, "ui.notification.show", 1, new { Title = "Server notice", Text = "Hello" }));
        var shown = await client.ReceiveAsync();
        Assert.Equal("event", shown.GetProperty("kind").GetString());
        Assert.Equal("Hello", shown.GetProperty("payload").GetProperty("text").GetString());
        Assert.Equal(epoch, shown.GetProperty("epoch").GetString());

        await client.SendAsync(new { kind = "request", id = "r1", type = "test.echo", version = 2, payload = new { value = "ping" }, epoch });
        var reply = await client.ReceiveAsync();
        Assert.Equal("response", reply.GetProperty("kind").GetString());
        Assert.Equal("test.echo", reply.GetProperty("type").GetString());
        Assert.Equal(2, reply.GetProperty("version").GetInt32());
        Assert.Equal("r1", reply.GetProperty("reply_to").GetString());
        Assert.Equal("alice", reply.GetProperty("payload").GetProperty("account").GetString());
        Assert.Equal("en", reply.GetProperty("payload").GetProperty("locale").GetString());
        Assert.Equal("ping", reply.GetProperty("payload").GetProperty("value").GetString());

        // The game connection closing ends the launch and its companion connection.
        _launches.End(session.Connection);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await client.ReceiveCloseAsync());
        Assert.Equal(0, _launches.Count);
        Assert.Null(_launches.Renew("alice", grant.LaunchId));
    }

    [Fact]
    public async Task ALaunchBoundBeforeTheLauncherConnectsIsWelcomedAsConnected()
    {
        var grant = _launches.Open("alice", "cn-324");
        _launches.Bind(new LauncherLaunchRedeemed(grant.LaunchId, "alice", "cn-324", new Session(3, "cn-324")));
        await using var client = await Client.ConnectAsync(_endpoint, grant.Ticket);
        var welcome = await client.HelloAsync("cn-324", grant.LaunchId, []);
        Assert.Equal("connected", welcome.GetProperty("payload").GetProperty("state").GetString());
        Assert.Empty(welcome.GetProperty("payload").GetProperty("capabilities").EnumerateObject());
    }

    [Fact]
    public async Task TicketsAreOneTimeAndAProtocolMismatchKeepsTheTicket()
    {
        var grant = _launches.Open("alice", "kr-812");
        Assert.Equal(HttpStatusCode.UpgradeRequired, await Client.RefusedStatusAsync(_endpoint, grant.Ticket, "other.v1"));
        await using (var client = await Client.ConnectAsync(_endpoint, grant.Ticket))
            await client.HelloAsync("kr-812", grant.LaunchId, []);
        Assert.Equal(HttpStatusCode.Unauthorized, await Client.RefusedStatusAsync(_endpoint, grant.Ticket, CompanionWire.Subprotocol));
        Assert.Equal(HttpStatusCode.Unauthorized, await Client.RefusedStatusAsync(_endpoint, "not-a-ticket", CompanionWire.Subprotocol));
    }

    [Fact]
    public async Task RenewalBelongsToTheLaunchOwnerAndReplacesTheTicketAndConnection()
    {
        var grant = _launches.Open("alice", "kr-812");
        Assert.Null(_launches.Renew("mallory", grant.LaunchId));
        Assert.Null(_launches.Renew("alice", "missing"));
        await using var first = await Client.ConnectAsync(_endpoint, grant.Ticket);
        await first.HelloAsync("kr-812", grant.LaunchId, []);

        var renewed = _launches.Renew("alice", grant.LaunchId)!;
        Assert.Equal(grant.LaunchId, renewed.LaunchId);
        Assert.NotEqual(grant.Ticket, renewed.Ticket);
        await using var second = await Client.ConnectAsync(_endpoint, renewed.Ticket);
        await second.HelloAsync("kr-812", grant.LaunchId, []);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await first.ReceiveCloseAsync());
        Assert.Equal("companion.replaced", first.CloseDescription);
    }

    [Fact]
    public async Task AHelloForAnotherLaunchOrBuildIsRefused()
    {
        var grant = _launches.Open("alice", "kr-812");
        await using var client = await Client.ConnectAsync(_endpoint, grant.Ticket);
        await client.SendAsync(Client.Hello("kr-812", "another-launch", []));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await client.ReceiveCloseAsync());
        Assert.Equal("companion.identity-mismatch", client.CloseDescription);

        var other = _launches.Open("alice", "kr-812");
        await using var wrongBuild = await Client.ConnectAsync(_endpoint, other.Ticket);
        await wrongBuild.SendAsync(Client.Hello("kr-1256", other.LaunchId, []));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await wrongBuild.ReceiveCloseAsync());
        Assert.Equal("companion.identity-mismatch", wrongBuild.CloseDescription);
    }

    [Theory]
    [InlineData("""{"kind":"event","id":"e1","type":"test.echo","version":2,"payload":null,"epoch":"EPOCH"}""", "companion.not-bound")]
    [InlineData("""{"kind":"heartbeat","id":"h1","type":"companion.heartbeat","version":1,"payload":null,"epoch":"stale"}""", "companion.stale-epoch")]
    [InlineData("""{"kind":"heartbeat","id":"h1","type":"companion.heartbeat","version":1,"payload":null,"epoch":"EPOCH","extra":1}""", "companion.invalid-envelope")]
    [InlineData("""{"kind":"heartbeat","id":"h1","id":"h2","type":"companion.heartbeat","version":1,"payload":null,"epoch":"EPOCH"}""", "companion.duplicate-property")]
    [InlineData("""{"kind":"response","id":"x1","type":"test.echo","version":2,"payload":null,"reply_to":"r","epoch":"EPOCH"}""", "companion.unexpected-kind")]
    public async Task ProtocolViolationsCloseTheConnectionWithAReason(string message, string reason)
    {
        using var echo = _hub.Route("test.echo", 2, (_, _, _) => ValueTask.FromResult<object?>(null));
        var grant = _launches.Open("alice", "kr-812");
        await using var client = await Client.ConnectAsync(_endpoint, grant.Ticket);
        var welcome = await client.HelloAsync("kr-812", grant.LaunchId, new() { ["test.echo"] = 2 });
        await client.SendRawAsync(message.Replace("EPOCH", welcome.GetProperty("epoch").GetString(), StringComparison.Ordinal));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await client.ReceiveCloseAsync());
        Assert.Equal(reason, client.CloseDescription);
    }

    [Fact]
    public async Task RepeatedMessageIdsCloseTheConnection()
    {
        var grant = _launches.Open("alice", "kr-812");
        await using var client = await Client.ConnectAsync(_endpoint, grant.Ticket);
        var epoch = (await client.HelloAsync("kr-812", grant.LaunchId, [])).GetProperty("epoch").GetString();
        var heartbeat = new { kind = "heartbeat", id = "same", type = "companion.heartbeat", version = 1, payload = (object?)null, epoch };
        await client.SendAsync(heartbeat);
        await client.ReceiveAsync();
        await client.SendAsync(heartbeat);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await client.ReceiveCloseAsync());
        Assert.Equal("companion.duplicate-id", client.CloseDescription);
    }

    [Fact]
    public async Task UnboundLaunchesExpireAndTheTableIsBounded()
    {
        await _server.DisposeAsync();
        await StartAsync(new CompanionOptions { Listen = "127.0.0.1:0", MaxLaunches = 2, PendingSeconds = 60, TicketSeconds = 30 });
        var stale = _launches.Open("alice", "kr-812");
        var bound = _launches.Open("bob", "kr-812");
        _launches.Bind(new LauncherLaunchRedeemed(bound.LaunchId, "bob", "kr-812", new Session(9, "kr-812")));
        Assert.Throws<InvalidOperationException>(() => _launches.Open("carol", "kr-812"));

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.Unauthorized, await Client.RefusedStatusAsync(_endpoint, stale.Ticket, CompanionWire.Subprotocol));
        _clock.Advance(TimeSpan.FromSeconds(30));
        _launches.Sweep();
        Assert.Null(_launches.Renew("alice", stale.LaunchId));
        Assert.NotNull(_launches.Renew("bob", bound.LaunchId));
        _launches.Open("carol", "kr-812");
    }

    [Fact]
    public void ABindingByAnotherAccountIsRefused()
    {
        var grant = _launches.Open("alice", "kr-812");
        var session = new Session(1, "kr-812");
        _launches.Bind(new LauncherLaunchRedeemed(grant.LaunchId, "mallory", "kr-812", session));
        _launches.End(session.Connection);
        Assert.NotNull(_launches.Renew("alice", grant.LaunchId));
    }

    [Fact]
    public void ServerRoutesCannotClaimChannelControlsOrDuplicateAHandler()
    {
        Assert.Throws<ArgumentException>(() => _hub.Route("companion.hello", 1, (_, _, _) => ValueTask.FromResult<object?>(null)));
        Assert.Throws<ArgumentException>(() => _hub.Offer("Upper.case", 1));
        using var first = _hub.Route("test.echo", 1, (_, _, _) => ValueTask.FromResult<object?>(null));
        Assert.Throws<InvalidOperationException>(() => _hub.Route("test.echo", 1, (_, _, _) => ValueTask.FromResult<object?>(null)));
        first.Dispose();
        using var again = _hub.Route("test.echo", 1, (_, _, _) => ValueTask.FromResult<object?>(null));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private Client(ClientWebSocket socket) => _socket = socket;
        public string? CloseDescription => _socket.CloseStatusDescription;

        public static async Task<Client> ConnectAsync(Uri endpoint, string ticket)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(CompanionWire.Subprotocol);
            socket.Options.SetRequestHeader("Authorization", "Bearer " + ticket);
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(endpoint, timeout.Token);
            Assert.Equal(CompanionWire.Subprotocol, socket.SubProtocol);
            return new Client(socket);
        }

        public static async Task<HttpStatusCode> RefusedStatusAsync(Uri endpoint, string ticket, string subprotocol)
        {
            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(subprotocol);
            socket.Options.SetRequestHeader("Authorization", "Bearer " + ticket);
            socket.Options.CollectHttpResponseDetails = true;
            await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(endpoint, CancellationToken.None));
            return socket.HttpStatusCode;
        }

        public static object Hello(string build, string launchId, Dictionary<string, int> capabilities) => new
        {
            kind = "hello", id = Guid.NewGuid().ToString("N"), type = "companion.hello", version = 1,
            payload = new { protocol = 1, game = "bubblefighter", build, launch_id = launchId, locale = "en", capabilities },
        };

        public async Task<JsonElement> HelloAsync(string build, string launchId, Dictionary<string, int> capabilities)
        {
            await SendAsync(Hello(build, launchId, capabilities));
            return await ReceiveAsync();
        }

        public Task SendAsync(object message) => SendRawAsync(JsonSerializer.Serialize(message));

        public async Task SendRawAsync(string json) =>
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonElement> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[65536];
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await _socket.ReceiveAsync(buffer, timeout.Token);
                Assert.Equal(WebSocketMessageType.Text, part.MessageType);
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            return JsonDocument.Parse(message.ToArray()).RootElement.Clone();
        }

        public async Task<WebSocketCloseStatus?> ReceiveCloseAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[65536];
            try
            {
                while ((await _socket.ReceiveAsync(buffer, timeout.Token)).MessageType != WebSocketMessageType.Close) { }
            }
            catch (WebSocketException) { return null; }
            return _socket.CloseStatus;
        }

        public ValueTask DisposeAsync()
        {
            _socket.Abort();
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session(long id, string build) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new GameConnection(id);
        public ClientProfile Profile { get; } = new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class GameConnection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
