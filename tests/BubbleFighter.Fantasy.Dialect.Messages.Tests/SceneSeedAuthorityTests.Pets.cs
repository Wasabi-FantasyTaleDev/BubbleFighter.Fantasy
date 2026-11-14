using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Pets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class SceneSeedAuthorityTests
{
    /// <summary>Modern cancellation and defence send every victim's same tables without any critical attack table.</summary>
    [Fact]
    public async Task ModernVictimTablesAreIdenticalAcrossRecipientsWithoutCritical()
    {
        var authority = PetAuthority();
        var roster = PetRoster();
        var first = new SeedSession(1, "kr-1256");
        var second = new SeedSession(2, "kr-1256");
        await authority.SendAsync(first, PetOpening(roster, 0), default);
        await authority.SendAsync(second, PetOpening(roster, 1), default);
        Assert.Equal(new ushort[] { 5, 5, 6, 6 }, first.Sent.Select(frame => frame.Opcode));
        var a = first.Sent.ToArray();
        var b = second.Sent.ToArray();
        for (var index = 0; index < a.Length; index++) Assert.Equal(a[index].Payload, b[index].Payload);
        Assert.Equal(136, a[2].Payload.Length);
        Assert.Equal(32, BitConverter.ToInt32(a[2].Payload, 4));
        Assert.True(authority.Issued(1, NativeSceneSeedKind.CriticalCancellation, 1_100_000, out var generation));
        Assert.Equal(7, generation);
        Assert.False(authority.Issued(1, NativeSceneSeedKind.Critical, 1_000_000, out _));
        Assert.False(authority.Issued(1, NativeSceneSeedKind.CriticalCancellation, 1_200_000, out _));
    }

    /// <summary>Replays preserve all cursors, and a failed cancellation fan-out retains its original failure.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CancellationFanOutRetainsCompletedAndFailedIssuance(int failedAttempt)
    {
        var authority = PetAuthority();
        var failure = new IOException("Cancellation send failed.");
        var session = new SeedSession(1, "kr-1256")
        {
            BeforeSend = (attempt, _) => attempt == failedAttempt ? Task.FromException(failure) : Task.CompletedTask,
        };
        var request = PetOpening(PetRoster(), 0);
        if (failedAttempt == 0)
        {
            await authority.SendAsync(session, request, default);
            await authority.SendAsync(session, request with { }, default);
            Assert.Equal(4, session.Attempts);
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, request, default).AsTask()));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, request, default).AsTask()));
            Assert.Equal(failedAttempt, session.Attempts);
        }
    }

    /// <summary>A modifier-only change cannot overwrite an existing opening request, and stale generations remain rejected.</summary>
    [Fact]
    public async Task CancellationRejectsChangedAttributesAndStaleGeneration()
    {
        var authority = PetAuthority();
        var session = new SeedSession(1, "kr-1256");
        var request = PetOpening(PetRoster(), 0);
        await authority.SendAsync(session, request, default);
        var changed = PetCombatSeedTests.Equipment(
            PetCombatSeedTests.Attribute(190100, PetCombatEffectKind.AddCriticalCancellationRatio, 1));
        var roster = new MatchSeedRoster([new(1_000_000, changed), request.Roster!.Occupants[1]]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session,
            request with { Equipment = changed, Roster = roster }, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session,
            request with { Generation = 6 }, default).AsTask());
        Assert.Equal(4, session.Attempts);
    }

    /// <summary>An equipment publication carrying the same modifier values does not reset critical cursors.</summary>
    [Fact]
    public async Task EqualPetPublicationPreservesCriticalRevisionsAndVictimTables()
    {
        var catalog = WeaponCriticalCatalog.Load(WeaponCriticalCatalogTests.Fixture(), Tables, "cn-324", new(),
            PetCombatSeedTests.Pets("cn-324/game"));
        var chances = Chances(Workbook(("prevWin", "0"), ("prevDraw", "0"), ("prevLose", "0"), ("prevLoseInRow", "0"),
            ("win", "0"), ("draw", "0"), ("lose", "0"), ("highRank", "0"), ("midRank", "0"), ("lowRank", "0")));
        var authority = new SceneSeedAuthority("cn-324/relay", "cn-324", Tables, 4, 5, 6, chances, 4,
            new FixedRandom(0.025), weapons: catalog);
        var attribute = PetCombatSeedTests.Attribute(190100, PetCombatEffectKind.AssignCriticalChancePercent, 0.5f);
        var equipment = new MatchSeedEquipment(1, [203103, 201001, 190100], itemAttributes: [attribute]);
        var request = Opening with { Equipment = equipment };
        var session = new SeedSession(1);
        await authority.SendAsync(session, request, default);
        var current = new MatchCriticalSeedUpdate(41, 7, 1_000_000, 1, equipment);
        var equal = new MatchSeedEquipment(1, [190100, 201001, 203103], itemAttributes:
            [attribute with { Values = attribute.Values.ToArray() }]);
        Assert.Same(current, authority.WithPublishedEquipment(current, equal));
        await authority.SendAsync(session, current, default);
        Assert.Equal(3, session.Attempts);
        var stronger = new MatchSeedEquipment(1, [203103, 201001, 190100], itemAttributes:
            [PetCombatSeedTests.Attribute(190100, PetCombatEffectKind.AssignCriticalChancePercent, 1)]);
        var next = authority.WithPublishedEquipment(current, stronger) with { Revision = 2 };
        await authority.SendAsync(session, next, default);
        await authority.SendAsync(session, next with { }, default);
        Assert.Equal(new ushort[] { 4, 5, 6, 4 }, session.Sent.Select(frame => frame.Opcode));
        Assert.Equal(2000, SetBits(session.Sent.Last().Payload));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, current, default).AsTask());
        Assert.Equal(4, session.Attempts);
    }

    /// <summary>Modern cancellation can mount with its own option without loading critical or weapon tables.</summary>
    [Fact]
    public async Task CancellationOnlyActivationDoesNotEnableModernCritical()
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Relay));
        var data = new WeaponCriticalCatalogTests.FixtureStore(new() { ["Config/ShopItem2.xml"] = DefenceSeedTests.ItemWorkbook() });
        using var resource = fixture.Services.Register<IClientDataStore>(data);
        var context = fixture.Context("kr-1256", "kr-1256/relay", "dialect.scene-seeds",
            extra: new() { ["critical_cancellation_seeds"] = true });
        using var events = fixture.Services.Register<IEventBus>(context.Events);
        context.BindServices(typeof(SceneSeedsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new SceneSeedsPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Services.GetAll<IMatchCriticalSeedAuthority>());
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
            var session = new SeedSession(1, "kr-1256");
            await sender.SendAsync(session, Opening with { Equipment = new(1, [100072]) }, default);
            Assert.Equal(new ushort[] { 5, 6 }, session.Sent.Select(frame => frame.Opcode));
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
    }

    /// <summary>A seeded build can apply pet critical effects without opting into unsupported weapon-master logic.</summary>
    [Fact]
    public async Task PetCriticalFallbackMountsWithoutWeaponCatalogs()
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-812").ForConnection(OpcodeConnection.Relay));
        var data = new WeaponCriticalCatalogTests.FixtureStore(new()
        {
            ["Config/ShopItem2.xml"] = DefenceSeedTests.ItemWorkbook(),
            ["Config/CriticalHitTable.xml"] = Workbook(("prevWin", "0"), ("prevDraw", "0"), ("prevLose", "0"), ("prevLoseInRow", "0"),
                ("win", "0"), ("draw", "0"), ("lose", "0"), ("highRank", "0"), ("midRank", "0"), ("lowRank", "0")),
        });
        using var resource = fixture.Services.Register<IClientDataStore>(data);
        using var resolver = fixture.Services.RegisterMany<IPetCombatEffects>(new PetCombatSeedTests.FixtureEffects("kr-812/game"));
        var context = fixture.Context("kr-812", "kr-812/relay", "dialect.scene-seeds", extra: new()
        {
            ["pet_combat_effects"] = true, ["pet_combat_mode_ids"] = new[] { 1 },
        });
        using var events = fixture.Services.Register<IEventBus>(context.Events);
        context.BindServices(typeof(SceneSeedsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new SceneSeedsPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Services.GetAll<IMatchCriticalSeedAuthority>());
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
            var session = new SeedSession(1, "kr-812");
            await sender.SendAsync(session, Opening with { Equipment = PetCombatSeedTests.Equipment(
                PetCombatSeedTests.Attribute(190100, PetCombatEffectKind.AssignCriticalChancePercent, 100)) }, default);
            Assert.Equal(2000, SetBits(session.Sent.First().Payload));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Descendants do not inherit modern cancellation authority without their own consumer audit.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public async Task UnauditedCancellationCapabilityFailsBeforePublication(string build)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build).ForConnection(OpcodeConnection.Relay));
        using var resource = fixture.Services.Register<IClientDataStore>(new WeaponCriticalCatalogTests.FixtureStore(new()));
        var context = fixture.Context(build, build + "/relay", "dialect.scene-seeds",
            extra: new() { ["critical_cancellation_seeds"] = true });
        using var events = fixture.Services.Register<IEventBus>(context.Events);
        try
        {
            await Assert.ThrowsAsync<ConfigException>(() => new SceneSeedsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Enabling instance effects requires exactly one resolver scoped to the owning game protocol.</summary>
    [Theory]
    [InlineData(0, "kr-1256/game")]
    [InlineData(2, "kr-1256/game")]
    [InlineData(1, "cn-717/game")]
    public async Task ResolverAbsenceAmbiguityAndForeignProtocolFailBeforePublication(int count, string protocol)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Relay));
        using var resource = fixture.Services.Register<IClientDataStore>(new WeaponCriticalCatalogTests.FixtureStore(new()));
        var context = fixture.Context("kr-1256", "kr-1256/relay", "dialect.scene-seeds", extra: new()
        {
            ["defence_seeds"] = true, ["pet_combat_effects"] = true, ["pet_combat_mode_ids"] = new[] { 1 },
        });
        using var events = fixture.Services.Register<IEventBus>(context.Events);
        for (var index = 0; index < count; index++)
            context.Effect(fixture.Services.RegisterMany<IPetCombatEffects>(new PetCombatSeedTests.FixtureEffects(protocol)));
        context.BindServices(typeof(SceneSeedsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await Assert.ThrowsAsync<ConfigException>(() => new SceneSeedsPlugin().ActivateAsync(context, default).AsTask());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSeedTablesRequested>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static SceneSeedAuthority PetAuthority()
    {
        var store = new WeaponCriticalCatalogTests.FixtureStore(new() { ["Config/ShopItem2.xml"] = DefenceSeedTests.ItemWorkbook() });
        var effects = PetCombatSeedTests.Pets();
        var defence = DefenceSeedCatalog.Load(store, Tables, "kr-1256", "Config/ShopItem2.xml", 1 << 20,
            "defence_workbook_max_bytes", effects);
        return new("kr-1256/relay", "kr-1256", Tables, null, 5, 6, null, 4, new Random(1), defences: defence,
            combat: new CombatSeedCatalog(new Dictionary<int, PetCombatEffect[]>(), effects), cancellationSeeds: true);
    }

    private static MatchSeedRoster PetRoster() => new([
        new(1_000_000, PetCombatSeedTests.Equipment(
            PetCombatSeedTests.Attribute(190100, PetCombatEffectKind.AddCriticalCancellationRatio, 0.5f),
            PetCombatSeedTests.Attribute(190100, PetCombatEffectKind.AddDefenceMinimumRatio, 0.03f),
            PetCombatSeedTests.Attribute(190100, PetCombatEffectKind.AddDefenceMaximumRatio, 0.07f))),
        new(1_100_000, null),
    ]);

    private static MatchSeedTablesRequested PetOpening(MatchSeedRoster roster, int index) => Opening with
    {
        EntityId = roster.Occupants[index].EntityId, Equipment = roster.Occupants[index].Equipment, Roster = roster,
    };
}
