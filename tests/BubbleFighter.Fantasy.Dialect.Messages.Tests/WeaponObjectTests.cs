using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the independently audited CN2012 weapon drop, creation and pickup frames.</summary>
public sealed class WeaponObjectTests
{
    private const string Position = "0000803F000000C000004040";
    private const string Velocity = "0000003F00000040000080BF";
    private const string Parameters = "3508000004030201FFFFFFFF";
    private const string Request = Position + Velocity + Parameters + "01000000";

    /// <summary>Creation rearranges request fields and adds the room identity; it never echoes the request unchanged.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeWeaponObjectsPinBothDirectionsAndUnload(bool remap)
    {
        var profile = new ClientProfile("cn-324", "cn", 324, 0, 0, "cn-324");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var native = OpcodeTableLoader.Load(Path.Combine(root, "profiles/cn-324/opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, native.All.Select(row => row with { Value = checked((ushort)(row.Value + 1000)) })) : native;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        const string protocol = "cn-324/game";
        var context = fixture.Context("cn-324", protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            var binding = new WeaponObjectsBinding(protocol, table, TestBuilds.All.Tables, "cn-324");
            binding.Mount(context, fixture.Messages);
            async Task Dispatch(string name, byte[] bytes) => await fixture.Packets.DispatchAsync(protocol, session,
                new InboundPacket(table.C2S(name), bytes), default);
            var bytes = Convert.FromHexString(Request);
            Assert.Equal(40, bytes.Length);
            await Dispatch("gcpReqCreateWeaponObject", bytes);
            var drop = Assert.IsType<RoundWeaponDropRequested>(fixture.Messages.Received[^1]);
            Assert.Equal(new(1, -2, 3, 0.5f, 2, -1, 2101, 0x01020304, -1, true), drop);
            await binding.SendAsync(session, new RoundWeaponCreated(0x64000001, drop), default);
            Assert.Equal(table.S2C("gspGameCreateWeaponObject"), session.Sent[^1]);
            Assert.Equal("01000064" + Parameters + Velocity + Position + "01000000", Convert.ToHexString(session.Bodies[^1]));
            Assert.Equal(44, session.Bodies[^1].Length);
            await Dispatch("gcpPickUpWeapon", Convert.FromHexString("01000064E0C81000"));
            Assert.Equal(new RoundWeaponPickupRequested(0x64000001, 1100000, 1), fixture.Messages.Received[^1]);
            await binding.SendAsync(session, new RoundWeaponPicked(0x64000001, 1100000), default);
            Assert.Equal(table.S2C("gspPickUpWeapon"), session.Sent[^1]);
            Assert.Equal("01000064E0C81000", Convert.ToHexString(session.Bodies[^1]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpReqCreateWeaponObject", bytes[..^1]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpReqCreateWeaponObject", [.. bytes, 0]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpPickUpWeapon", new byte[7]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpReqCreateWeaponObject",
                Convert.FromHexString("0000C07F" + Request[8..])));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch("gcpReqCreateWeaponObject", bytes));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundWeaponDropRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundWeaponPickupRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundWeaponCreated>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundWeaponPicked>>());
    }

    /// <summary>Native support is explicit and mounting this feature cannot silently cover an unaudited build.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-279")]
    [InlineData("kr-1256")]
    public void UnauditedBuildsDoNotAdvertiseWeaponObjectSupport(string build) =>
        Assert.False(TestBuilds.All.Tables.CapabilityOf(build, "weapon_objects"));
}
