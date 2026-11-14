using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the kr-1256 equipment frame (S2C 1001) that assigns weapons in round mode 26.</summary>
public sealed class WeaponMasterPacketTests
{
    private const string Build = "kr-1256";

    /// <summary>Two counted lists keep their order and the trailing reason word closes the 32-byte body.</summary>
    [Fact]
    public void EquipmentApplyPinsSeatSlotListItemListAndReason()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "EquipmentApply", new Dictionary<string, object?>
        {
            ["seat"] = 5,
            ["slots"] = new object[] { 0u, 1u },
            ["items"] = new object[] { 100123u, 100456u },
            ["reason"] = 3,
        });
        Assert.Equal(32, body.Length);
        Assert.Equal("05000000" + "02000000" + "0000000001000000" + "02000000" + "1B87010068880100" + "03000000",
            Convert.ToHexString(body));
        var decoded = PacketCodec.Read(TestBuilds.Packets, Build, "EquipmentApply", body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "EquipmentApply", decoded));
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, "EquipmentApply", malformed));
    }

    /// <summary>An empty assignment is the 16-byte minimum; the counts alone decide the length.</summary>
    [Fact]
    public void EmptyListsAreSixteenBytes()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "EquipmentApply", new Dictionary<string, object?>
        {
            ["seat"] = 0, ["slots"] = Array.Empty<object>(), ["items"] = Array.Empty<object>(), ["reason"] = 0,
        });
        Assert.Equal(16, body.Length);
    }

    /// <summary>Only kr-1256 states the frame and the capability; the descendants whose reader is not audited do not.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("kr-812", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    public void OnlyTheAuditedBuildStatesTheFrame(string build, bool audited)
    {
        Assert.Equal(audited, TestBuilds.Packets.CapabilityOf(build, "weapon_master"));
        Assert.Equal(audited ? 4 : 0, TestBuilds.Packets.Resolve(build, "EquipmentApply").Count);
    }

    /// <summary>The frame is numbered 1001 and the revival request 765 on the game connection of kr-1256.</summary>
    [Fact]
    public void ProfileNumbersBothFramesOnTheGameConnection()
    {
        Assert.Equal((ushort)1001, TestBuilds.Opcodes(Build).S2C("gspApplyEquipment_"));
        Assert.Equal((ushort)765, TestBuilds.Opcodes(Build).C2S("gcpReqWeaponMasterRevival_"));
    }

    /// <summary>The revival request has no field: zero bytes round-trip and any byte is refused.</summary>
    [Fact]
    public void RevivalRequestIsZeroBytes()
    {
        Assert.Empty(TestBuilds.Packets.Resolve(Build, "WeaponMasterRevivalRequest"));
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, Build, "WeaponMasterRevivalRequest", new Dictionary<string, object?>()));
        Assert.Equal(new RoundWeaponMasterRevivalRequested(), WeaponMasterBinding.ReadRevival(TestBuilds.Packets, Build, []));
        Assert.Throws<PacketFormatException>(() => WeaponMasterBinding.ReadRevival(TestBuilds.Packets, Build, [0]));
        Assert.Throws<PacketFormatException>(() => WeaponMasterBinding.ReadRevival(TestBuilds.Packets, Build, new byte[4]));
    }

    /// <summary>The mounted request reaches the message router, refuses a body, and fails loud without a handler.</summary>
    [Fact]
    public async Task RevivalRequestIsRoutedAndUnloads()
    {
        var table = TestBuilds.Opcodes(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        const string protocol = Build + "/game";
        var context = fixture.Context(Build, protocol, "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var opcode = table.C2S("gcpReqWeaponMasterRevival_");
        try
        {
            new WeaponMasterBinding(protocol, table, TestBuilds.Packets, Build, NullLogger.Instance).Mount(context);
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundWeaponMasterRevivalRequested>>());
            await fixture.Packets.DispatchAsync(protocol, session, new InboundPacket(opcode, Array.Empty<byte>()), default);
            Assert.IsType<RoundWeaponMasterRevivalRequested>(Assert.Single(fixture.Messages.Received));
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                fixture.Packets.DispatchAsync(protocol, session, new InboundPacket(opcode, new byte[] { 1 }), default).AsTask());
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Packets.DispatchAsync(protocol, session, new InboundPacket(opcode, Array.Empty<byte>()), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundWeaponMasterRevivalRequested>>());
    }

    /// <summary>The sender writes the frame under the profile's opcode, refuses what the client refuses, and unloads.</summary>
    [Fact]
    public async Task SenderWritesTheFrameAndRefusesWhatTheClientRefuses()
    {
        var table = TestBuilds.Opcodes(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Build + "/game", "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var binding = new WeaponMasterBinding(Build + "/game", table, TestBuilds.Packets, Build, NullLogger.Instance);
        try
        {
            binding.Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundEquipmentApplied>>());
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await sender.SendAsync(session, new(2, [0], [100123], 2), default);
            Assert.Equal((ushort)1001, Assert.Single(session.Sent));
            Assert.Equal("02000000" + "01000000" + "00000000" + "01000000" + "1B870100" + "02000000",
                Convert.ToHexString(Assert.Single(session.Bodies)));
            foreach (var invalid in new RoundEquipmentApplied[]
            {
                new(36, [0], [1], 0), new(-1, [0], [1], 0), new(0, [6], [1], 0), new(0, [-1], [1], 0),
                new(0, [0, 1], [1], 0), new(0, [0], [-1], 0),
            })
                await Assert.ThrowsAsync<InvalidDataException>(() => sender.SendAsync(session, invalid, default).AsTask());
            Assert.Single(session.Sent);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundEquipmentApplied>>());
    }

    /// <summary>The dialect option needs the capability and the result scoreboard of mode 26; descendants without audit refuse it.</summary>
    [Theory]
    [InlineData("kr-1256", new[] { 1 }, "result_scoreboard_modes")]
    [InlineData("cn-774", new[] { 1, 26 }, "capability")]
    public async Task OptionIsRefusedWithoutItsPrerequisites(string build, int[] scoreboardModes, string expected)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["weapon_master"] = true, ["result_scoreboard_modes"] = scoreboardModes,
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var failure = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains(expected, failure.Message);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundEquipmentApplied>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The enabled option publishes the sender through the whole dialect row and removes it again.</summary>
    [Fact]
    public async Task EnabledOptionMountsTheSenderThroughTheDialectRow()
    {
        var table = TestBuilds.Opcodes(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["weapon_master"] = true, ["result_scoreboard_modes"] = new[] { 1, 26 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Single(fixture.Services.GetAll<IMessageSender<RoundEquipmentApplied>>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundEquipmentApplied>>());
    }
}
