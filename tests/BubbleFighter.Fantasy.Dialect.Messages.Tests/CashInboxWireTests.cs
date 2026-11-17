using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the wire binding, frame codecs, layout guards, and login <c>"cash"</c> snapshot publication for
/// the cash item inbox (<c>gcpReqCashInven</c>, <c>gcpReqPickup</c>, <c>gcpReqCloseGiftBox</c>,
/// <c>gspCashInvenUpdateAllInventorySlot</c>, and <c>gspRepPickup</c>).
/// </summary>
public sealed class CashInboxWireTests
{
    private const int SampleUserNum = 42;

    /// <summary>Every enabled build mounts all five cash-inbox routes/senders, dispatches requests, encodes replies, and unwinds cleanly.</summary>
    [Theory]
    [InlineData("kr-812", false, "unknown_52")]
    [InlineData("id-400", false, "unknown_52")]
    [InlineData("id-1809", false, "unknown_52")]
    [InlineData("cn-406", false, "unknown_52")]
    [InlineData("cn-404", false, "unknown_52")]
    [InlineData("cn-408", false, "unknown_52")]
    [InlineData("cn-706", false, "unknown_52")]
    [InlineData("cn-717", false, "unknown_52")]
    [InlineData("cn-324", false, "unknown_52")]
    [InlineData("tw-410", false, "unknown_52")]
    [InlineData("kr-1256", true, "unknown_48")]
    [InlineData("cn-774", true, "unknown_48")]
    [InlineData("tw-454", true, "unknown_48")]
    public async Task BindingMountsDispatchesEncodesAndUnwindsAcrossEnabledBuilds(
        string build,
        bool hasModernPresentationFields,
        string cashTailField)
    {
        var protocol = build + "/game";
        var opcodes = TestBuilds.Opcodes(build).ForConnection(OpcodeConnection.Game);
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var packets = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "cash-inbox", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new CashInboxBinding(protocol, opcodes, TestBuilds.Packets, build);
        var session = new Session(MakeProfile(build));
        try
        {
            binding.Bind(context, messages);

            Assert.Single(services.GetAll<IInboundMessageBinding<CashInboxOpenRequested>>());
            Assert.Single(services.GetAll<IInboundMessageBinding<CashInboxPickupRequested>>());
            Assert.Single(services.GetAll<IInboundMessageBinding<CashInboxCloseRequested>>());
            var listSender = Assert.Single(services.GetAll<IMessageSender<CashInventoryPublished>>());
            var replySender = Assert.Single(services.GetAll<IMessageSender<CashInboxPickupReplied>>());

            var openHandler = routes.Handlers[(protocol, opcodes.C2S("gcpReqCashInven"))];
            await openHandler(session, new(opcodes.C2S("gcpReqCashInven"), ReadOnlyMemory<byte>.Empty), default);
            Assert.IsType<CashInboxOpenRequested>(Assert.Single(messages.Received));
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                openHandler(session, new(opcodes.C2S("gcpReqCashInven"), new byte[] { 0x01 }), default).AsTask());

            var pickupOpcode = opcodes.C2S("gcpReqPickup");
            var pickupHandler = routes.Handlers[(protocol, pickupOpcode)];
            await pickupHandler(session, new(pickupOpcode, Convert.FromHexString("42000080")), default);
            var pickup = Assert.IsType<CashInboxPickupRequested>(messages.Received[^1]);
            Assert.Equal(0x80000042L, pickup.CashSlotId);
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                pickupHandler(session, new(pickupOpcode, new byte[] { 0x01, 0x02, 0x03 }), default).AsTask());
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                pickupHandler(session, new(pickupOpcode, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }), default).AsTask());

            var closeOpcode = opcodes.C2S("gcpReqCloseGiftBox");
            var closeHandler = routes.Handlers[(protocol, closeOpcode)];
            await closeHandler(session, new(closeOpcode, ReadOnlyMemory<byte>.Empty), default);
            Assert.IsType<CashInboxCloseRequested>(messages.Received[^1]);
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                closeHandler(session, new(closeOpcode, new byte[] { 0x00, 0x00 }), default).AsTask());

            await listSender.SendAsync(session, new([]), default);
            var emptySent = Assert.Single(session.Sent);
            Assert.Equal(opcodes.S2C("gspCashInvenUpdateAllInventorySlot"), emptySent.Opcode);
            Assert.Equal(new byte[4], emptySent.Body);
            session.Sent.Clear();

            var entry = new CashInboxEntryRecord(
                77,
                build,
                SampleUserNum,
                1001123,
                2002345,
                1,
                30,
                "EventGift",
                1_700_000_000_000L,
                1_702_592_000_000L,
                CashInboxEntryState.Active)
            {
                SecondaryId = 55,
            };
            await listSender.SendAsync(session, new([entry]), default);
            var populatedSent = Assert.Single(session.Sent);
            Assert.Equal(opcodes.S2C("gspCashInvenUpdateAllInventorySlot"), populatedSent.Opcode);
            var decodedList = PacketCodec.Read(TestBuilds.Packets, build, "CashInventoryUpdate", populatedSent.Body);
            var slots = Assert.IsType<object?[]>(decodedList["slots"]);
            var slot = Assert.IsType<Dictionary<string, object?>>(Assert.Single(slots));
            Assert.Equal(77, slot["cash_slot_id"]);
            Assert.Equal(55, slot["unknown_8"]);
            Assert.Equal((ushort)1, slot["unknown_12"]);
            Assert.Equal((ushort)30, slot["unknown_14"]);
            Assert.Equal(1001123, slot["unknown_16"]);
            Assert.Equal(2002345, slot["item_key"]);
            Assert.Equal("EventGift", slot["unknown_24"]);
            Assert.Equal((byte)0, slot[cashTailField]);
            session.Sent.Clear();

            await replySender.SendAsync(
                session,
                new(77, CashInboxPickupStatus.AlreadyOwned, PresentationCode: 9, PresentationText: "Warn"),
                default);
            var replySent = Assert.Single(session.Sent);
            Assert.Equal(opcodes.S2C("gspRepPickup"), replySent.Opcode);
            var decodedReply = PacketCodec.Read(TestBuilds.Packets, build, "CashPickupReply", replySent.Body);
            Assert.Equal(77, decodedReply["cash_slot_id"]);
            Assert.Equal(8, decodedReply["result"]);
            if (hasModernPresentationFields)
            {
                Assert.Equal(9, decodedReply["unknown_8"]);
                Assert.Equal("Warn", decodedReply["unknown_12"]);
            }
            else
            {
                Assert.Equal(8, replySent.Body.Length);
            }
        }
        finally { await context.UnwindAsync(); }

        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<CashInboxOpenRequested>>());
        Assert.Empty(services.GetAll<IInboundMessageBinding<CashInboxPickupRequested>>());
        Assert.Empty(services.GetAll<IInboundMessageBinding<CashInboxCloseRequested>>());
        Assert.Empty(services.GetAll<IMessageSender<CashInventoryPublished>>());
        Assert.Empty(services.GetAll<IMessageSender<CashInboxPickupReplied>>());
    }

    /// <summary>Status enum members map to the native switch constants proved by <c>kr-812 sub_824310</c>.</summary>
    [Theory]
    [InlineData(CashInboxPickupStatus.Success, 1u)]
    [InlineData(CashInboxPickupStatus.Failed, 2u)]
    [InlineData(CashInboxPickupStatus.AccountMismatch, 7u)]
    [InlineData(CashInboxPickupStatus.AlreadyOwned, 8u)]
    public void PickupStatusMapsToNativeConstants(CashInboxPickupStatus status, uint expectedWord)
    {
        Assert.Equal(expectedWord, CashInboxBinding.ResultWord(status));
    }

    /// <summary>A missing required opcode or retyped packet field fails bind cleanly without leaving partial registrations.</summary>
    [Fact]
    public async Task IncompleteOpcodesOrMutatedLayoutFailsBeforePublishingServices()
    {
        var loaded = TestBuilds.Opcodes("kr-812").ForConnection(OpcodeConnection.Game);
        var missingPickup = new OpcodeTable(MakeProfile("kr-812"),
            loaded.All.Where(entry => entry.Name != "gcpReqPickup"));

        var services = new ServiceRegistry();
        var routes = new Routes();
        using var packets = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "cash-inbox", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var binding = new CashInboxBinding("kr-812/game", missingPickup, TestBuilds.Packets, "kr-812");
            Assert.Throws<ConfigException>(() => binding.Bind(context, new Messages()));
            Assert.Empty(routes.Handlers);
            Assert.Empty(services.GetAll<IMessageSender<CashInboxPickupReplied>>());
        }
        finally { await context.UnwindAsync(); }

        var mutatedDir = PacketTableEdits.WithFieldType("CashPickupRequest", "cash_slot_key", "u32", "wstr");
        var services2 = new ServiceRegistry();
        var routes2 = new Routes();
        using var packets2 = services2.Register<IPacketRouter>(routes2);
        var context2 = new PluginContext(new PluginRow { Id = "cash-inbox", Plugin = "test", Source = "test" },
            services2, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var binding = new CashInboxBinding("kr-812/game", loaded, PacketTables.Load(mutatedDir.FullName), "kr-812");
            var error = Assert.Throws<ConfigException>(() => binding.Bind(context2, new Messages()));
            Assert.Contains("cash_slot_key", error.Message, StringComparison.Ordinal);
            Assert.Empty(routes2.Handlers);
            Assert.Empty(services2.GetAll<IInboundMessageBinding<CashInboxPickupRequested>>());
        }
        finally { await context2.UnwindAsync(); }
    }

    /// <summary>
    /// <see cref="InventoryBinding"/> publishes empty cash slots when unsubscribed and queries
    /// <see cref="CashInboxSnapshotRequested"/> for the build's protocol when subscribed.
    /// </summary>
    [Fact]
    public async Task LoginCashInventoryBurstUsesSubscribedSnapshotAndStaysScopedToProtocol()
    {
        var opcodes = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Game);
        var cashOpcode = opcodes.S2C("gspCashInvenUpdateAllInventorySlot");
        var events = new EventBus(NullLogger.Instance);
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var binding = new InventoryBinding("kr-1256/game", opcodes, TestBuilds.Packets, "kr-1256", scope, events);

        var session = new Session(MakeProfile("kr-1256"));
        var publication = new InventoryReplaced([], []);

        // No subscriber -> plain 4-byte zero count without failing.
        await binding.SendAsync(session, publication, default);
        var unsubscribedCashFrame = Assert.Single(session.Sent, frame => frame.Opcode == cashOpcode);
        Assert.Equal(new byte[4], unsubscribedCashFrame.Body);
        session.Sent.Clear();

        // A subscriber matching another protocol is ignored; only kr-1256/game is honored.
        using var otherProtocol = events.Intercept<CashInboxSnapshotRequested, IReadOnlyList<CashInboxEntryRecord>>(
            async (req, next, ct) => req.Protocol == "cn-774/game"
                ? [SampleEntry("cn-774", 99)]
                : await next(ct).ConfigureAwait(false));
        using var matchingProtocol = events.Intercept<CashInboxSnapshotRequested, IReadOnlyList<CashInboxEntryRecord>>(
            async (req, next, ct) => req.Protocol == "kr-1256/game"
                ? [SampleEntry("kr-1256", 12)]
                : await next(ct).ConfigureAwait(false));

        await binding.SendAsync(session, publication, default);
        var populatedCashFrame = Assert.Single(session.Sent, frame => frame.Opcode == cashOpcode);
        var decoded = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "CashInventoryUpdate", populatedCashFrame.Body);
        var slots = Assert.IsType<object?[]>(decoded["slots"]);
        var slot = Assert.IsType<Dictionary<string, object?>>(Assert.Single(slots));
        Assert.Equal(12, slot["cash_slot_id"]);
        Assert.Equal(1002222, slot["unknown_16"]);
        Assert.Equal(2003333, slot["item_key"]);
        Assert.Equal("Welcome", slot["unknown_24"]);
    }

    private static CashInboxEntryRecord SampleEntry(string profile, long slotId) => new(
        slotId,
        profile,
        SampleUserNum,
        1002222,
        2003333,
        1,
        7,
        "Welcome",
        1_700_000_000_000L,
        1_700_604_800_000L,
        CashInboxEntryState.Active);

    private static ClientProfile MakeProfile(string build) =>
        new(build, build[..2], int.Parse(build[3..]), 0, 0, build);

    private sealed class Session(ClientProfile profile) : ISessionWithFeatures
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(SampleUserNum);
        public long Id => 1;
        public ClientProfile Profile => profile;
        public IConnection Connection { get; } = new Link();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Link : IConnection
    {
        public long Id => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
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
            throw new NotSupportedException();
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Received.Add(message);
            return ValueTask.FromResult(true);
        }
    }
}
