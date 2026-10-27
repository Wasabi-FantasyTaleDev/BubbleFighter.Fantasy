using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Wire;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// In-process handshake tests: plaintext 258-byte greeting, then C2S/S2C ciphers
/// (kr-1256 <c>sub_10B9720</c>) and the <c>gcpReqGamePatch</c>/<c>gspRepGamePatch</c> exchange.
/// </summary>
public sealed class ProtocolHandshakeTests
{
    /// <summary>Shared sink filled by <see cref="HarnessPlugin"/> during activation.</summary>
    internal sealed class HarnessSink
    {
        public ISessionRegistry? Sessions;
        public IConnectionProtocolRegistry? Protocols;
        public readonly List<int> MismatchReported = [];
        public readonly List<PacketReceivedEvent> Received = [];
        public readonly List<PacketSentEvent> Sent = [];
    }

    /// <summary>Captures registries and handshake events for one host start.</summary>
    [Plugin("test.protocol-harness", Requires = [typeof(ISessionRegistry), typeof(IConnectionProtocolRegistry)])]
    public sealed class HarnessPlugin : IPlugin
    {
        /// <summary>The sink the next activation writes into.</summary>
        internal static HarnessSink? Current;

        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var sink = Current ?? throw new InvalidOperationException("HarnessPlugin.Current is unset.");
            sink.Sessions = context.Services.Get<ISessionRegistry>();
            sink.Protocols = context.Services.Get<IConnectionProtocolRegistry>();
            context.Effect(context.Events.On<ClientVersionMismatchEvent>(e =>
            {
                lock (sink.MismatchReported)
                    sink.MismatchReported.Add(e.Reported);
            }));
            context.Effect(context.Events.On<PacketReceivedEvent>(e =>
            {
                lock (sink.Received)
                    sink.Received.Add(e);
            }));
            context.Effect(context.Events.On<PacketSentEvent>(e =>
            {
                lock (sink.Sent)
                    sink.Sent.Add(e);
            }));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Two pipes standing in for an accepted connection.</summary>
    internal sealed class LoopbackConnection : IConnection
    {
        private readonly Pipe _inbound = new(new PipeOptions(useSynchronizationContext: false));
        private readonly Pipe _outbound = new(new PipeOptions(useSynchronizationContext: false));
        private int _closed;

        public LoopbackConnection()
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1);
            Input = _inbound.Reader;
            Output = _outbound.Writer;
        }

        /// <inheritdoc />
        public long Id { get; } = 1;

        /// <inheritdoc />
        public EndPoint RemoteEndPoint { get; }

        /// <inheritdoc />
        public PipeReader Input { get; }

        /// <inheritdoc />
        public PipeWriter Output { get; }

        /// <inheritdoc />
        public ConcurrentDictionary<string, object> Items { get; } = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public bool IsClosed => _closed != 0;

        /// <summary>Reason passed to the first <see cref="CloseAsync"/> call.</summary>
        public string? CloseReason { get; private set; }

        /// <summary>Client-side writer into the protocol inbound pipe.</summary>
        public PipeWriter ClientOutput => _inbound.Writer;

        /// <summary>Client-side reader of the protocol outbound pipe.</summary>
        public PipeReader ClientInput => _outbound.Reader;

        /// <inheritdoc />
        public async ValueTask CloseAsync(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            CloseReason = reason;
            try { Input.CancelPendingRead(); } catch (InvalidOperationException) { }
            try { ClientInput.CancelPendingRead(); } catch (InvalidOperationException) { }
            await _inbound.Writer.CompleteAsync().ConfigureAwait(false);
            await _outbound.Writer.CompleteAsync().ConfigureAwait(false);
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes from the server.</summary>
        public async Task<byte[]> ReceiveExactlyAsync(int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                var result = await ClientInput.ReadAsync(ct).ConfigureAwait(false);
                if (result.Buffer.IsEmpty && (result.IsCompleted || result.IsCanceled))
                    throw new IOException($"closed after {filled}/{count} bytes ({CloseReason ?? "no reason"})");
                var take = (int)Math.Min(result.Buffer.Length, count - filled);
                result.Buffer.Slice(0, take).CopyTo(buffer.AsSpan(filled));
                filled += take;
                ClientInput.AdvanceTo(result.Buffer.GetPosition(take));
            }
            return buffer;
        }

        /// <summary>Writes already-encrypted bytes into the protocol inbound pipe.</summary>
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await ClientOutput.WriteAsync(data, ct).ConfigureAwait(false);
        }
    }

    private sealed class ProtocolHarness : IAsyncDisposable
    {
        private readonly PluginHost _host;
        private readonly HarnessSink _sink;

        private ProtocolHarness(PluginHost host, HarnessSink sink)
        {
            _host = host;
            _sink = sink;
        }

        public IConnectionProtocol Game => _sink.Protocols!.Resolve(ProtocolPlugin.GameProtocolName);

        public IConnectionProtocol Relay => _sink.Protocols!.Resolve(ProtocolPlugin.RelayProtocolName);

        public ISessionRegistry Sessions => _sink.Sessions!;

        public List<int> MismatchReported => _sink.MismatchReported;

        public List<PacketReceivedEvent> Received => _sink.Received;

        public List<PacketSentEvent> Sent => _sink.Sent;

        public static async Task<ProtocolHarness> StartAsync(Dictionary<string, object?>? protocolExtras = null)
        {
            var sink = new HarnessSink();
            HarnessPlugin.Current = sink;
            var opcodeTable = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml");
            var protocolConfig = new Dictionary<string, object?> { ["opcode_table_path"] = opcodeTable };
            if (protocolExtras is not null)
            {
                foreach (var (key, value) in protocolExtras)
                    protocolConfig[key] = value;
            }

            PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
                new() { Id = id, Plugin = plugin, Source = "test", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };

            var host = new PluginHost(
                [
                    Row("core.protocol-registry", "core.protocol-registry"),
                    Row("core.session-registry", "core.session-registry"),
                    Row("core.packet-router", "core.packet-router"),
                    Row("core.client-profiles", "core.client-profiles"),
                    Row("core.opcode-tables", "core.opcode-tables"),
                    Row("core.wire-dialects", "core.wire-dialects"),
                    Row("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
                    Row("protocol", "protocol", protocolConfig),
                    Row("harness", "test.protocol-harness"),
                ],
                PluginCatalog.ScanAssemblies(
                    [typeof(Dialect.DialectPlugin).Assembly, typeof(ProtocolPlugin).Assembly, typeof(PluginHost).Assembly, typeof(ProtocolHandshakeTests).Assembly]),
                NullLoggerFactory.Instance);

            try
            {
                await host.StartAsync(CancellationToken.None);
            }
            catch
            {
                if (ReferenceEquals(HarnessPlugin.Current, sink))
                    HarnessPlugin.Current = null;
                throw;
            }

            return new ProtocolHarness(host, sink);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.ShutdownAsync(CancellationToken.None);
            if (ReferenceEquals(HarnessPlugin.Current, _sink))
                HarnessPlugin.Current = null;
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(15);
        }
        return false;
    }

    private static byte[] EncryptFrame(SimpleCipher cipher, ushort opcode, byte[] payload)
    {
        var frame = PacketFrame_8Bytes.Encode(opcode, payload);
        cipher.EncryptInPlace(frame);
        return frame;
    }

    private static async Task WithProtocolAsync(
        IConnectionProtocol protocol,
        Func<LoopbackConnection, CancellationToken, Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new LoopbackConnection();
        var run = protocol.RunAsync(connection, cts.Token).AsTask();
        try
        {
            await body(connection, cts.Token);
        }
        finally
        {
            if (!connection.IsClosed)
                await connection.CloseAsync("test complete");
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                cts.Cancel();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeaturesCloseAfterGameOrRelayConnectionEnds(bool relay)
    {
        await using var harness = await ProtocolHarness.StartAsync(new() { ["max_session_features"] = 1 });
        ISessionWithFeatures? session = null;
        IDisposable? registration = null;
        await WithProtocolAsync(relay ? harness.Relay : harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            Assert.True(await WaitForAsync(() => harness.Sessions.All.Count == 1));
            session = Assert.IsAssignableFrom<ISessionWithFeatures>(Assert.Single(harness.Sessions.All));
            Assert.Equal(1, session.Features.Capacity);
            registration = session.RegisterFeature(new object());
            Assert.True(session.TryFeature<object>(out _));
        });
        Assert.NotNull(session);
        Assert.True(session.Features.IsClosed);
        Assert.Equal(0, session.Features.Count);
        Assert.Empty(harness.Sessions.All);
        Assert.Throws<ObjectDisposedException>(() => session.RegisterFeature(new object()));
        registration!.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task InvalidFeatureCapacityIsRejectedAtActivation(int capacity)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var harness = await ProtocolHarness.StartAsync(new() { ["max_session_features"] = capacity });
        });
        Assert.IsType<ConfigException>(error.InnerException);
        Assert.Contains("MaxSessionFeatures", error.InnerException!.Message);
    }

    [Fact]
    public async Task MalformedFrameClosesFeatureAdmission()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        ISessionWithFeatures? session = null;
        IDisposable? registration = null;
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            Assert.True(await WaitForAsync(() => harness.Sessions.All.Count == 1));
            session = Assert.IsAssignableFrom<ISessionWithFeatures>(Assert.Single(harness.Sessions.All));
            registration = session.RegisterFeature(new object());
            var frame = PacketFrame_8Bytes.Encode(WirePins.PatchRequestOpcode, WirePins.GamePatchPayload());
            frame[7] ^= 0xFF;
            SimpleCipher.ClientToServer().EncryptInPlace(frame);
            await connection.SendAsync(frame, ct);
            Assert.True(await WaitForAsync(() => connection.IsClosed));
            Assert.Contains("header integrity", connection.CloseReason);
        });
        Assert.NotNull(session);
        Assert.True(session.Features.IsClosed);
        Assert.Equal(0, session.Features.Count);
        registration!.Dispose();
    }

    [Fact]
    public async Task FeaturesCloseWhenProtocolLifetimeIsCancelled()
    {
        var harness = await ProtocolHarness.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new LoopbackConnection();
        var run = harness.Game.RunAsync(connection, timeout.Token).AsTask();
        try
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, timeout.Token);
            Assert.True(await WaitForAsync(() => harness.Sessions.All.Count == 1));
            var session = Assert.IsAssignableFrom<ISessionWithFeatures>(Assert.Single(harness.Sessions.All));
            using var registration = session.RegisterFeature(new object());
            await harness.DisposeAsync();
            await run.WaitAsync(timeout.Token);
            Assert.True(session.Features.IsClosed);
            Assert.Equal(0, session.Features.Count);
            Assert.True(connection.IsClosed);
        }
        finally
        {
            await connection.CloseAsync("test complete");
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task GameAcceptsPinnedC2SGamePatchCiphertext()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var wire = Convert.FromHexString(WirePins.GamePatchC2SFrameCipherHex);
            Assert.Equal(WirePins.GamePatchFrameSize, wire.Length);
            await connection.SendAsync(wire, ct);

            var reply = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);
            Assert.Equal(WirePins.RepGamePatchS2CFrameCipherHex, Convert.ToHexString(reply));
        });
    }

    /// <summary>The first-frame gate, handler and reply all follow the game table after its numbers change.</summary>
    [Theory]
    [InlineData("gcpReqGamePatch")]
    [InlineData("gcpFirstConnect")]
    public async Task GameHandshakeFollowsConfiguredNamesAndRemappedNumbers(string requestName)
    {
        var path = WriteRemappedHandshakeTable(requestName);
        try
        {
            await using var harness = await ProtocolHarness.StartAsync(new()
            {
                ["opcode_table_path"] = path, ["patch_request_name"] = requestName,
                ["use_catalog_candidates"] = false,
            });
            await WithProtocolAsync(harness.Game, async (connection, ct) =>
            {
                _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
                await connection.SendAsync(EncryptFrame(SimpleCipher.ClientToServer(), 30001, WirePins.GamePatchPayload()), ct);
                var reply = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);
                SimpleCipher.ServerToClient().DecryptInPlace(reply);
                Assert.Equal(PacketFrame_8Bytes.Encode(30002, new byte[WirePins.RepGamePatchPayloadSize]), reply);
                Assert.False(connection.IsClosed);
            });
        }
        finally { File.Delete(path); }
    }

    /// <summary>A packet at the former numeric slot cannot satisfy a handshake remapped by the profile.</summary>
    [Fact]
    public async Task RemappedHandshakeRejectsTheFormerRequestNumber()
    {
        var path = WriteRemappedHandshakeTable("gcpFirstConnect");
        try
        {
            await using var harness = await ProtocolHarness.StartAsync(new()
            {
                ["opcode_table_path"] = path, ["patch_request_name"] = "gcpFirstConnect",
                ["use_catalog_candidates"] = false,
            });
            await WithProtocolAsync(harness.Game, async (connection, ct) =>
            {
                _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
                await connection.SendAsync(EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, WirePins.GamePatchPayload()), ct);
                Assert.True(await WaitForAsync(() => connection.IsClosed));
                Assert.Contains("first packet must be gcpFirstConnect", connection.CloseReason);
                Assert.Empty(harness.Sent);
            });
        }
        finally { File.Delete(path); }
    }

    /// <summary>Both bootstrap directions must resolve before the protocol row can become active.</summary>
    [Theory]
    [InlineData("gcpReqGamePatch")]
    [InlineData("gspRepGamePatch")]
    public async Task MissingHandshakeNameFailsAtActivation(string missingName)
    {
        var path = WriteRemappedHandshakeTable("gcpReqGamePatch");
        try
        {
            File.WriteAllText(path, File.ReadAllText(path).Replace(missingName, "MissingBootstrap", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var harness = await ProtocolHarness.StartAsync(new()
                {
                    ["opcode_table_path"] = path, ["use_catalog_candidates"] = false,
                });
            });
            Assert.IsType<ConfigException>(error.InnerException);
            Assert.Contains(missingName, error.InnerException!.Message);
        }
        finally { File.Delete(path); }
    }

    private static string WriteRemappedHandshakeTable(string requestName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bff-handshake-{Guid.NewGuid():N}.yaml");
        // Synthetic assignments differ from every pinned bootstrap number; relay names must not shadow game names.
        File.WriteAllText(path, $$"""
            profile: kr-1256
            opcodes:
              - { value: 30001, direction: c2s, name: {{requestName}}, connection: game, evidence: unverified }
              - { value: 30002, direction: s2c, name: gspRepGamePatch, connection: game, evidence: unverified }
              - { value: 30003, direction: c2s, name: {{requestName}}, connection: relay, evidence: unverified }
              - { value: 30004, direction: s2c, name: gspRepGamePatch, connection: relay, evidence: unverified }
            """);
        return path;
    }

    [Fact]
    public async Task GameHandshakePinsGreeting258AndReply16()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            var greeting = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            Assert.Equal(WirePins.GreetingSize, greeting.Length);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
            Assert.All(greeting[2..], b => Assert.Equal(0, b));

            var cipher = SimpleCipher.ClientToServer();
            await connection.SendAsync(
                EncryptFrame(cipher, WirePins.PatchRequestOpcode, WirePins.GamePatchPayload()),
                ct);

            var reply = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);
            Assert.Equal(WirePins.RepGamePatchFrameSize, reply.Length);
            Assert.Equal(WirePins.RepGamePatchS2CFrameCipherHex, Convert.ToHexString(reply));

            var plain = (byte[])reply.Clone();
            SimpleCipher.ServerToClient().DecryptInPlace(plain);
            Assert.Equal(WirePins.RepGamePatchPlainFrameHex, Convert.ToHexString(plain));

            var sent = await WaitForAsync(() =>
            {
                lock (harness.Sent)
                    return harness.Sent.Any(e => e.Opcode == WirePins.PatchReplyOpcode && e.PayloadLength == WirePins.RepGamePatchPayloadSize);
            });
            Assert.True(sent, "no PacketSentEvent for gspRepGamePatch with an 8-byte payload");
            lock (harness.Sent)
            {
                var packet = Assert.Single(harness.Sent, e => e.Opcode == WirePins.PatchReplyOpcode);
                Assert.Equal(plain[8..], packet.Payload.ToArray());
            }

            // Post-handshake dispatch is the observable stand-in for Mark: Unknown521
            // must be accepted without closing (the gate stays open after the reply).
            await connection.SendAsync(
                EncryptFrame(cipher, WirePins.PreHandshakeOpcode, [1, 2, 3]),
                ct);
            var received = await WaitForAsync(() =>
            {
                lock (harness.Received)
                    return harness.Received.Any(e => e.Packet.Opcode == WirePins.PreHandshakeOpcode && !e.HasHandler);
            });
            Assert.True(received, "Unknown521 was not dispatched after the handshake");
            Assert.False(connection.IsClosed);
        });
    }

    [Fact]
    public async Task GreetingCarriesConfiguredStatusAndCommandLine()
    {
        await using var harness = await ProtocolHarness.StartAsync(new()
        {
            ["bootstrap_status"] = 9,
            ["bootstrap_command_line"] = "run",
        });
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            var greeting = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            Assert.Equal(WirePins.GreetingSize, greeting.Length);
            Assert.Equal(9, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
            Assert.Equal("720075006E00", Convert.ToHexString(greeting.AsSpan(2, 6)));
            Assert.All(greeting[8..], b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public async Task ShortPatchPayloadIsNotAHandshakeAndStaysOpen()
    {
        // A body that is not the 96-byte handshake is not a version report. A match connection uses a 49-byte
        // relay authentication on the same opcode number; treating that body as a malformed version report
        // would refuse the match connection.
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, new byte[3]),
                ct);

            await Task.Delay(200, ct);
            Assert.False(connection.IsClosed, "a non-handshake opcode 1 closed the connection");
        });
    }

    [Fact]
    public async Task WrongVersionInAHandshakeShapedBodyStillCloses()
    {
        // The check is not weakened: a body of the handshake's own length carrying
        // a wrong version is still a mismatch and still closes.
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var wrong = new byte[WirePins.GamePatchPayloadSize];
            WirePins.WrongVersion.CopyTo(wrong, 0);
            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, wrong),
                ct);

            Assert.True(await WaitForAsync(() => connection.IsClosed), "wrong version did not close");
            Assert.Contains("client version mismatch", connection.CloseReason);
        });
    }

    [Fact]
    public async Task WrongVersionClosesAfter96BytePayload()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var payload = WirePins.GamePatchPayload(9999);
            Assert.Equal(WirePins.GamePatchPayloadSize, payload.Length);
            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, payload),
                ct);

            Assert.True(await WaitForAsync(() => connection.IsClosed), "version mismatch did not close");
            Assert.Contains("client version mismatch", connection.CloseReason);
            lock (harness.MismatchReported)
                Assert.Contains(9999, harness.MismatchReported);
        });
    }

    [Fact]
    public async Task PreHandshakeOpcodePassesButTheHandshakeIsStillRequired()
    {
        await using var harness = await ProtocolHarness.StartAsync(new() { ["pre_handshake_opcodes"] = new List<object?> { "521" } });
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var cipher = SimpleCipher.ClientToServer();
            await connection.SendAsync(EncryptFrame(cipher, WirePins.PreHandshakeOpcode, [1, 2, 3]), ct);
            await Task.Delay(200, ct);
            Assert.False(connection.IsClosed, "a pre-handshake opcode closed the connection");
            await connection.SendAsync(EncryptFrame(cipher, WirePins.PreHandshakeOpcode, [1, 2, 3]), ct);
            await connection.SendAsync(EncryptFrame(cipher, 7, [1]), ct);
            Assert.True(await WaitForAsync(() => connection.IsClosed), "another opcode before the handshake did not close");
            Assert.Contains("gcpReqGamePatch", connection.CloseReason);
        });
    }

    [Fact]
    public async Task FirstNonPatchOpcodeClosesGame()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PreHandshakeOpcode, [1, 2, 3]),
                ct);

            Assert.True(await WaitForAsync(() => connection.IsClosed), "non-patch first packet did not close");
            Assert.Contains("gcpReqGamePatch", connection.CloseReason);
            lock (harness.MismatchReported)
                Assert.Contains(-1, harness.MismatchReported);
        });
    }

    [Fact]
    public async Task RelaxedHandshakeKeepsANonPatchFirstPacketOpen()
    {
        // A live session showed the client authenticating on its match connection,
        // closing it, and opening a new one that began with C2S 3 - the login
        // request - rather than the handshake. The requirement closed that
        // reconnect. With it relaxed the frame is accepted and the connection stays
        // up, which is what lets the client finish its connection setup.
        await using var harness = await ProtocolHarness.StartAsync(
            new Dictionary<string, object?> { ["require_patch_handshake_first"] = false });
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PreHandshakeOpcode, [1, 2, 3]),
                ct);

            await Task.Delay(300, ct);
            Assert.False(connection.IsClosed, "a non-patch first packet closed the connection");
            lock (harness.MismatchReported)
                Assert.DoesNotContain(-1, harness.MismatchReported);
        });
    }

    [Fact]
    public async Task RelayGreetingIs258AndAcceptsNonPatchFirstPacket()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Relay, async (connection, ct) =>
        {
            var greeting = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            Assert.Equal(WirePins.GreetingSize, greeting.Length);

            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PreHandshakeOpcode, [1, 2, 3]),
                ct);

            var received = await WaitForAsync(() =>
            {
                lock (harness.Received)
                    return harness.Received.Any(e => e.Packet.Opcode == WirePins.PreHandshakeOpcode && !e.HasHandler);
            });
            Assert.True(received, "relay did not dispatch Unknown521");
            Assert.False(connection.IsClosed);
        });
    }

    [Fact]
    public async Task TrailingUnknownBytesIn96BytePatchStillHandshake()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var payload = WirePins.GamePatchPayload();
            payload[^1] = 0xFF;
            Assert.Equal(WirePins.GamePatchPayloadSize, payload.Length);

            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, payload),
                ct);

            var reply = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);
            Assert.Equal(WirePins.RepGamePatchFrameSize, reply.Length);
            Assert.False(connection.IsClosed);
        });
    }

    [Fact]
    public async Task EnforceOffStillRepliesOnVersionMismatch()
    {
        await using var harness = await ProtocolHarness.StartAsync(new()
        {
            ["enforce_client_version"] = false,
        });
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, WirePins.GamePatchPayload(1)),
                ct);

            var reply = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);
            Assert.Equal(WirePins.RepGamePatchS2CFrameCipherHex, Convert.ToHexString(reply));
            Assert.False(connection.IsClosed);
            lock (harness.MismatchReported)
                Assert.Contains(1, harness.MismatchReported);
        });
    }

    [Fact]
    public async Task OversizedDeclaredPayloadCloses()
    {
        await using var harness = await ProtocolHarness.StartAsync(new()
        {
            ["max_payload_bytes"] = 16,
        });
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            await connection.SendAsync(
                EncryptFrame(SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, WirePins.GamePatchPayload()),
                ct);

            Assert.True(await WaitForAsync(() => connection.IsClosed), "oversize frame did not close");
            Assert.Contains("exceeds max_payload_bytes", connection.CloseReason);
        });
    }

    [Fact]
    public async Task CorruptHeaderCloses()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var frame = PacketFrame_8Bytes.Encode(WirePins.PatchRequestOpcode, WirePins.GamePatchPayload());
            frame[7] ^= 0xFF;
            SimpleCipher.ClientToServer().EncryptInPlace(frame);
            await connection.SendAsync(frame, ct);

            Assert.True(await WaitForAsync(() => connection.IsClosed), "corrupt header did not close");
            Assert.Contains("header integrity", connection.CloseReason);
        });
    }

    [Fact]
    public async Task CorruptPayloadCloses()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var frame = PacketFrame_8Bytes.Encode(WirePins.PatchRequestOpcode, WirePins.GamePatchPayload());
            frame[PacketFrame_8Bytes.HeaderSize] ^= 0xFF;
            SimpleCipher.ClientToServer().EncryptInPlace(frame);
            await connection.SendAsync(frame, ct);

            Assert.True(await WaitForAsync(() => connection.IsClosed), "corrupt payload did not close");
            Assert.Contains("payload integrity", connection.CloseReason);
        });
    }

    [Fact]
    public async Task SplitFrameAcrossWritesCompletesHandshake()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var wire = EncryptFrame(
                SimpleCipher.ClientToServer(),
                WirePins.PatchRequestOpcode,
                WirePins.GamePatchPayload());
            Assert.Equal(WirePins.GamePatchFrameSize, wire.Length);

            await connection.SendAsync(wire.AsMemory(0, 10), ct);
            await Task.Delay(50, ct);
            Assert.False(connection.IsClosed);
            await connection.SendAsync(wire.AsMemory(10), ct);

            var reply = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);
            Assert.Equal(WirePins.RepGamePatchFrameSize, reply.Length);
        });
    }

    [Fact]
    public async Task AfterHandshakeUnknownOpcodeStaysOpen()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var cipher = SimpleCipher.ClientToServer();
            await connection.SendAsync(
                EncryptFrame(cipher, WirePins.PatchRequestOpcode, WirePins.GamePatchPayload()),
                ct);
            _ = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);

            await connection.SendAsync(
                EncryptFrame(cipher, WirePins.PreHandshakeOpcode, [1, 2, 3]),
                ct);

            var received = await WaitForAsync(() =>
            {
                lock (harness.Received)
                    return harness.Received.Any(e => e.Packet.Opcode == WirePins.PreHandshakeOpcode && !e.HasHandler);
            });
            Assert.True(received, "Unknown521 was not dispatched after the handshake");
            Assert.False(connection.IsClosed);
        });
    }

    [Fact]
    public async Task TwoFramesInOneWriteDispatchPatchThenUnknown()
    {
        await using var harness = await ProtocolHarness.StartAsync();
        await WithProtocolAsync(harness.Game, async (connection, ct) =>
        {
            _ = await connection.ReceiveExactlyAsync(WirePins.GreetingSize, ct);
            var cipher = SimpleCipher.ClientToServer();
            var first = EncryptFrame(cipher, WirePins.PatchRequestOpcode, WirePins.GamePatchPayload());
            var second = EncryptFrame(cipher, WirePins.PreHandshakeOpcode, [9, 8, 7]);
            var blob = new byte[first.Length + second.Length];
            first.CopyTo(blob, 0);
            second.CopyTo(blob, first.Length);
            Assert.Equal(WirePins.GamePatchFrameSize + PacketFrame_8Bytes.HeaderSize + 3, blob.Length);

            await connection.SendAsync(blob, ct);
            var reply = await connection.ReceiveExactlyAsync(WirePins.RepGamePatchFrameSize, ct);
            Assert.Equal(WirePins.RepGamePatchFrameSize, reply.Length);

            var received = await WaitForAsync(() =>
            {
                lock (harness.Received)
                    return harness.Received.Any(e => e.Packet.Opcode == WirePins.PreHandshakeOpcode);
            });
            Assert.True(received, "second frame in the same write was not dispatched");
        });
    }
}
