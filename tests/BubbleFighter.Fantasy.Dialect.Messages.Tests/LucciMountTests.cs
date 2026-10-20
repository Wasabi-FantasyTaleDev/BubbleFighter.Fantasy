using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises the NPC-kill Lucci routes and replies with each build's reviewed game opcode table.</summary>
public sealed class LucciMountTests
{
    private const string Floats = "0000803F000000C0000040400000003F00000040000080BF";

    /// <summary>Every legacy build with the packets routes the requests, pins the byte layouts and unloads cleanly.</summary>
    [Theory]
    [InlineData("kr-812", true, false)]
    [InlineData("kr-812", true, true)]
    [InlineData("cn-717", true, false)]
    [InlineData("cn-706", true, false)]
    [InlineData("id-1809", false, false)]
    [InlineData("id-400", false, false)]
    [InlineData("cn-324", false, false)]
    [InlineData("cn-406", false, false)]
    [InlineData("cn-279", false, false)]
    public async Task LucciRoutesAndRepliesUseTheBuildTableAndUnload(string build, bool award, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        var binding = new LucciBinding(protocol, table, TestBuilds.All.Tables, build);
        try
        {
            binding.Mount(context, fixture.Messages);
            Assert.Equal(award, binding.HasHordeAward);
            Assert.Equal(award ? 3 : 2, fixture.Packets.Handlers.Count);
            async Task Dispatch(string name, byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new InboundPacket(table.C2S(name), body), default);
            var point = new RoundLucciPoint(1, -2, 3, 0.5f, 2, -1);
            await Dispatch("gcpReqCreateLucci", Convert.FromHexString(Floats + "02000000" + "05000000" + "07000000"));
            Assert.Equal(new RoundLucciDropRequested(point, 2, 7), fixture.Messages.Received[^1]);
            await Dispatch("gcpEatLucci", Convert.FromHexString("40420F00" + "01000063"));
            Assert.Equal(new RoundLucciPickupRequested(1000000, 0x63000001), fixture.Messages.Received[^1]);
            // A short or long body, or a non-finite coordinate, is a format error and never reaches a handler.
            var received = fixture.Messages.Received.Count;
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpReqCreateLucci", new byte[35]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpReqCreateLucci", new byte[37]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpEatLucci", new byte[7]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpReqCreateLucci", Convert.FromHexString("0000C07F" + Floats[8..] + "02000000" + "05000000" + "07000000")));
            Assert.Equal(received, fixture.Messages.Received.Count);
            if (award)
            {
                await Dispatch("gcpReqCreateHordeAward", Convert.FromHexString(Floats + "05000000" + "09000000"));
                Assert.Equal(new RoundHordeAwardRequested(point, 9), fixture.Messages.Received[^1]);
                await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch("gcpReqCreateHordeAward", new byte[36]));
            }
            else
                Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpReqCreateHordeAward"));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch("gcpEatLucci", Convert.FromHexString("40420F0001000063")));
            var created = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLucciCreated>>());
            await created.SendAsync(session, new(0x63000001, 2, point, 7), default);
            Assert.Equal(table.S2C("gspGameCreateLucci"), session.Sent[^1]);
            Assert.Equal("01000063" + "02000000" + Floats + "07000000", Convert.ToHexString(session.Bodies[^1]));
            Assert.Equal(36, session.Bodies[^1].Length);
            var picked = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLucciPicked>>());
            await picked.SendAsync(session, new(1000000, 0x63000001), default);
            Assert.Equal(table.S2C("gspLucciEaten"), session.Sent[^1]);
            Assert.Equal("40420F0001000063", Convert.ToHexString(session.Bodies[^1]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundLucciDropRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundHordeAwardRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundLucciPickupRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLucciCreated>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLucciPicked>>());
    }

    /// <summary>The opcode numbers each build's client uses, from the reviewed profile tables.</summary>
    [Theory]
    [InlineData("kr-812", 736, 735, 720, 721, 2021)]
    [InlineData("cn-717", 736, 735, 720, 721, 2021)]
    [InlineData("cn-706", 736, 735, 720, 721, 2020)]
    [InlineData("id-400", 736, 735, 720, 721, 0)]
    [InlineData("id-1809", 736, 735, 720, 721, 0)]
    [InlineData("cn-324", 736, 735, 720, 721, 0)]
    [InlineData("cn-406", 736, 735, 720, 721, 0)]
    [InlineData("cn-279", 733, 732, 717, 718, 0)]
    public void OpcodeNumbersMatchTheClientDispatchers(string build, int create, int eat, int created, int eaten, int award)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        Assert.Equal((ushort)create, table.C2S("gcpReqCreateLucci"));
        Assert.Equal((ushort)eat, table.C2S("gcpEatLucci"));
        Assert.Equal((ushort)created, table.S2C("gspGameCreateLucci"));
        Assert.Equal((ushort)eaten, table.S2C("gspLucciEaten"));
        Assert.Equal(award, table.Find(PacketDirection.ClientToServer, "gcpReqCreateHordeAward") ?? 0);
    }

    /// <summary>The body sizes are the client readers' nine and eight words and two words, in every build.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-706")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-279")]
    public void StructuresHaveTheNativeBodySizes(string build)
    {
        var tables = TestBuilds.All.Tables;
        Assert.Equal(36, PacketCodec.Write(tables, build, "LucciDropRequest", new Dictionary<string, object?>()).Length);
        Assert.Equal(32, PacketCodec.Write(tables, build, "HordeAwardRequest", new Dictionary<string, object?>()).Length);
        Assert.Equal(36, PacketCodec.Write(tables, build, "LucciCreate", new Dictionary<string, object?>()).Length);
        Assert.Equal(8, PacketCodec.Write(tables, build, "LucciPickup", new Dictionary<string, object?>()).Length);
        Assert.Equal(8, PacketCodec.Write(tables, build, "LucciPicked", new Dictionary<string, object?>()).Length);
    }

    /// <summary>A build without the audited capability, or a request that its creation handler cannot build, never installs routes.</summary>
    [Fact]
    public async Task UnauditedSurfaceCannotInstallRoutes()
    {
        const string modern = "cn-735";
        var profile = new ClientProfile(modern, "cn", 735, 0, 0, modern);
        var empty = new OpcodeTable(profile, []);
        using (var fixture = new MatchMessagesMountTests.Fixture(empty))
        {
            var context = fixture.Context(modern, modern + "/game");
            Assert.Throws<ConfigException>(() => new LucciBinding(modern + "/game", empty, TestBuilds.All.Tables, modern).Mount(context, fixture.Messages));
            Assert.Empty(fixture.Packets.Handlers);
            await context.UnwindAsync();
        }
        // id-400 reads only kinds 1 and 2 of S2C 720; an award request row there must be refused, not mounted.
        const string indonesian = "id-400";
        var idProfile = new ClientProfile(indonesian, "id", 400, 0, 0, indonesian);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var real = OpcodeTableLoader.Load(Path.Combine(root, "profiles", indonesian, "opcodes.yaml"), idProfile).ForConnection(OpcodeConnection.Game);
        var withAward = new OpcodeTable(idProfile, real.All.Append(new OpcodeEntry(2021, PacketDirection.ClientToServer,
            "gcpReqCreateHordeAward", Evidence.Inferred, "test", null)));
        using var second = new MatchMessagesMountTests.Fixture(withAward);
        var other = second.Context(indonesian, indonesian + "/game");
        Assert.Throws<ConfigException>(() => new LucciBinding(indonesian + "/game", withAward, TestBuilds.All.Tables, indonesian).Mount(other, second.Messages));
        await other.UnwindAsync();
    }
}
