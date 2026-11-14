using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks pet message routing, native identity preservation and build-scoped registration lifetime.</summary>
public sealed class PetBindingTests
{
    private const string LevelRequest = "gcpReqFollowerLevelup_";
    private const string AbilityRequest = "gcpReqFollowerAbilityChange_";

    /// <summary>Every pet marker and sender can be registered through the plugin's declared services.</summary>
    [Fact]
    public void PetServicesAreDeclared()
    {
        var declared = typeof(MessagesPlugin).GetCustomAttribute<PluginAttribute>()!.ConditionalProvidesMany;
        Assert.Contains(typeof(IInboundMessageBinding<PetLevelUpRequested>), declared);
        Assert.Contains(typeof(IInboundMessageBinding<PetAbilityChangeRequested>), declared);
        Assert.Contains(typeof(IMessageSender<PetLevelUpReplied>), declared);
        Assert.Contains(typeof(IMessageSender<PetAbilityChangeReplied>), declared);
        Assert.Contains(typeof(IMessageSender<PetEvolved>), declared);
    }

    /// <summary>Build capabilities admit only the native request and reply families present in that client.</summary>
    [Theory]
    [InlineData("kr-812", false, false)]
    [InlineData("cn-717", false, false)]
    [InlineData("cn-404", false, false)]
    [InlineData("cn-406", false, false)]
    [InlineData("cn-408", false, false)]
    [InlineData("cn-706", false, false)]
    [InlineData("id-400", false, false)]
    [InlineData("id-1809", false, false)]
    [InlineData("kr-1256", true, true)]
    [InlineData("cn-774", true, true)]
    [InlineData("tw-454", true, false)]
    public async Task CapabilitiesControlServicesAndUnload(string build, bool levelUp, bool abilityChange)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new()
        { ["login"] = false, ["pets"] = true });
        context.BindServices(typeof(MessagesPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            Assert.Equal(protocol, Assert.Single(fixture.Services.GetAll<IMessageSender<PetEvolved>>()).Protocol);
            Assert.Equal(levelUp ? 1 : 0, fixture.Services.GetAll<IInboundMessageBinding<PetLevelUpRequested>>().Count);
            Assert.Equal(levelUp ? 1 : 0, fixture.Services.GetAll<IMessageSender<PetLevelUpReplied>>().Count);
            Assert.Equal(abilityChange ? 1 : 0, fixture.Services.GetAll<IInboundMessageBinding<PetAbilityChangeRequested>>().Count);
            Assert.Equal(abilityChange ? 1 : 0, fixture.Services.GetAll<IMessageSender<PetAbilityChangeReplied>>().Count);
            if (levelUp)
                Assert.True(fixture.Packets.Handlers.ContainsKey((protocol, table.C2S(LevelRequest))));
            if (abilityChange)
                Assert.True(fixture.Packets.Handlers.ContainsKey((protocol, table.C2S(AbilityRequest))));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<PetLevelUpRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<PetAbilityChangeRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<PetLevelUpReplied>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<PetAbilityChangeReplied>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<PetEvolved>>());
    }

    /// <summary>Requests and replies resolve symbolic names even when every opcode number changes.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    [InlineData("tw-454", true)]
    public async Task NamedRoutesPreserveTargetsAndRejectIncompleteBodies(string build, bool remap)
    {
        var full = TestBuilds.Opcodes(build);
        var table = remap ? new OpcodeTable(full.Profile, full.All.Select(row => row with
        { Value = checked((ushort)(row.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new PetBinding(protocol, table, TestBuilds.Packets, build);
        try
        {
            binding.Bind(context, fixture.Messages);
            var level = Convert.FromHexString("FFFFFFFFFFFFFFFF");
            await Request(LevelRequest, level);
            Assert.Equal(new PetLevelUpRequested(ulong.MaxValue), Assert.Single(fixture.Messages.Received));
            await RejectMalformed(LevelRequest, level);
            await binding.SendAsync(session, new PetLevelUpReplied(ulong.MaxValue, 19), default);
            Assert.Equal(table.S2C("gspRepFollowerLevelup"), session.Sent[^1]);
            Assert.Equal("FFFFFFFFFFFFFFFF13000000", Convert.ToHexString(session.Bodies[^1]));
            if (TestBuilds.Packets.CapabilityOf(build, "pet_ability_change"))
            {
                var ability = Convert.FromHexString("FFFFFFFFFFFFFFFF010000000200000003000000");
                await Request(AbilityRequest, ability);
                Assert.Equal(new PetAbilityChangeRequested(ulong.MaxValue, 1, 2, 3), fixture.Messages.Received[^1]);
                await RejectMalformed(AbilityRequest, ability);
                await binding.SendAsync(session, new PetAbilityChangeReplied(ulong.MaxValue, 0, 3)
                { Unknown12 = 2, Unknown20 = 4 }, default);
                Assert.Equal(table.S2C("gspRepFollowerAbilityChange"), session.Sent[^1]);
                Assert.Equal("FFFFFFFFFFFFFFFF00000000020000000300000004000000", Convert.ToHexString(session.Bodies[^1]));
            }
            await binding.SendAsync(session, new PetEvolved(1, ulong.MaxValue), default);
            Assert.Equal(table.S2C("gspEvolveResult"), session.Sent[^1]);
            Assert.Equal(build == "tw-454" ? "01000000FFFFFFFFFFFFFFFF00000000" : "01000000FFFFFFFFFFFFFFFFBA00000000",
                Convert.ToHexString(session.Bodies[^1]));
            await binding.SendAsync(session, new PetEvolved(0, 1)
            { UnknownOptional = 5, Rewards = [6, 7], UnknownLast = 9 }, default);
            Assert.Equal(build == "tw-454" ? "00000000010000000000000009000000"
                : "0000000001000000000000003A05000000020000000600000007000000",
                Convert.ToHexString(session.Bodies[^1]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(LevelRequest, level));
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(async () => await binding.SendAsync(session, new PetLevelUpReplied(1, 0), default));
        }
        finally { await context.UnwindAsync(); }

        Task Request(string name, byte[] body) => fixture.Packets.DispatchAsync(protocol, session,
            new(table.C2S(name), body), default).AsTask();
        async Task RejectMalformed(string name, byte[] body)
        {
            var received = fixture.Messages.Received.Count;
            for (var length = 0; length < body.Length; length++)
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(name, body[..length]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(name, [.. body, 0]));
            Assert.Equal(received, fixture.Messages.Received.Count);
        }
    }

    /// <summary>The native operation numbers are independently pinned for each modern dispatcher.</summary>
    [Theory]
    [InlineData("kr-1256", 144, 166, 145, 167, 950)]
    [InlineData("cn-774", 143, 166, 144, 167, 949)]
    [InlineData("tw-454", 143, 164, null, null, 949)]
    public void NativeOpcodeNumbersRemainIndependent(string build, int levelRequest, int levelReply,
        int? abilityRequest, int? abilityReply, int evolution)
    {
        var table = TestBuilds.Opcodes(build);
        Assert.Equal(levelRequest, table.C2S(LevelRequest));
        Assert.Equal(levelReply, table.S2C("gspRepFollowerLevelup"));
        Assert.Equal(abilityRequest, table.Find(PacketDirection.ClientToServer, AbilityRequest) is { } request ? (int)request : null);
        Assert.Equal(abilityReply, table.Find(PacketDirection.ServerToClient, "gspRepFollowerAbilityChange") is { } reply ? (int)reply : null);
        Assert.Equal(evolution, table.S2C("gspEvolveResult"));
    }

    /// <summary>Leaving pet adaptation disabled registers no pet routes or senders.</summary>
    [Fact]
    public async Task DefaultOptionKeepsPetsUnbound()
    {
        var table = TestBuilds.Opcodes("kr-1256");
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.messages", extra: new() { ["login"] = false });
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            Assert.False(fixture.Packets.Handlers.ContainsKey(("kr-1256/game", table.C2S(LevelRequest))));
            Assert.False(fixture.Packets.Handlers.ContainsKey(("kr-1256/game", table.C2S(AbilityRequest))));
            Assert.Empty(fixture.Services.GetAll<IMessageSender<PetEvolved>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Unaudited generations refuse activation before publishing pet services.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-705")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public async Task MissingCapabilityFailsBeforeRegistration(string build)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        try
        {
            var binding = new PetBinding(build + "/game", table, TestBuilds.Packets, build);
            Assert.Throws<ConfigException>(() => binding.Bind(context, fixture.Messages));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<PetEvolved>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A missing request or response prevents partial registration of an otherwise enabled family.</summary>
    [Theory]
    [InlineData("gspEvolveResult", false)]
    [InlineData(LevelRequest, false)]
    [InlineData("gspRepFollowerLevelup", false)]
    [InlineData(AbilityRequest, false)]
    [InlineData("gspRepFollowerAbilityChange", false)]
    [InlineData("gspEvolveResult", true)]
    [InlineData(LevelRequest, true)]
    [InlineData("gspRepFollowerLevelup", true)]
    [InlineData(AbilityRequest, true)]
    [InlineData("gspRepFollowerAbilityChange", true)]
    public async Task MissingOrUnverifiedOpcodeFailsBeforeRegistration(string missing, bool inferred)
    {
        var full = TestBuilds.Opcodes("kr-1256");
        var table = new OpcodeTable(full.Profile, full.All.Where(row => inferred || row.Name != missing)
            .Select(row => inferred && row.Name == missing ? row with { Evidence = Evidence.Inferred } : row));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.messages");
        try
        {
            Assert.Throws<ConfigException>(() => new PetBinding("kr-1256/game", table, TestBuilds.Packets, "kr-1256")
                .Bind(context, fixture.Messages));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<PetEvolved>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A legacy item target must fit its own field without truncating a modern article identity.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void LegacyTargetWidthIsEnforced(string build)
    {
        var binding = new PetBinding(build + "/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build);
        Assert.Equal("FEFFFFFFFFFFFFFF03000000", Convert.ToHexString(binding.Write(new PetEvolved(-2, uint.MaxValue)
        { UnknownLast = 3 })));
        Assert.Throws<OverflowException>(() => binding.Write(new PetEvolved(1, (ulong)uint.MaxValue + 1)));
    }
}
