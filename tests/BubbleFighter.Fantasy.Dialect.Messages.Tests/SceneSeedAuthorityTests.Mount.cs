using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class SceneSeedAuthorityTests
{
    /// <summary>The optional catalog and its validator mount and unload with the owning row; disabled rows need no weapon files.</summary>
    [Theory]
    [InlineData("cn-324", false)]
    [InlineData("cn-324", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-406", true)]
    [InlineData("cn-404", false)]
    [InlineData("cn-404", true)]
    [InlineData("cn-408", false)]
    [InlineData("cn-408", true)]
    [InlineData("id-400", false)]
    [InlineData("id-400", true)]
    public async Task WeaponOptionMountsAndUnwindsTheOwningServices(string build, bool enabled)
    {
        var table = TestBuilds.Opcodes(build).ForConnection(OpcodeConnection.Relay);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var data = WeaponCriticalCatalogTests.Fixture();
        if (!enabled) data.Files.Clear();
        data.Files["Config/CriticalHitTable.xml"] = Workbook(
            ("prevWin", "0.7"), ("prevDraw", "0.8"), ("prevLose", "2"), ("prevLoseInRow", "4"),
            ("win", "0"), ("draw", "0.2"), ("lose", "1"), ("highRank", "0"), ("midRank", "0.1"), ("lowRank", "0.4"));
        using var resource = fixture.Services.Register<IClientDataStore>(data);
        var context = fixture.Context(build, build + "/relay", "dialect.scene-seeds",
            extra: new() { ["weapon_critical_bonuses"] = enabled });
        using var eventService = fixture.Services.Register<IEventBus>(context.Events);
        context.BindServices(typeof(SceneSeedsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new SceneSeedsPlugin().ActivateAsync(context, default);
            var validator = Assert.Single(fixture.Services.GetAll<IMatchSeedInputValidator>());
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
            Assert.Same(sender, validator);
            var critical = fixture.Services.GetAll<IMatchCriticalSeedAuthority>();
            if (enabled) Assert.Same(sender, Assert.Single(critical));
            else Assert.Empty(critical);
            Assert.Equal(enabled, validator.RequiresFixedEquipment(101));
            Assert.Equal(enabled, validator.RequiresFixedEquipment(16));
            Assert.Equal(enabled, validator.RequiresFixedEquipment(3));
            Assert.Equal(enabled, validator.RequiresFixedEquipment(1));
            Assert.Equal(build + "/relay", validator.Protocol);
            var session = new SeedSession(1, build);
            await sender.SendAsync(session, enabled ? Opening with { Equipment = new(101, new[] { 203103, 201001 }) } : Opening, default);
            Assert.Equal(3, session.Sent.Count);
            Assert.NotEmpty(fixture.Packets.Handlers);
            Assert.Single(fixture.Services.GetAll<INativeSceneSeedStore>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMatchSeedInputValidator>());
        Assert.Empty(fixture.Services.GetAll<IMatchCriticalSeedAuthority>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
        Assert.Empty(fixture.Services.GetAll<INativeSceneSeedStore>());
    }

    /// <summary>An enabled row with incomplete weapon data publishes neither routes nor seed services.</summary>
    [Fact]
    public async Task MissingWeaponResourceFailsBeforeMountEffects()
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("cn-324").ForConnection(OpcodeConnection.Relay));
        var data = WeaponCriticalCatalogTests.Fixture();
        data.Files.Remove("Config/WeaponSpecDetail.xml");
        data.Files["Config/CriticalHitTable.xml"] = Workbook(
            ("prevWin", "0.7"), ("prevDraw", "0.8"), ("prevLose", "2"), ("prevLoseInRow", "4"),
            ("win", "0"), ("draw", "0.2"), ("lose", "1"), ("highRank", "0"), ("midRank", "0.1"), ("lowRank", "0.4"));
        using var resource = fixture.Services.Register<IClientDataStore>(data);
        var context = fixture.Context("cn-324", "cn-324/relay", "dialect.scene-seeds",
            extra: new() { ["weapon_critical_bonuses"] = true });
        using var eventService = fixture.Services.Register<IEventBus>(context.Events);
        context.BindServices(typeof(SceneSeedsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await Assert.ThrowsAsync<ConfigException>(() => new SceneSeedsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMatchSeedInputValidator>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
            Assert.Empty(fixture.Services.GetAll<INativeSceneSeedStore>());
        }
        finally { await context.UnwindAsync(); }
    }
}
