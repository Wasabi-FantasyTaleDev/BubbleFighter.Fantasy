using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class SceneSeedAuthorityTests
{
    /// <summary>
    /// The defence-only authority needs no critical files or report routes, and each round sends the occupant's
    /// 1000-byte defence table filled with the build's own roll.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 2)]
    [InlineData("cn-705", 1)]
    [InlineData("cn-706", 1)]
    [InlineData("cn-774", 2)]
    [InlineData("tw-454", 2)]
    public async Task DefenceOnlyRowMountsWithoutCriticalResources(string build, int firstPercent)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build).ForConnection(OpcodeConnection.Relay));
        var data = new WeaponCriticalCatalogTests.FixtureStore(new() { ["Config/ShopItem2.xml"] = DefenceSeedTests.ItemWorkbook() });
        using var resource = fixture.Services.Register<IClientDataStore>(data);
        var context = fixture.Context(build, build + "/relay", "dialect.scene-seeds",
            extra: new() { ["defence_seeds"] = true });
        using var eventService = fixture.Services.Register<IEventBus>(context.Events);
        context.BindServices(typeof(SceneSeedsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new SceneSeedsPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMatchCriticalSeedAuthority>());
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
            var session = new SeedSession(1, build);
            await sender.SendAsync(session, Opening with { Equipment = DefenceSeedTests.Worn(100072) }, default);
            var frame = Assert.Single(session.Sent);
            Assert.Equal((ushort)5, frame.Opcode);
            Assert.Equal(1008, frame.Payload.Length);
            Assert.Equal(1_000_000, BitConverter.ToInt32(frame.Payload, 0));
            Assert.Equal(Enumerable.Range(firstPercent, 6), Values(frame.Payload).Distinct().Order());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMatchSeedInputValidator>());
        Assert.Empty(fixture.Services.GetAll<INativeSceneSeedStore>());
    }

    /// <summary>
    /// A defence-only build without defence_seeds has nothing to issue, and CN2011's local floating-point defence
    /// refuses the table option; both fail before any service is published.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-705", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("cn-279", true)]
    public async Task DefenceActivationNeedsTheOptionAndTheCapability(string build, bool defenceSeeds)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build).ForConnection(OpcodeConnection.Relay));
        var data = new WeaponCriticalCatalogTests.FixtureStore(new() { ["Config/ShopItem2.xml"] = DefenceSeedTests.ItemWorkbook() });
        using var resource = fixture.Services.Register<IClientDataStore>(data);
        var context = fixture.Context(build, build + "/relay", "dialect.scene-seeds",
            extra: new() { ["defence_seeds"] = defenceSeeds });
        using var eventService = fixture.Services.Register<IEventBus>(context.Events);
        context.BindServices(typeof(SceneSeedsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await Assert.ThrowsAsync<ConfigException>(() => new SceneSeedsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
            Assert.Empty(fixture.Services.GetAll<IMatchSeedInputValidator>());
        }
        finally { await context.UnwindAsync(); }
    }
}
