using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

public sealed class MatchTransportTests
{
    [Fact]
    public async Task RealTcpMatchHandshakeRejectsIdentityForgeryAndClosesLeasesOnAdapterReload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var port = MinimalProfileTests.FreePort();
        var rows = MinimalProfileTests.Rows(port);
        rows.RemoveAll(r => r.Id == "game");
        rows.AddRange([
            PluginBoundaryTests.Row("relay", "net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/relay" }),
            PluginBoundaryTests.Row("core.message-router", "core.message-router"),
            // The match transport rows the profile mounts: the account admission of the raw path (the test's
            // directory checks the seat key with the account the join carries) and the success-signal sender.
            PluginBoundaryTests.Row("match-messages", "dialect.match", new()
            {
                ["protocol"] = "kr-1256/relay", ["native_handshake"] = "account",
                ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
            }),
            PluginBoundaryTests.Row("match-replies", "dialect.match-replies", new()
            {
                ["protocol"] = "kr-1256/relay", ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
            }),
            PluginBoundaryTests.Row("rooms", "test.match-directory"),
            PluginBoundaryTests.Row("match", "match-connection"),
        ]);
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            using var first = await Client.ConnectAsync(port, ct);
            using var second = await Client.ConnectAsync(port, ct);
            await first.AuthenticateAsync(7, ct);
            await second.AuthenticateAsync(8, ct);
            await first.SceneAsync(7, ct);
            await second.SceneAsync(8, ct);
            using var replacement = await Client.ConnectAsync(port, ct);
            await replacement.AuthenticateAsync(7, ct);
            Assert.Equal(0, await first.Stream.ReadAsync(new byte[1], ct));
            // A different entity may not claim the authenticated seat's scene.
            await replacement.SendAsync(12, Scene(8), ct);
            Assert.Equal(0, await replacement.Stream.ReadAsync(new byte[1], ct));
            var reload = await host.ReloadAsync("match-messages", ct);
            Assert.Contains("match", reload.ReloadedRowIds);
            Assert.Equal(0, await second.Stream.ReadAsync(new byte[1], ct));
            using var fresh = await Client.ConnectAsync(port, ct);
            await fresh.AuthenticateAsync(7, ct);
            await fresh.SceneAsync(7, ct);
            await host.UnloadAsync("match", ct);
            Assert.Equal(0, await fresh.Stream.ReadAsync(new byte[1], ct));
            using var unauthenticated = await Client.ConnectAsync(port, ct);
            await unauthenticated.SendAsync(3, Bytes(w =>
            {
                w.WriteInt32(1256); w.WriteInt32(0); w.WriteWideStringPrefixed("map");
                w.WriteInt32(1000000); w.WriteWideStringPrefixed("seat7"); w.WriteInt32(0); w.WriteInt32(1);
            }), ct);
            Assert.Equal(0, await unauthenticated.Stream.ReadAsync(new byte[1], ct));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(); write(new PacketWriter(buffer)); return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Scene(int user) => Bytes(w =>
    {
        w.WriteInt32(user); w.WriteInt32(1000000 + (user - 7) * 100000); w.WriteWideStringPrefixed($"seat{user}");
        w.WriteInt32(4300); w.WriteInt32(300001);
        for (var i = 0; i < 8; i++) w.WriteInt32(0);
        w.WriteWideStringPrefixed("scene");
        w.WriteByte(0); w.WriteUInt16(1); w.WriteUInt16(0); w.WriteUInt16(0);
        w.WriteUInt16(0); w.WriteByte(0); w.WriteInt32(0); w.WriteInt32(0);
    });

    private sealed class Client(TcpClient socket) : IDisposable
    {
        public NetworkStream Stream { get; } = socket.GetStream();
        private readonly SimpleCipher _send = SimpleCipher.ClientToServer();
        private readonly SimpleCipher _receive = SimpleCipher.ServerToClient();
        public static async Task<Client> ConnectAsync(int port, CancellationToken ct)
        {
            var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port, ct);
            var client = new Client(socket);
            var greeting = new byte[BootstrapGreeting.Size];
            await client.Stream.ReadExactlyAsync(greeting, ct);
            Assert.Equal(BootstrapGreeting.Build(4, ""), greeting);
            return client;
        }
        public Task SendAsync(ushort opcode, byte[] body, CancellationToken ct) => MinimalProfileTests.SendAsync(Stream, _send, opcode, body, ct);
        public async Task AuthenticateAsync(int user, CancellationToken ct)
        {
            await SendAsync(1, Bytes(w =>
            {
                w.WriteBytes(Enumerable.Repeat((byte)user, 20).ToArray()); w.WriteByte(0); w.WriteInt32(0);
                w.WriteInt32(user); w.WriteWideStringPrefixed($"seat{user}");
            }), ct);
            Assert.Equal(new byte[10], await ReceiveAsync(22, ct));
            Assert.Equal(new byte[4], await ReceiveAsync(1, ct));
        }
        public async Task SceneAsync(int user, CancellationToken ct)
        {
            await SendAsync(12, Scene(user), ct);
            foreach (var (opcode, length) in new (ushort, int)[] { (4, 268), (5, 1008), (6, 136) })
            {
                var body = await ReceiveAsync(opcode, ct);
                Assert.Equal(length, body.Length);
                Assert.Equal(1000000 + (user - 7) * 100000, BitConverter.ToInt32(body));
            }
            Assert.Empty(await ReceiveAsync(13, ct));
        }
        private async Task<byte[]> ReceiveAsync(ushort expected, CancellationToken ct)
        {
            var bytes = new byte[8]; await Stream.ReadExactlyAsync(bytes, ct); _receive.DecryptInPlace(bytes);
            Assert.True(PacketFrame_8Bytes.TryReadHeader(bytes, out var header)); Assert.Equal(expected, header.Opcode);
            Assert.InRange(header.PayloadLength, 0, 4096);
            var body = new byte[header.PayloadLength]; await Stream.ReadExactlyAsync(body, ct); _receive.DecryptInPlace(body);
            Assert.True(PacketFrame_8Bytes.ValidatePayload(header, body)); return body;
        }
        public void Dispose() => socket.Dispose();
    }

    [Plugin("test.match-directory", Provides = [typeof(IMatchRoomDirectory)])]
    public sealed class DirectoryPlugin : IPlugin, IMatchRoomDirectory
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, ISession> _seats = [];
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            context.Effect(context.Services.Register<IMatchRoomDirectory>(this)); return ValueTask.CompletedTask;
        }
        public async ValueTask<MatchSeatBinding> BindAsync(ISession session, MatchAuthenticate message, CancellationToken ct)
        {
            var user = message.UserNumber;
            if (user is not (7 or 8) || message.Nickname != $"seat{user}"
                || !message.Ticket.Span.SequenceEqual(Enumerable.Repeat((byte)user, 20).ToArray()))
                throw new InvalidOperationException("Invalid test seat capability.");
            ISession? previous;
            lock (_gate) { _seats.TryGetValue(user, out previous); _seats[user] = session; }
            if (previous is not null) await previous.Connection.CloseAsync("replaced");
            return new(1, user - 7, user, 1000000 + (user - 7) * 100000, $"seat{user}", "map", 1);
        }
        public bool IsCurrent(ISession session, MatchSeatBinding binding)
        {
            lock (_gate) return _seats.TryGetValue(binding.UserNumber, out var owner) && owner == session && !session.Connection.IsClosed;
        }
        public void MarkInitialized(ISession session, MatchSeatBinding binding)
        {
            if (!IsCurrent(session, binding)) throw new InvalidOperationException("Revoked test lease.");
        }
        public void Release(ISession session)
        {
            lock (_gate)
                foreach (var key in _seats.Where(pair => pair.Value == session).Select(pair => pair.Key).ToArray()) _seats.Remove(key);
        }
    }
}
