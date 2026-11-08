using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises CN2013's restored preset routes and ordered local-view publication.</summary>
public sealed class Cn406PresetMountTests
{
    /// <summary>Uses named opcodes for the five-byte request/reply and the native five-pair bar, then releases effects.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePresetRoutesPublishAndUnload(bool remap)
    {
        const string build = "cn-406";
        const string protocol = "cn-406/game";
        var profile = new ClientProfile(build, "cn", 406, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(row => row with { Value = checked((ushort)(row.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new()
        {
            ["login"] = false, ["equipment"] = true, ["equipment_view"] = true,
        });
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            async Task Request(byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpReqInventoryChangeWeaponPresetIdx"), body), default);
            await Request(Convert.FromHexString("FFFFFFFF04"));
            Assert.Equal(new WeaponPresetChangeRequested(-1, 4), Assert.Single(fixture.Messages.Received));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(new byte[4]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(new byte[6]));
            var reply = Assert.Single(fixture.Services.GetAll<IMessageSender<WeaponPresetChangeReplied>>());
            await reply.SendAsync(session, new(new(-1, 4), 4) { Status = 1 }, default);
            Assert.Equal(table.S2C("gspReqInventoryChangeWeaponPresetIdx"), session.Sent[^1]);
            Assert.Equal("0401000000", Convert.ToHexString(session.Bodies[^1]));
            var view = Assert.Single(fixture.Services.GetAll<IMessageSender<LocalEquipmentViewUpdated>>());
            await view.SendAsync(session, new(42, 300001, [300001, 202001, 201001, 207001], 0, 202001, 0), default);
            Assert.Equal(new[] { table.S2C("gspInventoryViewUpdateView"), table.S2C("gspReqWeaponPreSetUpdate") }, session.Sent.TakeLast(2));
            Assert.Equal(47, session.Bodies[^1].Length);
            Assert.Equal(PresetWire.WriteUpdate(TestBuilds.Packets, build, 0, 42, [202001, 201001, 0, 207001, 0]), session.Bodies[^1]);
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(new byte[5]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<WeaponPresetChangeRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<WeaponPresetChangeReplied>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LocalEquipmentViewUpdated>>());
    }
}
