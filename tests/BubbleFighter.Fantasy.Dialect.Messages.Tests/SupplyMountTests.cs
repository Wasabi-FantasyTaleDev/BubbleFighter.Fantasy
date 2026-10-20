using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises installed supply routes and effects with each build's reviewed game opcode table.</summary>
public sealed class SupplyMountTests
{
    /// <summary>Checks complete routing, slot replies and local effects without registering counted-item requests.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-324", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
    public async Task NativeSupplyRoutesAndRepliesUseTheBuildTableAndUnload(string build, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        var tables = TestBuilds.All.Tables;
        try
        {
            new SupplyBinding(protocol, table, tables, build, 128).Mount(context, fixture.Messages);
            new CountedItemsBinding(protocol, table, tables, build).MountEffect(context);
            Assert.Equal(5, fixture.Packets.Handlers.Count);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundCountedItemUseRequested>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemsPrepared>>());
            async Task Dispatch(string name, byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new InboundPacket(table.C2S(name), body), default);
            await Dispatch("gcpGameReportMapData", new byte[64]);
            Assert.Empty(Assert.IsType<RoundSuppliesReported>(fixture.Messages.Received[^1]).Points);
            await Dispatch("gcpReqCreateItem", Convert.FromHexString("0000803F000000C000004040000000000000000000000000650000000000000010270000"));
            Assert.Equal(10000, Assert.IsType<RoundSupplyDropRequested>(fixture.Messages.Received[^1]).CreationClass);
            await Dispatch("gcpEatItem", Convert.FromHexString("0100006265000000"));
            Assert.Equal(new RoundSupplyPickupRequested(0x62000001, 101), fixture.Messages.Received[^1]);
            await Dispatch("gcpUseItem", []);
            Assert.IsType<RoundItemUseRequested>(fixture.Messages.Received[^1]);
            await Dispatch("gcpSwitchItemSlot", []);
            Assert.IsType<RoundItemSlotSwitchRequested>(fixture.Messages.Received[^1]);
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpUseItem", new byte[4]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpSwitchItemSlot", new byte[4]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch("gcpUseItem", []));
            var picked = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyPicked>>());
            await picked.SendAsync(session, new(2, 0x62000001) { ItemKind = 101, ItemSlot = 1 }, default);
            Assert.Equal(new[] { table.S2C("gspItemEaten"), table.S2C("gspAddItemToSlot") }, session.Sent);
            Assert.Equal("0200000001000062", Convert.ToHexString(session.Bodies[0]));
            Assert.Equal("020000006500000001000000", Convert.ToHexString(session.Bodies[1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundItemUsed>>()).SendAsync(session,
                new(2) { Kind = 101, ItemSlot = 1 }, default);
            Assert.Equal(table.S2C("gspItemUsed"), session.Sent[^1]);
            Assert.Equal("020000006500000001000000", Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundCountedItemUsed>>()).SendAsync(session, new(2, 10), default);
            Assert.Equal(table.S2C("gspPveItemUsed"), session.Sent[^1]);
            Assert.Equal("020000000A00000000000000", Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundItemSlotChanged>>()).SendAsync(session, new(2, 0, 102, false), default);
            Assert.Equal(table.S2C("gspSwitchItemSlot"), session.Sent[^1]);
            Assert.Equal("02000000000000006600000000000000", Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>()).SendAsync(session,
                new(0x62000001, new(1, -2, 3, 10, 15000, 30000)), default);
            Assert.Equal(table.S2C("gspGameCreateGameItem"), session.Sent[^1]);
            Assert.Equal("01000062102700000000803F000000C0000040400A00000000000000000000000000000000000000", Convert.ToHexString(session.Bodies[^1]));
            // A random box a team owns carries the team in the envelope's last word (kr-812 sub_519480 -> sub_636F20).
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>()).SendAsync(session,
                new(0x62000002, new(1, -2, 3, 0, 15000, 30000, 2)), default);
            Assert.Equal("02000062102700000000803F000000C0000040400000000000000000000000000000000002000000", Convert.ToHexString(session.Bodies[^1]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundSuppliesReported>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundSupplyPickupRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundSupplyDropRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundItemUseRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundItemSlotSwitchRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundSupplyPicked>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundItemUsed>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundItemSlotChanged>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemUsed>>());
    }

    /// <summary>The 2011 client mounts the shorter supply envelopes: no switch packet, a 28-byte drop and two-word grant and use.</summary>
    [Fact]
    public async Task EarlySupplyRoutesUseTheirShorterEnvelopesAndUnload()
    {
        const string build = "cn-279";
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        Assert.Equal(new ushort[] { 702, 707, 708, 727 }, new[] { "gcpGameReportMapData", "gcpEatItem", "gcpUseItem", "gcpReqCreateItem" }.Select(table.C2S));
        Assert.Equal(new ushort[] { 711, 712, 713, 714 }, new[] { "gspGameCreateGameItem", "gspItemEaten", "gspAddItemToSlot", "gspItemUsed" }.Select(table.S2C));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            new SupplyBinding(protocol, table, TestBuilds.All.Tables, build, 128).Mount(context, fixture.Messages);
            Assert.Equal(4, fixture.Packets.Handlers.Count);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundItemSlotChanged>>());
            async Task Dispatch(string name, byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new InboundPacket(table.C2S(name), body), default);
            await Dispatch("gcpGameReportMapData", new byte[60]);
            Assert.Empty(Assert.IsType<RoundSuppliesReported>(fixture.Messages.Received[^1]).Points);
            await Dispatch("gcpReqCreateItem", Convert.FromHexString("0000803F000000C0000040400000000000000000000000000A000000"));
            var drop = Assert.IsType<RoundSupplyDropRequested>(fixture.Messages.Received[^1]);
            Assert.Equal(10, drop.Point.Kind);
            Assert.Equal(10000, drop.CreationClass);
            await Dispatch("gcpEatItem", Convert.FromHexString("0100006265000000"));
            Assert.Equal(new RoundSupplyPickupRequested(0x62000001, 101), fixture.Messages.Received[^1]);
            await Dispatch("gcpUseItem", []);
            Assert.IsType<RoundItemUseRequested>(fixture.Messages.Received[^1]);
            var picked = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyPicked>>());
            await picked.SendAsync(session, new(2, 0x62000001) { ItemKind = 112, ItemSlot = 0 }, default);
            Assert.Equal(new[] { table.S2C("gspItemEaten"), table.S2C("gspAddItemToSlot") }, session.Sent);
            Assert.Equal("0200000001000062", Convert.ToHexString(session.Bodies[0]));
            Assert.Equal("0200000070000000", Convert.ToHexString(session.Bodies[1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundItemUsed>>()).SendAsync(session, new(2) { Kind = 112 }, default);
            Assert.Equal(table.S2C("gspItemUsed"), session.Sent[^1]);
            Assert.Equal("0200000070000000", Convert.ToHexString(session.Bodies[^1]));
            // The creation envelope ends in three floats and has no team word (cn-279 sub_403820).
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>()).SendAsync(session,
                new(0x62000001, new(1, -2, 3, 0, 15000, 30000)), default);
            Assert.Equal("01000062102700000000803F000000C00000404000000000000000000000000000000000", Convert.ToHexString(session.Bodies[^1]));
            Assert.Equal(36, session.Bodies[^1].Length);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>())
                .SendAsync(session, new(0x62000002, new(1, -2, 3, 0, 15000, 30000, 1)), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>());
    }

    /// <summary>Unavailable native supply families fail before publishing any service or packet route.</summary>
    [Theory]
    [InlineData("cn-706")]
    public async Task UnreviewedSupplySurfaceCannotInstallRoutes(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var table = new OpcodeTable(profile, []);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game");
        Assert.Throws<ConfigException>(() => new SupplyBinding(build + "/game", table, TestBuilds.All.Tables, build, 128).Mount(context, fixture.Messages));
        Assert.Throws<ConfigException>(() => new CountedItemsBinding(build + "/game", table, TestBuilds.All.Tables, build).MountEffect(context));
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemUsed>>());
        await context.UnwindAsync();
    }
}
