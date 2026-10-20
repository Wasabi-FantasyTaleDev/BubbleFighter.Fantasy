using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins where the legacy login codec raises <see cref="LoginMenuInstalled"/>: once, after the whole login sequence
/// (kr-812 sub_828B10 stores the new-gift flag in the task bar, which exists only once the menu is installed), with the codec's protocol and the
/// connection that logged in.
/// </summary>
public sealed class PresentLoginEventTests
{
    [Fact]
    public async Task TheEventIsRaisedOnceAfterTheLastLoginFrameWithTheProtocolSessionAndLogin()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var room = services.RegisterMany<IMessageSender<MainMenuRoomOpened>>(new MenuSender());
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var inventory = new InventoryBinding("kr-812/game", TestBuilds.Opcodes("kr-812"),
            TestBuilds.Packets, "kr-812", scope);
        var binding = new LoginBinding("kr-812/game", LoginValues.From("kr-812", TestBuilds.Packets.ValuesOf("kr-812")),
            3, TestBuilds.Opcodes("kr-812"), TestBuilds.Packets, "kr-812", services, events, inventory, null,
            NullLogger.Instance);
        var session = new RecordingSession();
        var seen = new List<(LoginMenuInstalled Event, int FramesSent)>();
        using var subscription = events.OnAsync<LoginMenuInstalled>((e, _) =>
        {
            seen.Add((e, session.Sent.Count));
            return ValueTask.CompletedTask;
        });

        var login = new LoginSucceeded(42, "account", "ab", 17, new DateTime(2026, 10, 7), 300001, [300001],
            new PlayerWallet(42, 12345, 678), [], []);
        await binding.SendAsync(session, login, default);

        var (raised, frames) = Assert.Single(seen);
        Assert.Equal("kr-812/game", raised.Protocol);
        Assert.Same(session, raised.Session);
        Assert.Same(login, raised.Login);
        // The menu frame (100) is the last login frame; the event follows it, so the gift icon is never lost to a
        // client that has not built its task bar yet.
        Assert.Equal(session.Sent.Count, frames);
        Assert.Equal((ushort)100, session.Sent[^1].Opcode);
    }

    [Fact]
    public async Task ARejectedLoginRaisesNothing()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var inventory = new InventoryBinding("kr-812/game", TestBuilds.Opcodes("kr-812"),
            TestBuilds.Packets, "kr-812", scope);
        var binding = new LoginBinding("kr-812/game", LoginValues.From("kr-812", TestBuilds.Packets.ValuesOf("kr-812")),
            3, TestBuilds.Opcodes("kr-812"), TestBuilds.Packets, "kr-812", services, events, inventory, null,
            NullLogger.Instance);
        var raised = 0;
        using var subscription = events.OnAsync<LoginMenuInstalled>((_, _) =>
        {
            raised++;
            return ValueTask.CompletedTask;
        });
        await binding.SendAsync(new RecordingSession(), new LoginRejected(LoginRejection.AccountNotFound), default);
        Assert.Equal(0, raised);
    }

    private sealed class MenuSender : IMessageSender<MainMenuRoomOpened>
    {
        public string Protocol => "kr-812/game";
        public ValueTask SendAsync(ISession session, MainMenuRoomOpened message, CancellationToken ct) =>
            session.SendAsync(100, ReadOnlyMemory<byte>.Empty, ct);
    }

    private sealed class RecordingSession : ISession
    {
        public long Id => 1;
        public IConnection Connection { get; } = new StubConnection();
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 458, 0, "kr-812");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubConnection : IConnection
    {
        public long Id => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
