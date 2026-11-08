using System.Buffers;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins modern byte-team supply envelopes and checks their integration with native round loading.</summary>
public sealed class ModernSupplyTests
{
    /// <summary>The complete room plugin gives map reports to the shared supply binding and removes every owned route on unload.</summary>
    [Theory]
    [InlineData("kr-1256", 636, 637, 638, 639, 640, 658, 12)]
    [InlineData("cn-774", 609, 610, 611, 612, 613, 647, 16)]
    [InlineData("tw-454", 609, 610, 611, 612, 613, 647, 16)]
    public async Task ModernSupplyRoutesComposeWithRoundLoading(string build, int create, int pickup, int grant,
        int use, int direct, int selection, int selectionBytes)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var table = full.ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(full);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["relay_protocol"] = build + "/relay", ["supply_items"] = true, ["counted_pve_items"] = true,
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            async Task Dispatch(string name, byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S(name), body), default);
            Assert.Equal((ushort)605, table.C2S("gcpEatItem"));
            Assert.Equal((ushort)606, table.C2S("gcpUseItem"));
            Assert.Equal((ushort)644, table.C2S("gcpSwitchItemSlot"));
            await Dispatch("gcpGameReportMapData", MapReport());
            var report = Assert.IsType<RoundSuppliesReported>(fixture.Messages.Received[^1]);
            Assert.True(report.IntervalsStated);
            Assert.Equal(new RoundSupplyPoint(1, -2, 3, 109, 15000, 30000, 2), Assert.Single(report.Points));
            await Dispatch("gcpReqCreateItem", Convert.FromHexString(
                "0000803F000000C0000040400000000000000000000000006D0000000110270000"));
            Assert.Equal(10000, Assert.IsType<RoundSupplyDropRequested>(fixture.Messages.Received[^1]).CreationClass);
            await Dispatch("gcpEatItem", Convert.FromHexString("010000626D000000"));
            Assert.Equal(new RoundSupplyPickupRequested(0x62000001, 109), fixture.Messages.Received[^1]);
            await Dispatch("gcpUseItem", []);
            Assert.IsType<RoundItemUseRequested>(fixture.Messages.Received[^1]);
            await Dispatch("gcpSwitchItemSlot", []);
            Assert.IsType<RoundItemSlotSwitchRequested>(fixture.Messages.Received[^1]);
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundCountedItemUseRequested>>());
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpUseItem", [0]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpSwitchItemSlot", [0]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch("gcpGameReportMapData", new byte[60]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>()).SendAsync(session,
                new(0x62000001, new(1, -2, 3, 109, 15000, 30000, 2)), default);
            Assert.Equal((ushort)create, session.Sent[^1]);
            Assert.Equal("01000062102700000000803F000000C0000040406D00000000000000000000000000000002",
                Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>()).SendAsync(session,
                new(0x62000002, new(1, -2, 3, 109, 15000, 30000, 2)) { CreationClass = 20000 }, default);
            Assert.Equal("02000062204E00000000803F000000C0000040406D00000000000000000000000000000002",
                Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundSupplyPicked>>()).SendAsync(session,
                new(2, 0x62000001) { ItemKind = 109, ItemSlot = 1 }, default);
            Assert.Equal((ushort)pickup, session.Sent[^2]);
            Assert.Equal("0200000001000062", Convert.ToHexString(session.Bodies[^2]));
            Assert.Equal((ushort)grant, session.Sent[^1]);
            Assert.Equal("020000006D00000001000000", Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundItemUsed>>()).SendAsync(session,
                new(2) { Kind = 109, ItemSlot = 1 }, default);
            Assert.Equal((ushort)use, session.Sent[^1]);
            Assert.Equal("020000006D00000001000000", Convert.ToHexString(session.Bodies[^1]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundCountedItemUsed>>()).SendAsync(session, new(2, 10), default);
            Assert.Equal((ushort)direct, session.Sent[^1]);
            Assert.Equal("020000000A00000000000000", Convert.ToHexString(session.Bodies[^1]));
            var switcher = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundItemSlotChanged>>());
            await switcher.SendAsync(session, new(2, 1, 109, false), default);
            Assert.Equal((ushort)selection, session.Sent[^1]);
            var expected = "02000000010000006D000000" + (selectionBytes == 16 ? "00000000" : "");
            Assert.Equal(expected, Convert.ToHexString(session.Bodies[^1]));
            await switcher.SendAsync(session, new(2, 1, 109, true), default);
            Assert.Equal("02000000010000006D000000" + (selectionBytes == 16 ? "01000000" : ""),
                Convert.ToHexString(session.Bodies[^1]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundSuppliesReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundCountedItemUseRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemsPrepared>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemUsed>>());
    }

    /// <summary>Modern map vectors keep byte teams, bounded counts, finite coordinates and complete-body validation.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernReportsAndDropsRejectMalformedBodies(string build)
    {
        var tables = TestBuilds.All.Tables;
        var body = MapReport();
        Assert.Equal(85, body.Length);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, body[..^1], 128));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, [.. body, 0], 128));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, MapReport(team: 3), 128));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, MapReport(x: float.NaN), 128));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, MapReport(kindCount: 0), 128));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadReport(tables, build, new byte[] { 0, 0, 0, 0, 129, 0, 0, 0 }, 128));
        var drop = Convert.FromHexString("0000803F000000C0000040400000000000000000000000006D0000000110270000");
        Assert.Equal(33, drop.Length);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(tables, build, drop[..^1]));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(tables, build, [.. drop, 0]));
        BitConverter.GetBytes(float.NaN).CopyTo(drop, 0);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(tables, build, drop));
    }

    private static byte[] MapReport(byte team = 2, float x = 1, int kindCount = 1)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        writer.WriteInt32(1);
        writer.WriteSingle(x); writer.WriteSingle(-2); writer.WriteSingle(3);
        writer.WriteInt32(kindCount);
        if (kindCount > 0) writer.WriteInt32(109);
        writer.WriteInt32(1); writer.WriteByte(team);
        writer.WriteInt32(1); writer.WriteInt32(30000);
        writer.WriteInt32(1); writer.WriteInt32(15000);
        for (var i = 0; i < 9; i++) writer.WriteInt32(0);
        return buffer.WrittenSpan.ToArray();
    }
}
