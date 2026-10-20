using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Verifies that login publishes loaded state before equipment and menu entry without a shop.</summary>
public sealed class LoginStateTests
{
    [Theory]
    [InlineData("kr-812", 384)]
    [InlineData("cn-717", 384)]
    [InlineData("id-400", 344)]
    [InlineData("id-1809", 348)]
    [InlineData("cn-706", 344)]
    [InlineData("cn-406", 272)]
    [InlineData("cn-324", 260)]
    [InlineData("cn-279", 172)]
    public async Task LoadedStatePrecedesInventoryEquipmentAndMenu(string build, int statLength)
    {
        var session = new RecordingSession();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var menu = new MenuSender(build);
        using var room = services.RegisterMany<IMessageSender<MainMenuRoomOpened>>(menu);
        using var equipment = events.Intercept<LoginEquipmentRequested, bool>(async (request, next, ct) =>
        {
            await request.Session.SendAsync(805, ReadOnlyMemory<byte>.Empty, ct);
            return await next(ct);
        });
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var inventory = new InventoryBinding(build + "/game", TestBuilds.Opcodes(build),
            TestBuilds.Packets, build, scope);
        var binding = Binding(build, services, events, inventory);
        await binding.SendAsync(session, Login() with { ColorIndex = 14 }, default);
        Assert.Equal(new ushort[] { 3, 105, 801, 805, 100 }, session.Sent.Select(frame => frame.Opcode));
        var stat = session.Sent[1].Payload;
        Assert.Equal(statLength, stat.Length);
        Assert.Equal(14, BinaryPrimitives.ReadInt32LittleEndian(stat));
        Assert.Equal((ushort)14, Assert.Single(menu.Opened).ColorIndex);
        Assert.Equal(12345, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(16)));
        Assert.Equal(678, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(20)));
        // CN279 omits the three counters/string preceding FighterInfo in later builds.
        var nicknameOffset = build == "cn-279" ? 32 : 44;
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(nicknameOffset)));
        Assert.Equal("ab", System.Text.Encoding.Unicode.GetString(stat, nicknameOffset + 4, 4));
        Assert.Equal(17, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(nicknameOffset + 12)));
    }

    [Fact]
    public async Task MissingWalletStillInitializesLevelAndMismatchedWalletSendsNothing()
    {
        var session = new RecordingSession();
        var binding = Binding("kr-812", new ServiceRegistry(), new EventBus(NullLogger.Instance), null);
        await binding.SendAsync(session, Login() with { Wallet = null }, default);
        Assert.Equal(new ushort[] { 3, 105 }, session.Sent.Select(frame => frame.Opcode));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(session.Sent[1].Payload.AsSpan(16)));
        Assert.Equal(17, BinaryPrimitives.ReadInt32LittleEndian(session.Sent[1].Payload.AsSpan(56)));
        session.Sent.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => binding.SendAsync(session,
            Login() with { Wallet = new PlayerWallet(99, 1, 2) }, default).AsTask());
        Assert.Empty(session.Sent);
    }

    private static LoginSucceeded Login() => new(42, "account", "ab", 17, new DateTime(2026, 10, 7),
        300001, [300001], new PlayerWallet(42, 12345, 678), [], []);

    [Fact]
    public async Task ModernLoginResponseDoesNotLoadTheLegacyStatLayout()
    {
        var session = new RecordingSession();
        var build = "cn-735";
        var binding = new LoginBinding(build + "/game", LoginValues.From(build, TestBuilds.Packets.ValuesOf(build)),
            3, TestBuilds.Opcodes(build), TestBuilds.Packets, build, new ServiceRegistry(),
            new EventBus(NullLogger.Instance), null, null, NullLogger.Instance);
        await binding.SendAsync(session, Login(), default);
        Assert.Equal((ushort)3, Assert.Single(session.Sent).Opcode);
    }

    private static LoginBinding Binding(string build, ServiceRegistry services, EventBus events,
        InventoryBinding? inventory) => new(build + "/game", LoginValues.From(build, TestBuilds.Packets.ValuesOf(build)),
            3, TestBuilds.Opcodes(build), TestBuilds.Packets, build, services, events, inventory, null,
            NullLogger.Instance);

    private sealed class MenuSender(string build) : IMessageSender<MainMenuRoomOpened>
    {
        internal List<MainMenuRoomOpened> Opened { get; } = [];
        public string Protocol => build + "/game";
        public ValueTask SendAsync(ISession session, MainMenuRoomOpened message, CancellationToken ct)
        {
            Opened.Add(message);
            return session.SendAsync(100, ReadOnlyMemory<byte>.Empty, ct);
        }
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
