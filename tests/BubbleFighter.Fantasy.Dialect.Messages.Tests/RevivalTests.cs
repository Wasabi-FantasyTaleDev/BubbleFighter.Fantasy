using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the consumable revival body and distinguishes player references from owned NPC references.</summary>
public sealed class RevivalTests
{
    /// <summary>CN2011 admits its ordinary two-word respawn without mounting the later consumable or PvE revival requests.</summary>
    [Fact]
    public async Task Cn2011KeepsOrdinaryRespawnSeparateFromConsumableRevival()
    {
        const string build = "cn-279";
        const string protocol = build + "/game";
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol);
        var (schema, play) = TestBuilds.Of(build);
        try
        {
            new PlayRoomCodec(protocol, build, schema, play, fixture.Services.Get<IOpcodeTableRegistry>(),
                NullLogger.Instance, TestBuilds.Packets).Mount(context);
            Assert.False(TestBuilds.Packets.CapabilityOf(build, "instant_revival"));
            Assert.DoesNotContain(table.All, entry => entry.Direction == PacketDirection.ClientToServer
                && entry.Name is "gcpGameReqUseRespawnItem" or "gcpPveUseRevival");
            Assert.Throws<ConfigException>(() =>
                new RevivalBinding(protocol, table, TestBuilds.Packets, build).Mount(context, fixture.Messages));
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundInstantRevivalRequested>>());
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundPveRevivalRequested>>());
            var profile = new ClientProfile(build, "cn", 279, 0, 0, build);
            var session = new MatchMessagesMountTests.Session(profile);
            var opcode = table.C2S("gcpRespawn");
            // cn-279 sub_818D80 sends the actor/index pair; cn-279 sub_47E650 reads exactly those two words.
            await fixture.Packets.DispatchAsync(protocol, session,
                new(opcode, Convert.FromHexString("804F120003000000")), default);
            var ordinary = Assert.IsType<RoundSpawnAccepted>(Assert.Single(fixture.Messages.Received));
            Assert.Equal(1_200_000, ordinary.Actor);
            Assert.Equal(3, ordinary.SpawnIndex);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }

    /// <summary>Only explicitly reviewed PvE rows mount, even when a diagnostic fallback offers a number.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-812", true)]
    [InlineData("cn-406", true)]
    public async Task MountedRevivalUsesExplicitRowsAndReleasesEveryRoute(string build, bool pve)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var loaded = TestBuilds.Opcodes(build);
        var fallbackCalls = 0;
        var table = new OpcodeTable(profile, loaded.All, (_, _, name) =>
        {
            if (name == "gcpPveUseRevival") { fallbackCalls++; return 65000; }
            return null;
        });
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol);
        try
        {
            new RevivalBinding(protocol, table, TestBuilds.Packets, build).Mount(context, fixture.Messages);
            Assert.Equal(pve ? 2 : 1, fixture.Packets.Handlers.Count);
            Assert.Equal(0, fallbackCalls);
            Assert.Equal(pve ? 1 : 0, fixture.Services.GetAll<IInboundMessageBinding<RoundPveRevivalRequested>>().Count);
            var session = new MatchMessagesMountTests.Session(profile);
            var instant = table.C2S("gcpGameReqUseRespawnItem");
            await fixture.Packets.DispatchAsync(protocol, session, new(instant, BitConverter.GetBytes(1_200_000)), default);
            Assert.Equal(2, Assert.IsType<RoundInstantRevivalRequested>(Assert.Single(fixture.Messages.Received)).ActorSeat);
            await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync(protocol, session,
                new(instant, new byte[3]), default).AsTask());
            if (pve)
            {
                var opcode = table.C2S("gcpPveUseRevival");
                await fixture.Packets.DispatchAsync(protocol, session, new(opcode, ReadOnlyMemory<byte>.Empty), default);
                Assert.IsType<RoundPveRevivalRequested>(fixture.Messages.Received[^1]);
                await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync(protocol, session,
                    new(opcode, new byte[1]), default).AsTask());
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundInstantRevivalRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundPveRevivalRequested>>());
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    public void AuditedRevivalBuildsReadTheSameNativeActorReference(string build)
    {
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "instant_revival"));
        var body = BitConverter.GetBytes(1_200_000);
        var request = RevivalBinding.ReadInstant(TestBuilds.Packets, build, body);
        Assert.Equal(1_200_000, request.Actor);
        Assert.Equal(2, request.ActorSeat);
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.Packets, build, body[..3]));
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.Packets, build, new byte[8]));
    }

    /// <summary>C714 contains exactly one player reference and rejects truncated or extended bodies.</summary>
    [Fact]
    public void ConsumableRevivalIsFourBytesAndPreservesOwner()
    {
        var body = BitConverter.GetBytes(1_100_000);
        var request = RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", body);
        Assert.Equal(1_100_000, request.Actor);
        Assert.Equal(1, request.ActorSeat);
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", body[..3]));
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", new byte[8]));
        Assert.Equal(-1, RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", BitConverter.GetBytes(1_100_001)).ActorSeat);
    }

    /// <summary>The modern revival family keeps the one-word consumable body, shares the respawn
    /// request shapes, and frames kr-1256's respawn index in its own optional markers.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernRevivalKeepsTheActorWordAndTheRespawnShapes(string build)
    {
        var tables = TestBuilds.Packets;
        Assert.True(tables.CapabilityOf(build, "instant_revival"));
        var body = BitConverter.GetBytes(1_200_000);
        var request = RevivalBinding.ReadInstant(tables, build, body);
        Assert.Equal(1_200_000, request.Actor);
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(tables, build, body[..3]));
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(tables, build, new byte[8]));
        var index = PacketCodec.Write(tables, build, "RespawnIndexRequest",
            new Dictionary<string, object?> { ["actor"] = 7, ["spawn_index"] = 3 });
        Assert.Equal(8, index.Length);
        var extend = PacketCodec.Write(tables, build, "RespawnExtendRequest",
            new Dictionary<string, object?> { ["actor"] = 7, ["wait_delta"] = 90 });
        Assert.Equal(8, extend.Length);
    }

    /// <summary>cn-774's respawn request is the plain three-word body and kr-1256 frames the spawn
    /// index in its 0x3A-tagged optionals, absent as the bare 0xBA bytes.</summary>
    [Fact]
    public void ModernRespawnRequestBodiesMatchTheirFraming()
    {
        var tables = TestBuilds.Packets;
        var plain = PacketCodec.Write(tables, "cn-774", "RespawnRequest",
            new Dictionary<string, object?> { ["actor"] = 7, ["spawn_index"] = 3, ["item_reset"] = 1 });
        Assert.Equal(12, plain.Length);
        var absent = PacketCodec.Write(tables, "kr-1256", "RespawnRequest",
            new Dictionary<string, object?> { ["actor"] = 7, ["item_reset"] = 1 });
        Assert.Equal(10, absent.Length);
        Assert.Equal("07000000BABA01000000", Convert.ToHexString(absent));
        var offered = PacketCodec.Write(tables, "kr-1256", "RespawnRequest",
            new Dictionary<string, object?>
            {
                ["actor"] = 7,
                ["spawn_index"] = new Dictionary<string, object?> { ["spawn_index"] = 3 },
                ["item_reset"] = 1,
            });
        Assert.Equal(14, offered.Length);
        Assert.Equal("070000003A03000000BA01000000", Convert.ToHexString(offered));
    }
}
