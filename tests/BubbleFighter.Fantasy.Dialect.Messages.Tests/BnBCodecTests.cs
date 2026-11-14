using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins BnB relay words, counted generation/grid pairs, decoder bounds and binding effects.</summary>
public sealed class BnBCodecTests
{
    private const string Build = "kr-1256";
    private const string RelayProtocol = Build + "/relay";
    private static readonly (string Request, string Reply, ushort Value)[] Routes =
    [
        ("gcpBnBPlaceBomb_", "gspBnBPlaceBomb_", 38285),
        ("gcpBnBExplodeBombs_", "gspBnBExplodeBombs_", 38286),
        ("gcpBnBPickUpItem_", "gspBnBPickUpItem_", 38287),
    ];

    /// <summary>Placement and pickup preserve both complete native words and reject every missing or extra byte.</summary>
    [Fact]
    public void PlacementAndPickupPinActorThenGrid()
    {
        var expected = Convert.FromHexString("44332211DDCCBBAA");
        const int actor = 0x11223344;
        var grid = unchecked((int)0xAABBCCDD);
        Assert.Equal(8, BnBBinding.WritePlacement(TestBuilds.Packets, Build, new(actor, grid)).Length);
        Assert.Equal(expected, BnBBinding.WritePlacement(TestBuilds.Packets, Build, new(actor, grid)));
        Assert.Equal(expected, BnBBinding.WritePickup(TestBuilds.Packets, Build, new(actor, grid)));
        Assert.Equal(new RoundBnBPlacementReported(actor, grid), BnBBinding.ReadPlacement(TestBuilds.Packets, Build, expected));
        Assert.Equal(new RoundBnBPickupReported(actor, grid), BnBBinding.ReadPickup(TestBuilds.Packets, Build, expected));
        foreach (var malformed in Enumerable.Range(0, expected.Length).Select(length => expected[..length]).Append([.. expected, 0]))
        {
            Assert.Throws<PacketFormatException>(() => BnBBinding.ReadPlacement(TestBuilds.Packets, Build, malformed));
            Assert.Throws<PacketFormatException>(() => BnBBinding.ReadPickup(TestBuilds.Packets, Build, malformed));
        }
    }

    /// <summary>The explosion list is actor, count, then generation before grid for each entry, including generation zero.</summary>
    [Fact]
    public void ExplosionPinsCountPairOrderAndEmptyList()
    {
        var expected = Convert.FromHexString("44332211020000000000000007000000FFFFFFFF00000080");
        RoundBnBBomb[] bombs = [new(0, 7), new(-1, int.MinValue)];
        var written = BnBBinding.WriteExplosion(TestBuilds.Packets, Build, new(0x11223344, bombs));
        Assert.Equal(24, written.Length);
        Assert.Equal(expected, written);
        var read = BnBBinding.ReadExplosion(TestBuilds.Packets, Build, expected);
        Assert.Equal(0x11223344, read.Actor);
        Assert.Equal(bombs, read.Bombs);
        var empty = Convert.FromHexString("4433221100000000");
        Assert.Equal(empty, BnBBinding.WriteExplosion(TestBuilds.Packets, Build, new(0x11223344, [])));
        Assert.Empty(BnBBinding.ReadExplosion(TestBuilds.Packets, Build, empty).Bombs);
        foreach (var malformed in Enumerable.Range(0, expected.Length).Select(length => expected[..length]).Append([.. expected, 0]))
            Assert.Throws<PacketFormatException>(() => BnBBinding.ReadExplosion(TestBuilds.Packets, Build, malformed));
    }

    /// <summary>Impossible counts and a count unsupported by the remaining body never produce a partial report.</summary>
    [Theory]
    [InlineData("01000000FFFFFFFF")]
    [InlineData("01000000FF1F0000")]
    [InlineData("01000000FE1F0000")]
    [InlineData("01000000020000000000000007000000")]
    public void ExplosionRejectsImpossibleOrTruncatedCounts(string hex) =>
        Assert.Throws<PacketFormatException>(() => BnBBinding.ReadExplosion(TestBuilds.Packets, Build, Convert.FromHexString(hex)));

    /// <summary>The native body ceiling admits 8,190 eight-byte pairs after its two words, but never one more.</summary>
    [Fact]
    public void ExplosionReadAndWriteEnforceTheNativeBodyCeiling()
    {
        var bombs = Enumerable.Range(0, 8190).Select(index => new RoundBnBBomb(index, index + 1)).ToArray();
        var body = BnBBinding.WriteExplosion(TestBuilds.Packets, Build, new(17, bombs));
        Assert.Equal(65528, body.Length);
        var read = BnBBinding.ReadExplosion(TestBuilds.Packets, Build, body);
        Assert.Equal(bombs, read.Bombs);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BnBBinding.WriteExplosion(TestBuilds.Packets, Build, new(17, [.. bombs, new(8190, 8191)])));
        Assert.Throws<PacketFormatException>(() => BnBBinding.ReadExplosion(TestBuilds.Packets, Build, new byte[65536]));
    }

    /// <summary>The audited KR1256 structures are required; empty baseline placeholders cannot mount the exchange.</summary>
    [Fact]
    public void LayoutValidationRequiresTheCompleteAuditedBodies()
    {
        BnBBinding.RequireLayouts(TestBuilds.Packets, Build);
        Assert.Throws<ConfigException>(() => BnBBinding.RequireLayouts(TestBuilds.Packets, "kr-812"));
    }

    /// <summary>The native assignments exist in both relay directions and are absent from the game connection.</summary>
    [Fact]
    public void ProfileRowsResolveInTheRelayConnection()
    {
        var game = TestBuilds.Opcodes(Build);
        var relay = game.ForConnection(OpcodeConnection.Relay);
        foreach (var (request, reply, value) in Routes)
        {
            Assert.Null(game.Find(PacketDirection.ClientToServer, request));
            Assert.Null(game.Find(PacketDirection.ServerToClient, reply));
            Assert.Equal(value, relay.C2S(request));
            Assert.Equal(value, relay.S2C(reply));
        }
    }

    /// <summary>Both transports use relay-space mappings, dispatch once and remove every binding effect on unload.</summary>
    [Theory]
    [InlineData(Build + "/relay", false)]
    [InlineData(Build + "/relay", true)]
    [InlineData(Build + "/game", false)]
    [InlineData(Build + "/game", true)]
    public async Task RelayBindingDispatchesPublishesAndUnloads(string transport, bool remap)
    {
        var table = Table(remap).ForConnection(OpcodeConnection.Relay);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, transport, "test.bnb");
        context.BindServices(Declaration());
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var pair = Convert.FromHexString("6500000007000000");
        var explosion = Convert.FromHexString("65000000010000000900000007000000");
        try
        {
            new BnBBinding(transport, table, TestBuilds.Packets, Build, NullLogger.Instance).Mount(context);
            Assert.Equal(3, fixture.Packets.Handlers.Count);
            Assert.All(fixture.Packets.Handlers.Keys, key => Assert.Equal(transport, key.Item1));
            foreach (var (request, _, _) in Routes)
                await Request(request, request == "gcpBnBExplodeBombs_" ? explosion : pair);
            Assert.Equal(new RoundBnBPlacementReported(101, 7), fixture.Messages.Received[0]);
            var reported = Assert.IsType<RoundBnBExplosionReported>(fixture.Messages.Received[1]);
            Assert.Equal(101, reported.Actor);
            Assert.Equal(new RoundBnBBomb(9, 7), Assert.Single(reported.Bombs));
            Assert.Equal(new RoundBnBPickupReported(101, 7), fixture.Messages.Received[2]);
            Assert.Empty(session.Sent);

            Assert.Equal(transport, Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBPlacementReported>>()).Protocol);
            Assert.Equal(transport, Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBExplosionReported>>()).Protocol);
            Assert.Equal(transport, Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBPickupReported>>()).Protocol);
            var placementSender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBnBPlacementPublished>>());
            var explosionSender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBnBExplosionPublished>>());
            var pickupSender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBnBPickupPublished>>());
            Assert.Equal(transport, placementSender.Protocol);
            Assert.Equal(transport, explosionSender.Protocol);
            Assert.Equal(transport, pickupSender.Protocol);
            await placementSender.SendAsync(session, new(101, 7), default);
            await explosionSender.SendAsync(session, new(101, [new(9, 7)]), default);
            await pickupSender.SendAsync(session, new(101, 7), default);
            Assert.Equal(Routes.Select(route => table.S2C(route.Reply)), session.Sent);
            Assert.Equal(pair, session.Bodies[0]);
            Assert.Equal(explosion, session.Bodies[1]);
            Assert.Equal(pair, session.Bodies[2]);

            foreach (var (request, _, _) in Routes)
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(request, [0]));
            Assert.Equal(3, fixture.Messages.Received.Count);
            fixture.Messages.HasHandler = false;
            foreach (var (request, _, _) in Routes)
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Request(request, request == "gcpBnBExplodeBombs_" ? explosion : pair));
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(() => placementSender.SendAsync(session, new(101, 7), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBPlacementReported>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBExplosionReported>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBPickupReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBnBPlacementPublished>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBnBExplosionPublished>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBnBPickupPublished>>());

        Task Request(string name, byte[] body) =>
            fixture.Packets.DispatchAsync(transport, session, new(table.C2S(name), body), default).AsTask();
    }

    /// <summary>Game fallback and relay handlers coexist in one row without changing their shared opcode names.</summary>
    [Fact]
    public async Task BothTransportMountsCoexistAndUnloadTogether()
    {
        var table = Table(true).ForConnection(OpcodeConnection.Relay);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Build + "/game", "test.bnb");
        context.BindServices(Declaration());
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            foreach (var transport in new[] { Build + "/game", RelayProtocol })
                new BnBBinding(transport, table, TestBuilds.Packets, Build, NullLogger.Instance).Mount(context);
            Assert.Equal(6, fixture.Packets.Handlers.Count);
            var senders = fixture.Services.GetAll<IMessageSender<RoundBnBPlacementPublished>>();
            Assert.Equal([Build + "/game", RelayProtocol], senders.Select(sender => sender.Protocol).Order());
            foreach (var sender in senders)
            {
                await fixture.Packets.DispatchAsync(sender.Protocol, session,
                    new(table.C2S("gcpBnBPlaceBomb_"), Convert.FromHexString("6500000007000000")), default);
                await sender.SendAsync(session, new(101, 7), default);
            }
            Assert.Equal(2, fixture.Messages.Received.Count);
            Assert.All(fixture.Messages.Received, report => Assert.Equal(new RoundBnBPlacementReported(101, 7), report));
            Assert.Equal([table.S2C("gspBnBPlaceBomb_"), table.S2C("gspBnBPlaceBomb_")], session.Sent);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBPlacementReported>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBExplosionReported>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBPickupReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBnBPlacementPublished>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBnBExplosionPublished>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBnBPickupPublished>>());
    }

    /// <summary>A missing opcode in either direction is refused before any router or service registration.</summary>
    [Theory]
    [InlineData("gcpBnBPlaceBomb_")]
    [InlineData("gcpBnBExplodeBombs_")]
    [InlineData("gcpBnBPickUpItem_")]
    [InlineData("gspBnBPlaceBomb_")]
    [InlineData("gspBnBExplodeBombs_")]
    [InlineData("gspBnBPickUpItem_")]
    public async Task MissingOpcodeFailsBeforePublishingEffects(string missing)
    {
        var all = Table(false);
        var table = new OpcodeTable(all.Profile, all.ForConnection(OpcodeConnection.Relay).All.Where(entry => entry.Name != missing))
            .ForConnection(OpcodeConnection.Relay);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, RelayProtocol, "test.bnb");
        context.BindServices(Declaration());
        try
        {
            Assert.Throws<ConfigException>(() => new BnBBinding(RelayProtocol, table, TestBuilds.Packets, Build, NullLogger.Instance).Mount(context));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBnBPlacementReported>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBnBPlacementPublished>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A game table cannot supply the BnB exchange even when it contains names matching the relay rows.</summary>
    [Fact]
    public async Task GameOpcodeTableCannotMountTheRelayBinding()
    {
        var game = Table(false);
        using var fixture = new MatchMessagesMountTests.Fixture(game);
        var context = fixture.Context(Build, RelayProtocol, "test.bnb");
        try
        {
            Assert.Throws<ConfigException>(() => new BnBBinding(RelayProtocol, game, TestBuilds.Packets, Build, NullLogger.Instance).Mount(context));
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    private static PluginAttribute Declaration() => new("test.bnb")
    {
        Requires = [typeof(IPacketRouter), typeof(IMessageRouter)],
        ProvidesMany =
        [
            typeof(IInboundMessageBinding<RoundBnBPlacementReported>), typeof(IInboundMessageBinding<RoundBnBExplosionReported>),
            typeof(IInboundMessageBinding<RoundBnBPickupReported>), typeof(IMessageSender<RoundBnBPlacementPublished>),
            typeof(IMessageSender<RoundBnBExplosionPublished>), typeof(IMessageSender<RoundBnBPickupPublished>),
        ],
    };

    private static OpcodeTable Table(bool remap)
    {
        var entries = new List<OpcodeEntry>();
        foreach (var (request, reply, value) in Routes)
        {
            entries.Add(new(checked((ushort)(value + (remap ? 1000 : 0))), PacketDirection.ClientToServer,
                request, Evidence.Verified, null, null, "coined", OpcodeConnection.Relay));
            entries.Add(new(checked((ushort)(value + (remap ? 2000 : 0))), PacketDirection.ServerToClient,
                reply, Evidence.Verified, null, null, "coined", OpcodeConnection.Relay));
            entries.Add(new(checked((ushort)(value + 3000)), PacketDirection.ClientToServer,
                request, Evidence.Verified, null, null, "coined", OpcodeConnection.Game));
            entries.Add(new(checked((ushort)(value + 4000)), PacketDirection.ServerToClient,
                reply, Evidence.Verified, null, null, "coined", OpcodeConnection.Game));
        }
        return new(new ClientProfile(Build, "kr", 1256, 0, 0, Build), entries);
    }
}
