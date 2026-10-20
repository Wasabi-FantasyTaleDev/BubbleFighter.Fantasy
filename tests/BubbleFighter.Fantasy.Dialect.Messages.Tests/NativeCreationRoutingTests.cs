using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises native creation bindings against each audited build's real opcode table.</summary>
public sealed class NativeCreationRoutingTests
{
    /// <summary>
    /// A layout that keeps a consumed field's name but changes its type is refused when the row binds: the create
    /// request's character type is cast to an int, so a string there would fail on the first creation instead.
    /// </summary>
    [Fact]
    public void ACreationFieldWithTheWrongTypeIsRefusedWhenTheRowBinds()
    {
        var directory = PacketTableEdits.WithFieldType("CharacterCreateRequest", "character_type", "u32", "wstr");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        using var registration = services.Register<IPacketRouter>(new RecordingPackets());
        var context = new PluginContext(new PluginRow { Id = "creation", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new CharacterCreationBinding("kr-812/game", table, PacketTables.Load(directory.FullName), "kr-812");
        try
        {
            var error = Assert.Throws<ConfigException>(() => binding.Bind(context, new RecordingMessages()));
            Assert.Contains("CharacterCreateRequest.character_type", error.Message, StringComparison.Ordinal);
            Assert.Empty(services.GetAll<IMessageSender<CharacterCreationOpened>>());
        }
        finally { context.Unwind(); }
    }

    [Theory]
    [InlineData("kr-812", 12, 24)]
    [InlineData("cn-717", 12, 24)]
    [InlineData("id-400", 12, 20)]
    [InlineData("id-1809", 12, 20)]
    [InlineData("cn-324", 11, 20)]
    [InlineData("cn-279", 11, 20)]
    [InlineData("kr-812", 12, 24, true)]
    [InlineData("cn-324", 11, 20, true)]
    public async Task NativeRequestsReachSharedMessagesAndUnloadCleanly(string build, ushort notice, int createLength, bool remap = false)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        IOpcodeTable table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        if (remap)
        {
            table = new OpcodeTable(profile, table.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) }));
            notice += 5000;
        }
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "character_creation"));
        var protocol = build + "/game";
        var services = new ServiceRegistry();
        var packets = new RecordingPackets();
        using var registration = services.Register<IPacketRouter>(packets);
        var context = new PluginContext(new PluginRow { Id = "creation", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var messages = new RecordingMessages();
        var binding = new CharacterCreationBinding(protocol, table, TestBuilds.Packets, build);
        var session = new RecordingSession(profile);
        try
        {
            binding.Bind(context, messages);
            await binding.SendAsync(session, new CharacterCreationOpened(), default);
            Assert.Equal(notice, Assert.Single(session.Sent).Opcode);
            Assert.Empty(session.Sent[0].Payload);
            var name = PacketCodec.Write(TestBuilds.Packets, build, "CharacterNameRequest", new Dictionary<string, object?> { ["nickname"] = "ab" });
            Assert.Equal(8, name.Length);
            await packets.DispatchAsync(protocol, session,
                new(CharacterCreationBinding.ResolveNameRequestOpcode(table), name), default);
            var create = PacketCodec.Write(TestBuilds.Packets, build, "CharacterCreateRequest",
                new Dictionary<string, object?> { ["nickname"] = "ab", ["character_type"] = 2 });
            Assert.Equal(createLength, create.Length);
            await packets.DispatchAsync(protocol, session, new(table.C2S("gcpReqCreateUserInfo"), create), default);
            await packets.DispatchAsync(protocol, session, new(table.C2S("gcpLoginAfterCreateUserInfo"), ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(new CharacterNameRequested("ab"), messages.Received[0]);
            Assert.Equal(new CharacterCreateRequested("ab", 2), messages.Received[1]);
            Assert.IsType<CharacterLoginRequested>(messages.Received[2]);
            await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpLoginAfterCreateUserInfo"), new byte[1]), default).AsTask());
            Assert.Equal(3, messages.Received.Count);
            await binding.SendAsync(session, new CharacterNameReplied(0, "ab"), default);
            await binding.SendAsync(session, new CharacterCreateReplied(0), default);
            Assert.Equal(12, session.Sent[1].Payload.Length);
            Assert.Equal(new byte[4], session.Sent[2].Payload);
        }
        finally { context.Unwind(); }
        Assert.Empty(packets.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<CharacterCreationOpened>>());
    }

    [Fact]
    public void ConflictingOrMissingNameAliasesFailBeforeRegistration()
    {
        var profile = new ClientProfile("test", "kr", 0, 0, 0, "test");
        var table = new OpcodeTable(profile, [
            new(15, PacketDirection.ClientToServer, "gcpReqCheckCreateUserName", Evidence.Verified, null, null),
            new(99, PacketDirection.ClientToServer, "gcpReqCheckCreateUserName_", Evidence.Verified, null, null)]);
        Assert.Throws<ConfigException>(() => CharacterCreationBinding.ResolveNameRequestOpcode(table));
        Assert.Throws<ConfigException>(() => CharacterCreationBinding.ResolveNameRequestOpcode(new OpcodeTable(profile, [])));
    }

    [Fact]
    public void ReviewedAliasDoesNotConsultTheCandidateCatalog()
    {
        var profile = new ClientProfile("test", "kr", 0, 0, 0, "test");
        var table = new OpcodeTable(profile,
            [new(615, PacketDirection.ClientToServer, "gcpReqCheckCreateUserName", Evidence.Verified, null, null)],
            (_, _, _) => throw new InvalidOperationException("Reviewed names must win over candidates."));
        Assert.Equal((ushort)615, CharacterCreationBinding.ResolveNameRequestOpcode(table));
    }

    [Fact]
    public void MissingReplyDoesNotLeavePartialBindingsAndDeferredBuildsStayDisabled()
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile);
        var table = new OpcodeTable(profile, full.All.Where(entry => entry.Name != "gspRepCreateUserInfo"));
        var services = new ServiceRegistry();
        var packets = new RecordingPackets();
        using var registration = services.Register<IPacketRouter>(packets);
        var context = new PluginContext(new PluginRow { Id = "creation", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var binding = new CharacterCreationBinding("kr-812/game", table, TestBuilds.Packets, "kr-812");
            Assert.Throws<ConfigException>(() => binding.Bind(context, new RecordingMessages()));
            Assert.Empty(packets.Handlers);
            Assert.Empty(services.GetAll<IMessageSender<CharacterCreationOpened>>());
        }
        finally { context.Unwind(); }
        foreach (var build in new[] { "cn-406", "cn-706", "cn-735" })
            Assert.False(TestBuilds.Packets.CapabilityOf(build, "character_creation"));
    }

    private sealed class RecordingMessages : IMessageRouter
    {
        public List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Received.Add(message);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingPackets : IPacketRouter
    {
        public Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            Handlers[(protocolName, packet.Opcode)](session, packet, ct);
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
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
}
