using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins NPC control words when modern builds have a different tutorial completion envelope.</summary>
public sealed class NpcWireTests
{
    /// <summary>Each operation requires its own complete scalar signature, but no other NPC or tutorial structure.</summary>
    [Theory]
    [InlineData(true, "u32")]
    [InlineData(false, "u32")]
    [InlineData(true, "u16")]
    [InlineData(false, "u16")]
    public void EachOperationValidatesOnlyItsOwnLayout(bool wave, string type)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-npc-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "builds.yaml"), "version: 1\nbuilds:\n  test: {}\n");
            var structure = wave ? "NpcWave" : "NpcSpawnGroup";
            var field = wave ? "wave" : "spawn_group";
            File.WriteAllText(Path.Combine(directory, "npc.yaml"),
                $"version: 1\nstructs:\n  {structure}:\n    - {field}: {type}\n");
            var tables = PacketTables.Load(directory);
            byte[] Write() => wave ? NpcWire.WriteStartNewWave(tables, "test", 0x01020304)
                : NpcWire.WriteCreateMobsFromSpawnGroup(tables, "test", 0x01020304);
            int Read() => wave ? NpcWire.ReadStartNewWave(tables, "test", new byte[] { 4, 3, 2, 1 })
                : NpcWire.ReadCreateMobsFromSpawnGroup(tables, "test", new byte[] { 4, 3, 2, 1 });
            if (type == "u32")
            {
                Assert.Equal(new byte[] { 4, 3, 2, 1 }, Write());
                Assert.Equal(0x01020304, Read());
            }
            else
            {
                Assert.Throws<ConfigException>(() => Write());
                Assert.Throws<ConfigException>(() => Read());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>The legacy wave sender and inbound adapter preserve start/end words without tutorial state.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    public async Task LegacyWaveAdaptersUseOnlyTheNpcScalarLayout(string build)
    {
        var (schema, play) = TestBuilds.Of(build);
        var tables = new Tables(TestBuilds.Opcodes(build));
        var session = new RecordingSession(tables.Get(build).Profile);
        var messages = new WaveRouter();
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, tables, NullLogger.Instance, messages: messages);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play, tables,
            NullLogger.Instance, TestBuilds.Packets);
        byte[] body = [4, 3, 2, 1];
        foreach (var started in new[] { true, false })
        {
            var request = started ? "gcpReqStartNewWave" : "gcpReqEndWave";
            var reply = started ? "gspRepStartNewWave" : "gspRepEndWave";
            var packet = new InboundPacket(tables.Get(build).C2S(request), body);
            if (started) await handler.HandleStartNewWaveAsync(session, packet, default);
            else await handler.HandleEndWaveAsync(session, packet, default);
            Assert.Equal(new RoundWaveReported(0x01020304, started), messages.Last);
            Assert.Empty(session.Sent);

            await codec.SendAsync(session, new RoundWaveChanged(0x01020304, started), default);
            var sent = Assert.Single(session.Sent);
            Assert.Equal(tables.Get(build).S2C(reply), sent.Opcode);
            Assert.Equal(body, sent.Payload);
            session.Sent.Clear();
        }
    }

    /// <summary>Reads and writes ordinary NPC words without requiring the legacy tutorial completion field.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernNpcControlWordsDoNotRequireTheLegacyTutorialLayout(string build)
    {
        Assert.Equal("unknown_0:u32,unknown_4:bool",
            PacketLayoutContract.Signature(TestBuilds.Packets, build, "TutorialEndStage"));
        var wave = NpcWire.WriteStartNewWave(TestBuilds.Packets, build, -1);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, wave);
        Assert.Equal(-1, NpcWire.ReadStartNewWave(TestBuilds.Packets, build, wave));
        var spawn = NpcWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 0x01020304);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, spawn);
        Assert.Equal(0x01020304, NpcWire.ReadCreateMobsFromSpawnGroup(TestBuilds.Packets, build, spawn));
        foreach (var malformed in new[] { Array.Empty<byte>(), new byte[1], new byte[2], new byte[3], new byte[5] })
        {
            Assert.Throws<PacketFormatException>(() =>
                NpcWire.ReadStartNewWave(TestBuilds.Packets, build, malformed));
            Assert.Throws<PacketFormatException>(() =>
                NpcWire.ReadCreateMobsFromSpawnGroup(TestBuilds.Packets, build, malformed));
        }
    }

    private sealed class RecordingSession(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class WaveRouter : IMessageRouter
    {
        public RoundWaveReported? Last { get; private set; }
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct)
            where T : IMessage
        {
            Assert.Equal(session.Profile.Id + "/game", protocol);
            Last = Assert.IsType<RoundWaveReported>(message);
            return ValueTask.FromResult(true);
        }
    }
}
