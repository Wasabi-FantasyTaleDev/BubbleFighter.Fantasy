using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks reviewed native inventory-use routes, bounded decoding, replies and reversible registrations.</summary>
public sealed class InventoryUseMountTests
{
    /// <summary>Incomplete wire configuration fails before publishing item-use or state services.</summary>
    [Theory]
    [InlineData("gcpReqInventoryUseItem")]
    [InlineData("gspRepInventoryUseItem")]
    [InlineData("gspUserStat")]
    public async Task MissingRequiredOpcodePublishesNoServices(string missing)
    {
        var root = RepositoryRoot();
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var loaded = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile);
        var table = new OpcodeTable(profile, loaded.All.Where(entry => entry.Name != missing));
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "inventory-use", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var binding = new InventoryUseBinding("kr-812/game", table, TestBuilds.Packets, "kr-812");
            Assert.Throws<ConfigException>(() => binding.Bind(context, new Messages()));
            Assert.Empty(routes.Handlers);
            Assert.Empty(services.GetAll<IInboundMessageBinding<InventoryItemUseRequested>>());
            Assert.Empty(services.GetAll<IMessageSender<InventoryItemUseReplied>>());
            Assert.Empty(services.GetAll<IMessageSender<UserStatUpdated>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Each build mounts its own opcode pair and preserves the original item-use payload.</summary>
    [Theory]
    [InlineData("kr-812", 384)]
    [InlineData("cn-324", 260)]
    [InlineData("cn-406", 272)]
    [InlineData("cn-717", 384)]
    [InlineData("id-400", 344)]
    [InlineData("id-1809", 348)]
    [InlineData("cn-279", 172)]
    [InlineData("cn-706", 344)]
    public async Task NativeRenameRoutesDecodeAndReplyWithoutServerNotice(string build, int statLength)
    {
        var root = RepositoryRoot();
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var packets = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "inventory-use", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new InventoryUseBinding(build + "/game", table, PacketTables.Load(Path.Combine(root, "packets")), build);
        var session = new Session(profile);
        var payload = Convert.FromHexString("08870100010000001A0000006300680061006E006700650055007300650072004E0061006D0065003D0054006500730074004E0061006D006500300030003100");
        try
        {
            binding.Bind(context, messages);
            Assert.Empty(services.GetAll<IMessageSender<ServerNotice>>());
            Assert.Single(services.GetAll<IInboundMessageBinding<InventoryItemUseRequested>>());
            var handler = routes.Handlers[(build + "/game", table.C2S("gcpReqInventoryUseItem"))];
            await handler(session, new(805, payload), default);
            Assert.Equal(new InventoryItemUseRequested(100104, 1, "changeUserName=TestName001"), Assert.Single(messages.Received));
            foreach (var invalid in new[] { payload[..^1], new byte[] { 0 }, payload.Concat(new byte[] { 0 }).ToArray() })
                await Assert.ThrowsAsync<PacketFormatException>(() => handler(session, new(805, invalid), default).AsTask());
            Assert.Single(messages.Received);
            var sender = Assert.Single(services.GetAll<IMessageSender<InventoryItemUseReplied>>());
            await sender.SendAsync(session, new(0, "changeUserName=TestName001") { ItemId = 100104 }, default);
            var sent = Assert.Single(session.Sent);
            Assert.Equal(table.S2C("gspRepInventoryUseItem"), sent.Opcode);
            Assert.Equal(64, sent.Body.Length);
            Assert.Equal(Convert.FromHexString("0000000008870100"), sent.Body[..8]);
            Assert.Equal(payload[8..], sent.Body[8..]);
            session.Sent.Clear();
            await sender.SendAsync(session, new(0, "") { ItemId = 100180 }, default);
            Assert.Equal(12, Assert.Single(session.Sent).Body.Length);
            Assert.Equal(Convert.FromHexString("000000005487010000000000"), session.Sent[0].Body);
            session.Sent.Clear();
            var stat = Assert.Single(services.GetAll<IMessageSender<UserStatUpdated>>());
            await stat.SendAsync(session, new(new(7, "account", "ab", 17) { ColorIndex = 14 }, new(7, 12345, 678)), default);
            var refresh = Assert.Single(session.Sent);
            Assert.Equal(table.S2C("gspUserStat"), refresh.Opcode);
            Assert.Equal(14, BinaryPrimitives.ReadInt32LittleEndian(refresh.Body));
            Assert.Equal(12345, BinaryPrimitives.ReadInt32LittleEndian(refresh.Body.AsSpan(16)));
            Assert.Equal(678, BinaryPrimitives.ReadInt32LittleEndian(refresh.Body.AsSpan(20)));
            var expected = UserStatWire.Write(TestBuilds.Packets, build, "ab", 17, 12345, 678, 14);
            Assert.Equal(statLength, refresh.Body.Length);
            Assert.Equal(expected, refresh.Body);
            messages.Handled = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler(session, new(805, payload), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<InventoryItemUseRequested>>());
        Assert.Empty(services.GetAll<IMessageSender<InventoryItemUseReplied>>());
        Assert.Empty(services.GetAll<IMessageSender<UserStatUpdated>>());
    }

    /// <summary>
    /// kr-1256 reads the request's counted string list from the table, refuses more than one string unread,
    /// and keeps the modern reply and state refresh bytes pinned by <see cref="InventoryUseKr1256Pins"/>.
    /// </summary>
    [Fact]
    public async Task Kr1256ListRequestKeepsTheModernItemUseBytes()
    {
        var table = TestBuilds.Opcodes("kr-1256");
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var packets = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "inventory-use", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new InventoryUseBinding("kr-1256/game", table, TestBuilds.Packets, "kr-1256");
        var session = new Session(new ClientProfile("kr-1256", "kr", 1256, 0, 0, "kr-1256"));
        try
        {
            binding.Bind(context, messages);
            var requestOpcode = table.C2S("gcpReqInventoryUseItem");
            var handler = routes.Handlers[("kr-1256/game", requestOpcode)];

            await handler(session, new(requestOpcode, Convert.FromHexString("410D03000100000000000000")), default);
            Assert.Equal(new InventoryItemUseRequested(200001, 1, ""), Assert.Single(messages.Received));

            await handler(session, new(requestOpcode, Convert.FromHexString("410D0300010000000200000002000000610062000200000063006400")), default);
            Assert.Single(messages.Received);
            var refused = Assert.Single(session.Sent);
            Assert.Equal(table.S2C("gspRepInventoryUseItem"), refused.Opcode);
            Assert.Equal("01000000410D030000000000", Convert.ToHexString(refused.Body));
            session.Sent.Clear();

            messages.Received.Clear();
            await handler(session, new(requestOpcode, Convert.FromHexString("410D030001000000010000000200000061006200")), default);
            Assert.Equal(new InventoryItemUseRequested(200001, 1, "ab"), Assert.Single(messages.Received));

            var replies = Assert.Single(services.GetAll<IMessageSender<InventoryItemUseReplied>>());
            await replies.SendAsync(session, new(0, "") { ItemId = 100180 }, default);
            Assert.Equal("000000005487010000000000", Convert.ToHexString(Assert.Single(session.Sent).Body));
            session.Sent.Clear();

            var stat = Assert.Single(services.GetAll<IMessageSender<UserStatUpdated>>());
            await stat.SendAsync(session, new(new(1001, "user", "nick", 10) { ColorIndex = 14 }, new(1001, 12345, 678)), default);
            var refresh = Assert.Single(session.Sent);
            Assert.Equal(table.S2C("gspUserStat"), refresh.Opcode);
            Assert.Equal(494, refresh.Body.Length);
            Assert.Equal("0605263A23BCEB22FFFE0866DB319C0A", Convert.ToHexString(SHA256.HashData(refresh.Body))[..32]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<InventoryItemUseRequested>>());
        Assert.Empty(services.GetAll<IMessageSender<InventoryItemUseReplied>>());
        Assert.Empty(services.GetAll<IMessageSender<UserStatUpdated>>());
    }

    /// <summary>A table that retypes the request's item id is refused when the row binds.</summary>
    [Fact]
    public async Task AConsumedFieldWithTheWrongTypeIsRefusedWhenTheRowBinds()
    {
        var directory = PacketTableEdits.WithFieldType("InventoryUseRequest", "item_id", "u32", "wstr");
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var packets = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "inventory-use", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new InventoryUseBinding("kr-812/game", TestBuilds.Opcodes("kr-812"),
            PacketTables.Load(directory.FullName), "kr-812");
        try
        {
            var error = Assert.Throws<ConfigException>(() => binding.Bind(context, new Messages()));
            Assert.Contains("item_id", error.Message, StringComparison.Ordinal);
            Assert.Contains("Inventory item use", error.Message, StringComparison.Ordinal);
            Assert.Empty(routes.Handlers);
            Assert.Empty(services.GetAll<IInboundMessageBinding<InventoryItemUseRequested>>());
            Assert.Empty(services.GetAll<IMessageSender<InventoryItemUseReplied>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public long Id => 1;
        public ClientProfile Profile => profile;
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
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
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) => throw new NotSupportedException();
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Messages : IMessageRouter
    {
        internal List<InventoryItemUseRequested> Received { get; } = [];
        internal bool Handled { get; set; } = true;
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        { Received.Add(Assert.IsType<InventoryItemUseRequested>(message)); return ValueTask.FromResult(Handled); }
    }
}
