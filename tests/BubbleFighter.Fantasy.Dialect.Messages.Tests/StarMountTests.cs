using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises the kr-1256 star routes, empty Fever packet and optimized star-count report.</summary>
public sealed class StarMountTests
{
    private const string Floats = "0000803F000000C0000040400000003F00000040000080BF";

    /// <summary>Dispatches the reviewed star opcodes and round-trips the three outbound bodies.</summary>
    [Fact]
    public async Task Kr1256StarRoutesAndRepliesUseTheReviewedTable()
    {
        const string build = "kr-1256";
        const string protocol = "kr-1256/game";
        var profile = new ClientProfile(build, "kr", 1256, 0, 1304, "test");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        var binding = new StarBinding(protocol, table, TestBuilds.All.Tables, build, bindScoreReports: true);
        try
        {
            binding.Mount(context, fixture.Messages);
            Assert.Equal((ushort)632, table.C2S("gcpReqCreateStar"));
            Assert.Equal((ushort)631, table.C2S("gcpEatStar"));
            Assert.Equal((ushort)641, table.S2C("gspGameCreateStar"));
            Assert.Equal((ushort)642, table.S2C("gspStarEaten"));
            Assert.Equal((ushort)634, table.S2C("gspGameFeverStart_"));
            Assert.Equal((ushort)2029, table.C2S("gcpGameUpdateScoreOptimze"));

            async Task Dispatch(string name, byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new InboundPacket(table.C2S(name), body), default);

            var point = new RoundLucciPoint(1, -2, 3, 0.5f, 2, -1);
            await Dispatch("gcpReqCreateStar", Convert.FromHexString(Floats + "01000000" + "05000000" + "07000000"));
            Assert.Equal(new RoundStarDropRequested(point, 1, 7), fixture.Messages.Received[^1]);

            await Dispatch("gcpEatStar", Convert.FromHexString("40420F00" + "01000063"));
            Assert.Equal(new RoundStarPickupRequested(1000000, 0x63000001), fixture.Messages.Received[^1]);

            // kr-1256 optimized star report: u8 kind, u32 character id, u8 reporter slot.
            await Dispatch("gcpGameUpdateScoreOptimze", Convert.FromHexString("15" + "40420F00" + "00"));
            Assert.Equal(new RoundStarReported(0, RoundStarChange.SmallGained), fixture.Messages.Received[^1]);
            await Dispatch("gcpGameUpdateScoreOptimze", Convert.FromHexString("18" + "40420F00" + "00"));
            Assert.Equal(new RoundStarReported(0, RoundStarChange.BigGained), fixture.Messages.Received[^1]);
            var received = fixture.Messages.Received.Count;
            await Dispatch("gcpGameUpdateScoreOptimze", Convert.FromHexString("06" + "40420F00" + "00"));
            Assert.Equal(received, fixture.Messages.Received.Count);

            var created = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundStarCreated>>());
            await created.SendAsync(session, new(0x63000001, 2, point, 7), default);
            Assert.Equal(table.S2C("gspGameCreateStar"), session.Sent[^1]);
            Assert.Equal("01000063" + "02000000" + Floats + "07000000", Convert.ToHexString(session.Bodies[^1]));
            Assert.Equal(36, session.Bodies[^1].Length);

            var picked = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundStarPicked>>());
            await picked.SendAsync(session, new(1000000, 0x63000001), default);
            Assert.Equal(table.S2C("gspStarEaten"), session.Sent[^1]);
            Assert.Equal("40420F0001000063", Convert.ToHexString(session.Bodies[^1]));

            var fever = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundStarFeverStarted>>());
            await fever.SendAsync(session, new(), default);
            Assert.Equal(table.S2C("gspGameFeverStart_"), session.Sent[^1]);
            Assert.Empty(session.Bodies[^1]);
        }
        finally { await context.UnwindAsync(); }

        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundStarDropRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundStarPickupRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundStarReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundStarCreated>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundStarPicked>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundStarFeverStarted>>());
    }
}
