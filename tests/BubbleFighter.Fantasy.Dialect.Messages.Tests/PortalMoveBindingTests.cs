using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the map-contact exchange, its corrected game opcode and reversible typed activation.</summary>
public sealed class PortalMoveBindingTests
{
    /// <summary>The request is a contact vector and short region key; the reply uses a seat, never an object handle.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativeMapPortalBodiesPinExactWidthsAndValues(string build)
    {
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "map_portals"));
        var body = Convert.FromHexString("0000803F000020C00000C8423412");
        Assert.Equal(14, body.Length);
        Assert.Equal(new RoundPortalMoveRequested(0x1234, 1, -2.5f, 100),
            PortalMoveBinding.Read(TestBuilds.Packets, build, body));
        var reply = PortalMoveBinding.Write(TestBuilds.Packets, build, new(0x1234, 5));
        Assert.Equal(10, reply.Length);
        Assert.Equal("00000000341205000000", Convert.ToHexString(reply));
        foreach (var invalid in new[] { body[..^1], body.Concat(new byte[1]).ToArray() })
            Assert.Throws<PacketFormatException>(() => PortalMoveBinding.Read(TestBuilds.Packets, build, invalid));
    }

    /// <summary>Only the corrected game request invokes the typed flow; all services and routes unload.</summary>
    [Theory]
    [InlineData("kr-812", 948, 958, false)]
    [InlineData("kr-812", 948, 958, true)]
    [InlineData("cn-406", 948, 958, false)]
    [InlineData("cn-406", 948, 958, true)]
    [InlineData("cn-404", 948, 958, false)]
    [InlineData("cn-404", 948, 958, true)]
    [InlineData("cn-408", 948, 958, false)]
    [InlineData("cn-408", 948, 958, true)]
    [InlineData("cn-705", 948, 958, false)]
    [InlineData("cn-705", 948, 958, true)]
    [InlineData("cn-706", 948, 958, false)]
    [InlineData("cn-706", 948, 958, true)]
    [InlineData("cn-717", 948, 958, false)]
    [InlineData("cn-717", 948, 958, true)]
    [InlineData("id-400", 948, 958, false)]
    [InlineData("id-400", 948, 958, true)]
    [InlineData("id-1809", 948, 958, false)]
    [InlineData("id-1809", 948, 958, true)]
    [InlineData("kr-1256", 939, 953, false)]
    [InlineData("kr-1256", 939, 953, true)]
    [InlineData("cn-774", 939, 952, false)]
    [InlineData("cn-774", 939, 952, true)]
    [InlineData("tw-454", 939, 952, false)]
    [InlineData("tw-454", 939, 952, true)]
    public async Task NativeAndRemappedRoutesDispatchReplyAndUnload(string build, int requestCode, int replyCode, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        Assert.Equal(requestCode, full.C2S("gcpPortalMove"));
        Assert.Equal(replyCode, full.S2C("gspPortalMove"));
        if (full.TryGet(PacketDirection.ClientToServer, 26, out var lobbyFrame))
            Assert.NotEqual("gcpPortalMove", lobbyFrame!.Name);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            new PortalMoveBinding(build + "/game", table, TestBuilds.Packets, build).Mount(context);
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundPortalMoveRequested>>());
            await fixture.Packets.DispatchAsync(build + "/game", session,
                new(table.C2S("gcpPortalMove"), Convert.FromHexString("0000803F000020C00000C8423412")), default);
            Assert.Equal(new RoundPortalMoveRequested(0x1234, 1, -2.5f, 100), Assert.Single(fixture.Messages.Received));
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundPortalMoved>>());
            await sender.SendAsync(session, new(0x1234, 5), default);
            Assert.Equal(table.S2C("gspPortalMove"), Assert.Single(session.Sent));
            Assert.Equal("00000000341205000000", Convert.ToHexString(Assert.Single(session.Bodies)));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fixture.Packets.DispatchAsync(build + "/game", session,
                    new(table.C2S("gcpPortalMove"), new byte[14]), default));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundPortalMoveRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundPortalMoved>>());
    }

    /// <summary>Unreviewed build consumers remain unmounted even when a related packet table exists.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void MapPortalActivationRequiresOwnConsumerEvidence(string build) =>
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "map_portals"));
}
