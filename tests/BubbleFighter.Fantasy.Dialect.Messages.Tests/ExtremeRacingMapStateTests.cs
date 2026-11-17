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

/// <summary>Pins the audited KR map-state sender, full vector, capability boundary and disposable publication.</summary>
public sealed class ExtremeRacingMapStateTests
{
    /// <summary>The native frame carries two bomb patterns, a stone RNG seed and an independently counted route vector.</summary>
    [Fact]
    public void CompleteMapStatePinsEveryWordAndByteLength()
    {
        var body = ExtremeRacingBinding.WriteMapState(TestBuilds.Packets, "kr-1256", new(7, 9, 0x11223344, new[] { 3, 0, 1 }));
        Assert.Equal(28, body.Length);
        Assert.Equal("07000000090000004433221103000000030000000000000001000000", Convert.ToHexString(body));
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-1256", "ExtremeRacingMapState", malformed));
        Assert.Equal(16, ExtremeRacingBinding.WriteMapState(TestBuilds.Packets, "kr-1256", new(0, 0, 0, Array.Empty<int>())).Length);
    }

    /// <summary>Invalid pattern indices, negative server seeds and oversized vectors fail before allocation or writing.</summary>
    [Fact]
    public void InvalidSelectionsFailBeforeWriting()
    {
        foreach (var message in new RoundExtremeRacingMapState[]
        {
            new(-1, 0, 0, []), new(10, 0, 0, []), new(0, 10, 0, []), new(0, 0, -1, []),
            new(0, 0, 0, [-1]), new(0, 0, 0, new int[16380]),
        })
            Assert.Throws<InvalidDataException>(() => ExtremeRacingBinding.WriteMapState(TestBuilds.Packets, "kr-1256", message));
    }

    /// <summary>A related layout cannot enable the new typed semantics without its own audited capability and field meanings.</summary>
    [Theory]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void OtherBuildsDoNotInheritTheNewMapStateSender(string build)
    {
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "extreme_racing_map_state"));
        Assert.Throws<ConfigException>(() => ExtremeRacingBinding.WriteMapState(TestBuilds.Packets, build, new(1, 2, 3, [])));
    }

    /// <summary>The optional sender resolves its opcode by name and disappears when its declared row unwinds.</summary>
    [Fact]
    public async Task MapStateMountResolvesItsNameAndUnloads()
    {
        const string name = "gspExtremeRacingMapState_";
        var native = TestBuilds.Opcodes("kr-1256");
        var table = new OpcodeTable(native.Profile, native.All.Where(entry => entry.Name != name)
            .Append(new OpcodeEntry(1716, PacketDirection.ServerToClient, name, Evidence.Verified, null, null)));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var binding = new ExtremeRacingBinding("kr-1256/game", table, TestBuilds.Packets, "kr-1256", NullLogger.Instance);
            binding.Mount(context);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingMapState>>());
            binding.MountMapState(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingMapState>>());
            var session = new MatchMessagesMountTests.Session(native.Profile);
            await sender.SendAsync(session, new(1, 2, 3, []), default);
            Assert.Equal((ushort)1716, Assert.Single(session.Sent));
            Assert.Equal(16, Assert.Single(session.Bodies).Length);
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(session, new(1, 2, 3, []), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundExtremeRacingMapState>>());
    }

    /// <summary>The room option cannot publish map state without its owning racing exchange.</summary>
    [Fact]
    public async Task MapStateOptionRequiresRacingExchange()
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256"));
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms", extra: new()
            { ["modern_rooms"] = true, ["extreme_racing_map_state"] = true });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() => new RoomsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("extreme_racing_map_state requires extreme_racing", error.Message, StringComparison.Ordinal);
        }
        finally { await context.UnwindAsync(); }
    }
}
