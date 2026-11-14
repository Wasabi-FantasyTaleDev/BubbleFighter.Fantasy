using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Companion.Tests;

/// <summary>Server plugins ask a player's launcher for the game's own interface and get the player's outcome back.</summary>
public sealed class CompanionNativeUiTests
{
    private static readonly ClientUiRequest Box = new("messageBox", "Start anyway?")
    {
        Title = "Ban Pick", Icon = "question", Buttons = [new("confirm", "Start"), new("cancel", "Cancel")],
    };

    [Fact]
    public async Task AMessageBoxCompletesWithThePlayersAnswerFromTheirOwnLaunch()
    {
        var hub = new FakeHub(supported: true);
        using var ui = new CompanionNativeUi(hub, new(), TimeProvider.System, NullLogger.Instance);
        var player = new Session(7);
        var showing = ui.ShowAsync(player, Box, CancellationToken.None).AsTask();
        var sent = await hub.WaitAsync();
        Assert.Equal(CompanionNativeUiPlugin.InvokeType, sent.Type);
        var id = sent.Payload.GetProperty("operation_id").GetString()!;
        // Another player's launch cannot answer, and a progress report does not end a message box.
        await ui.OnResultAsync(Caller(new Session(8)), Result(id, "selected", "confirm", "native"), CancellationToken.None);
        await ui.OnResultAsync(Caller(player), Result(id, "shown", null, "native"), CancellationToken.None);
        Assert.False(showing.IsCompleted);
        await ui.OnResultAsync(Caller(player), Result(id, "selected", "confirm", "native"), CancellationToken.None);
        Assert.Equal(new ClientUiOutcome("selected", "confirm", "native"), await showing);
    }

    [Fact]
    public async Task ANotificationCompletesWhenItWasShown()
    {
        var hub = new FakeHub(supported: true);
        using var ui = new CompanionNativeUi(hub, new(), TimeProvider.System, NullLogger.Instance);
        var player = new Session(7);
        var showing = ui.ShowAsync(player, new("notification", "Server restarts in 5 minutes"), CancellationToken.None).AsTask();
        var id = (await hub.WaitAsync()).Payload.GetProperty("operation_id").GetString()!;
        await ui.OnResultAsync(Caller(player), Result(id, "shown", null, "overlay"), CancellationToken.None);
        Assert.Equal(new ClientUiOutcome("shown", null, "overlay"), await showing);
    }

    [Fact]
    public async Task APlayerWithoutANegotiatedLauncherIsUnavailableAtOnce()
    {
        using var ui = new CompanionNativeUi(new FakeHub(supported: false), new(), TimeProvider.System, NullLogger.Instance);
        Assert.Equal("unavailable", (await ui.ShowAsync(new Session(7), Box, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task AnUnansweredRequestTimesOut()
    {
        var hub = new FakeHub(supported: true);
        using var ui = new CompanionNativeUi(hub, new() { PresentTimeoutSeconds = 1 }, TimeProvider.System, NullLogger.Instance);
        Assert.Equal("timeout", (await ui.ShowAsync(new Session(7), new("notification", "hello"), CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("box-without-buttons")]
    [InlineData("chat-with-text")]
    public async Task AMalformedRequestIsTheCallersError(string shape)
    {
        using var ui = new CompanionNativeUi(new FakeHub(supported: true), new(), TimeProvider.System, NullLogger.Instance);
        ClientUiRequest request = shape switch
        {
            "popup" => new("popup", "x"),
            "box-without-buttons" => new("messageBox", "x"),
            _ => new("chat", "x") { Runs = [new("x", "#FFFFFF")] },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => ui.ShowAsync(new Session(7), request, CancellationToken.None).AsTask());
    }

    private static CompanionCaller Caller(ISession session) => new("account", "kr-812", "launch-" + session.Id, session, "en");

    private static JsonElement Result(string id, string status, string? button, string presenter) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["operation_id"] = id, ["status"] = status, ["button_id"] = button, ["presenter"] = presenter });

    private sealed class FakeHub(bool supported) : ICompanionHub
    {
        private readonly ConcurrentQueue<(string Type, JsonElement Payload)> _sent = new();
        public IDisposable Route(string type, int version, CompanionHandler handler) => throw new NotSupportedException();
        public IDisposable Offer(string type, int version) => throw new NotSupportedException();
        public bool Supports(ISession session, string type, int version) => supported;
        public bool Send(ISession session, string type, int version, object? payload)
        {
            _sent.Enqueue((type, JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })));
            return true;
        }
        public IReadOnlyList<ISession> BoundSessions() => [];
        internal async Task<(string Type, JsonElement Payload)> WaitAsync()
        {
            for (var i = 0; i < 200; i++)
            {
                if (_sent.TryDequeue(out var sent)) return sent;
                await Task.Delay(10);
            }
            throw new TimeoutException("Nothing was sent.");
        }
    }

    private sealed class Session(long id) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new GameConnection(id);
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "kr-812");
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
