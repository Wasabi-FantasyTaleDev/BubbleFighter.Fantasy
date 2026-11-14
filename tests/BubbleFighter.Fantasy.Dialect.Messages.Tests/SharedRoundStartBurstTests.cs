using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins recipient-local loading and start bursts before their family-specific senders are combined.</summary>
public sealed class SharedRoundStartBurstTests
{
    [Theory]
    [InlineData("kr-812", false, 3)]
    [InlineData("cn-324", false, 2)]
    [InlineData("cn-717", false, 3)]
    [InlineData("id-400", false, 2)]
    [InlineData("kr-1256", true, 2)]
    [InlineData("cn-774", true, 2)]
    [InlineData("tw-454", true, 3)]
    public async Task NativeBurstPreservesControlWordsAndResolvedOpcodes(string build, bool modern, int startWords)
    {
        foreach (var remap in new[] { false, true })
        foreach (var fallback in new[] { false, true })
        {
            var table = Table(build, remap);
            var recipient = new Session(table.Profile);
            await BurstAsync(table, build, modern, recipient,
                new RoundCountdownStarted(1, 3000) { MatchOverGame = fallback }, new RoundStarted(1, 240000));

            Assert.Equal(new[] { table.S2C("gspGameEverybodyLoadingComplete"), table.S2C("gspGameCountDown"),
                table.S2C("gspGameStartGame") }, recipient.Sent.Select(frame => frame.Opcode));
            var gates = modern
                ? fallback ? "000000000000000001000000" : "010000000100000001000000"
                : fallback ? "0000000000000000" : "0100000000000000";
            if (!modern && startWords == 3) gates += "00000000";
            Assert.Equal(gates, Convert.ToHexString(recipient.Sent[0].Body));
            Assert.Equal("B80B0000", Convert.ToHexString(recipient.Sent[1].Body));
            Assert.Equal(startWords == 3 ? "80A903000000000000000000" : "80A9030000000000",
                Convert.ToHexString(recipient.Sent[2].Body));
        }
    }

    [Theory]
    [InlineData("kr-1256", 2)]
    [InlineData("cn-774", 2)]
    [InlineData("tw-454", 3)]
    public async Task UnlimitedClockNotificationFollowsTheNativeStart(string build, int words)
    {
        var table = Table(build, true);
        var session = new Session(table.Profile);
        await BurstAsync(table, build, true, session, new RoundCountdownStarted(1, 3000),
            new RoundStarted(1, 12345) { Unlimited = true });
        Assert.Equal(new[] { table.S2C("gspGameEverybodyLoadingComplete"), table.S2C("gspGameCountDown"),
            table.S2C("gspGameStartGame"), table.S2C("gspGameClockUnbounded_") }, session.Sent.Select(frame => frame.Opcode));
        Assert.Equal(words == 3 ? "FFFFFF3F0000000000000000" : "FFFFFF3F00000000",
            Convert.ToHexString(session.Sent[2].Body));
        Assert.Empty(session.Sent[3].Body);
    }

    /// <summary>Restored profile names do not add frames to the legacy codec or shared loading-flag binding.</summary>
    [Theory]
    [InlineData("kr-812", false, 3, 702, 703, 704)]
    [InlineData("kr-1256", true, 2, 585, 607, 608)]
    [InlineData("cn-738", false, 3, 702, 703, 704)]
    [InlineData("cn-742", false, 3, 702, 703, 704)]
    [InlineData("cn-774", true, 2, 585, 601, 602)]
    [InlineData("tw-454", true, 3, 585, 601, 602)]
    public async Task NativePreCountdownNamesPreserveProfileStartBursts(string build, bool modern,
        int startWords, ushort loading, ushort countdown, ushort start)
    {
        var table = TestBuilds.Opcodes(build);
        // Only the legacy room writer resolves play rows; modern builds are pinned by the frames asserted below.
        if (!modern) Assert.False(TestBuilds.Of(build).Play.Wire.PreCountDownFirst);
        var preCountdown = table.S2C("gspGamePreCountDown");
        foreach (var fallback in new[] { false, true })
        {
            var recipient = new Session(table.Profile);
            var countdownMessage = new RoundCountdownStarted(1, 3000) { MatchOverGame = fallback };
            var started = new RoundStarted(1, 240000);
            if (modern)
                await BurstAsync(table, build, true, recipient, countdownMessage, started);
            else
            {
                var codec = LegacyCodec(table, build);
                await codec.SendAsync(recipient, countdownMessage, default);
                await codec.SendAsync(recipient, started, default);
            }

            Assert.Equal(new[] { loading, countdown, start }, recipient.Sent.Select(frame => frame.Opcode));
            Assert.DoesNotContain(recipient.Sent, frame => frame.Opcode == preCountdown);
            var gates = modern
                ? fallback ? "000000000000000001000000" : "010000000100000001000000"
                : fallback ? "000000000000000000000000" : "010000000000000000000000";
            Assert.Equal(gates, Convert.ToHexString(recipient.Sent[0].Body));
            Assert.Equal("B80B0000", Convert.ToHexString(recipient.Sent[1].Body));
            Assert.Equal(startWords == 3 ? "80A903000000000000000000" : "80A9030000000000",
                Convert.ToHexString(recipient.Sent[2].Body));
        }
    }

    /// <summary>Real profile pre-countdown names never extend the tutorial's loading, countdown and start burst.</summary>
    [Theory]
    [InlineData("kr-812", 3)]
    [InlineData("kr-1256", 2)]
    [InlineData("cn-738", 3)]
    [InlineData("cn-742", 3)]
    [InlineData("cn-774", 2)]
    [InlineData("tw-454", 3)]
    [InlineData("cn-324", 2)]
    public async Task NativePreCountdownNamesPreserveTutorialStartBursts(string build, int startWords)
    {
        var table = TestBuilds.Opcodes(build);
        var recipient = new Session(table.Profile);
        await RoundStartSender.SendCountdownAsync(TestBuilds.Packets, build, table, true, false, recipient,
            new RoundCountdownStarted(1, 3000) { IsTutorial = true }, default);
        await RoundStartSender.SendStartedAsync(TestBuilds.Packets, build, table, recipient,
            new RoundStarted(1, 240000) { IsTutorial = true }, default);

        Assert.Equal(new[] { table.S2C("gspGameEverybodyLoadingComplete"), table.S2C("gspGameCountDown"),
            table.S2C("gspGameStartGame") }, recipient.Sent.Select(frame => frame.Opcode));
        Assert.DoesNotContain(recipient.Sent, frame => frame.Opcode == table.S2C("gspGamePreCountDown"));
        Assert.Equal(build == "cn-324" ? "0100000000000000" : "010000000000000000000000",
            Convert.ToHexString(recipient.Sent[0].Body));
        Assert.Equal("B80B0000", Convert.ToHexString(recipient.Sent[1].Body));
        Assert.Equal(startWords == 3 ? "80A903000000000080A90300" : "80A9030000000000",
            Convert.ToHexString(recipient.Sent[2].Body));
    }

    /// <summary>The existing CN324 profile still selects its pre-countdown before the ordinary countdown.</summary>
    [Fact]
    public async Task ExistingCn324ProfileKeepsItsOrdinaryPreCountdown()
    {
        const string build = "cn-324";
        var table = TestBuilds.Opcodes(build);
        Assert.True(TestBuilds.Of(build).Play.Wire.PreCountDownFirst);
        var codec = LegacyCodec(table, build);
        foreach (var fallback in new[] { false, true })
        {
            var recipient = new Session(table.Profile);
            await codec.SendAsync(recipient, new RoundCountdownStarted(1, 3000) { MatchOverGame = fallback }, default);
            await codec.SendAsync(recipient, new RoundStarted(1, 240000), default);

            Assert.Equal(new ushort[] { 702, 737, 703, 704 }, recipient.Sent.Select(frame => frame.Opcode));
            Assert.Equal(fallback ? "0000000000000000" : "0100000000000000",
                Convert.ToHexString(recipient.Sent[0].Body));
            Assert.Equal("B80B0000", Convert.ToHexString(recipient.Sent[1].Body));
            Assert.Equal("B80B0000", Convert.ToHexString(recipient.Sent[2].Body));
            Assert.Equal("80A9030000000000", Convert.ToHexString(recipient.Sent[3].Body));
        }
    }

    private static PlayRoomCodec LegacyCodec(IOpcodeTable table, string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        return new PlayRoomCodec(build + "/game", build, schema, play, new Tables(table),
            NullLogger.Instance, TestBuilds.Packets) { SelectMatchRelay = true };
    }

    private static IOpcodeTable Table(string build, bool remap)
    {
        var table = TestBuilds.Opcodes(build);
        return remap ? new OpcodeTable(table.Profile,
            table.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) })) : table;
    }

    private static async Task BurstAsync(IOpcodeTable table, string build, bool modern, Session session,
        RoundCountdownStarted countdown, RoundStarted started)
    {
        if (!modern)
        {
            await RoundStartSender.SendCountdownAsync(TestBuilds.Packets, build, table, false, true,
                session, countdown, default);
            await RoundStartSender.SendStartedAsync(TestBuilds.Packets, build, table, session, started, default);
            return;
        }
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var packets = services.Register<IPacketRouter>(routes);
        using var messages = services.Register<IMessageRouter>(new Messages());
        var context = new PluginContext(new PluginRow { Id = "round-start-pin", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new RoundStartBinding(build + "/game", table, TestBuilds.Packets, build, [1, 1, 1], NullLogger.Instance)
        {
            MapReportBound = true,
        };
        try
        {
            binding.Mount(context);
            Assert.Single(services.GetAll<IMessageSender<RoundCountdownStarted>>());
            Assert.Single(services.GetAll<IMessageSender<RoundStarted>>());
            await binding.SendAsync(session, countdown, default);
            await binding.SendAsync(session, started, default);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<RoundCountdownStarted>>());
        Assert.Empty(services.GetAll<IMessageSender<RoundStarted>>());
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => TryGet(profileId, out var value)
            ? value! : throw new InvalidOperationException(profileId);
        public bool TryGet(string profileId, out IOpcodeTable? value)
        {
            value = profileId == table.Profile.Id ? table : null;
            return value is not null;
        }
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Routes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            Handlers[(protocolName, packet.Opcode)](session, packet, ct);
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Messages : IMessageRouter
    {
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct)
            where T : IMessage => ValueTask.FromResult(true);
    }
}
