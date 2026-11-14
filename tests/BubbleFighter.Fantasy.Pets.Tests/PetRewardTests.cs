using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Pets.Data;
using BubbleFighter.Fantasy.Pets.Rewards;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Checks pet reward arithmetic, frozen inputs and the pre-settlement extension's lifecycle.</summary>
public sealed class PetRewardTests
{
    private const int PetId = 301234;
    private const int OtherItemId = 1234;
    private static readonly RoundRewardAmounts BaseReward = new(10, 10, 7);

    /// <summary>Resolves every reward through its buff name while retaining the native zero payload.</summary>
    [Theory]
    [InlineData(901, 10, 20)]
    [InlineData(902, 10, 15)]
    [InlineData(903, 10, 13)]
    [InlineData(904, 20, 10)]
    [InlineData(905, 15, 10)]
    [InlineData(906, 13, 10)]
    public void NativeNamesSelectFactorsWithoutFixedSelectorsOrASecondChanceRoll(int selector, int experience, int lucci)
    {
        var catalog = Catalog();
        Assert.Equal(0, catalog.Skills[selector].Weight);
        var result = new PetRewardPolicy(catalog, Options()).Apply(1, Equipment(selector), BaseReward);
        Assert.Equal(new RoundRewardAmounts(experience, lucci, 7), result);
    }

    /// <summary>A familiar selector number cannot give an ordinary buff reward semantics.</summary>
    [Fact]
    public void SelectorNumberAloneDoesNotGrantAReward()
    {
        var policy = new PetRewardPolicy(Catalog(), Options());
        Assert.Equal(BaseReward, policy.Apply(1, Equipment(27), BaseReward));
    }

    /// <summary>Experience and Lucci effects combine independently before optional pickup multiplication.</summary>
    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 14)]
    public void CurrenciesStayIndependentAndPickupsFollowExplicitPolicy(bool includePickups, int pickedUp)
    {
        var options = Options();
        options.IncludeCollectedLucci = includePickups;
        var result = new PetRewardPolicy(Catalog(), options).Apply(1, Equipment(901, 904), BaseReward);
        Assert.Equal(new RoundRewardAmounts(20, 20, pickedUp), result);
        Assert.Equal(20 + pickedUp, result.TotalLucci);
    }

    /// <summary>Distinct earned stages follow the configured stacking rule for the same currency.</summary>
    [Theory]
    [InlineData(PetRewardStacking.Highest, 20)]
    [InlineData(PetRewardStacking.AdditiveBonus, 25)]
    [InlineData(PetRewardStacking.Multiplicative, 30)]
    public void StackingIsExplicitPerCurrency(PetRewardStacking stacking, int lucci)
    {
        var options = Options();
        options.Stacking = stacking;
        var result = new PetRewardPolicy(Catalog(), options).Apply(1, Equipment(901, 902), BaseReward);
        Assert.Equal(new RoundRewardAmounts(10, lucci, 7), result);
    }

    /// <summary>Legacy duplicate selectors in different earned stages remain eligible under the chosen policy.</summary>
    [Fact]
    public void DuplicateSelectorsAtDifferentAcquisitionStagesAreNotDiscarded()
    {
        var options = Options();
        options.Stacking = PetRewardStacking.AdditiveBonus;
        var result = new PetRewardPolicy(Catalog(), options).Apply(1, Equipment(901, 901), BaseReward);
        Assert.Equal(30, result.CompletionLucci);
    }

    /// <summary>Rounding applies to the combined factor and defines both fractional and exact-half behavior.</summary>
    [Theory]
    [InlineData(PetRewardRounding.Floor, 1, 6)]
    [InlineData(PetRewardRounding.Nearest, 1, 7)]
    [InlineData(PetRewardRounding.Ceiling, 2, 7)]
    public void RoundingHasDefinedFractionAndHalfCreditResults(PetRewardRounding rounding, int one, int five)
    {
        var options = Options();
        options.Rounding = rounding;
        var policy = new PetRewardPolicy(Catalog(), options);
        Assert.Equal(one, policy.Apply(1, Equipment(906), new(1, 0, 0)).Experience);
        Assert.Equal(five, policy.Apply(1, Equipment(906), new(5, 0, 0)).Experience);
    }

    /// <summary>Chained effects round once instead of discarding a fraction between earned skills.</summary>
    [Fact]
    public void FactorsCombineBeforeRoundingEachComponent()
    {
        var options = Options();
        options.Stacking = PetRewardStacking.Multiplicative;
        var result = new PetRewardPolicy(Catalog(), options).Apply(1, Equipment(903, 902), new(0, 3, 0));
        Assert.Equal(5, result.CompletionLucci);
    }

    /// <summary>All six factors are explicit configuration values captured when the policy is constructed.</summary>
    [Fact]
    public void EveryConfiguredFactorAndEligibilityListAreFrozen()
    {
        var options = Options();
        options.LucciBonus1Multiplier = 3m;
        options.LucciBonus2Multiplier = 4m;
        options.LucciBonus3Multiplier = 5m;
        options.ExpBonus1Multiplier = 6m;
        options.ExpBonus2Multiplier = 7m;
        options.ExpBonus3Multiplier = 8m;
        var policy = new PetRewardPolicy(Catalog(), options);
        options.ModeIds[0] = 2;
        options.Enabled = false;
        options.LucciBonus1Multiplier = 1m;
        options.ExpBonus3Multiplier = 1m;
        options.IncludeCollectedLucci = true;
        options.Stacking = PetRewardStacking.Multiplicative;
        for (var index = 0; index < 6; index++)
        {
            var result = policy.Apply(1, Equipment(901 + index), BaseReward);
            Assert.Equal(index < 3 ? (3 + index) * 10 : 10, result.CompletionLucci);
            Assert.Equal(index >= 3 ? (3 + index) * 10 : 10, result.Experience);
            Assert.Equal(7, result.CollectedLucci);
        }
        Assert.Equal(BaseReward, policy.Apply(2, Equipment(901), BaseReward));
        Assert.Equal(30, policy.Apply(1, Equipment(901, 901), BaseReward).CompletionLucci);
    }

    /// <summary>Disabled, excluded and absent pet effects leave already prepared rewards unchanged.</summary>
    [Fact]
    public void OnlyEnabledModesAndWornNativePetsCanContribute()
    {
        var options = Options();
        options.Enabled = false;
        Assert.Same(BaseReward, new PetRewardPolicy(Catalog(), options).Apply(1, Equipment(901), BaseReward));
        options.Enabled = true;
        var policy = new PetRewardPolicy(Catalog(), options);
        Assert.Same(BaseReward, policy.Apply(2, Equipment(901), BaseReward));
        Assert.Equal(BaseReward, policy.Apply(1, RoundEquipmentSnapshot.Empty, BaseReward));
        var other = new RoundEquipmentSnapshot([OtherItemId], [Attribute(OtherItemId, 901, 1)]);
        Assert.Equal(BaseReward, policy.Apply(1, other, BaseReward));
        Assert.Equal(new RoundRewardAmounts(0, 0, 0), policy.Apply(1, Equipment(901, 904), new(0, 0, 0)));
    }

    /// <summary>Snapshot collection mutations cannot change equipment, modifier words or their native trailing fields.</summary>
    [Fact]
    public void EquipmentSnapshotDeepCopiesEveryMutableCollection()
    {
        int[] ids = [PetId];
        uint[] values = [0, 0, 0, 0];
        RoomItemAttribute[] attributes = [new(PetId, 901, values, 1, "retained")];
        var snapshot = new RoundEquipmentSnapshot(ids, attributes);
        ids[0] = OtherItemId;
        values[0] = uint.MaxValue;
        attributes[0] = Attribute(OtherItemId, 906, 2);
        Assert.Equal(PetId, Assert.Single(snapshot.WornItemIds));
        var copied = Assert.Single(snapshot.ItemAttributes);
        Assert.Equal(new uint[] { 0, 0, 0, 0 }, copied.Values);
        Assert.Equal((PetId, 901u, 1u, "retained"), (copied.ItemId, copied.Kind, copied.Unknown28, copied.Unknown32));
        Assert.Throws<NotSupportedException>(() => ((IList<int>)snapshot.WornItemIds)[0] = OtherItemId);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)copied.Values)[0] = 1);
        Assert.Throws<NotSupportedException>(() => ((IList<RoomItemAttribute>)snapshot.ItemAttributes)[0] = attributes[0]);
        Assert.Equal(20, new PetRewardPolicy(Catalog(), Options()).Apply(1, snapshot, BaseReward).CompletionLucci);
    }

    /// <summary>Snapshot bounds reject excess allocation, malformed vectors and attributes of unworn identities.</summary>
    [Fact]
    public void SnapshotRejectsExcessiveOrUnrelatedData()
    {
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId, PetId], []));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([0], []));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot(
            Enumerable.Range(1, InventoryLimits.MaxEquippedSlots + 1).ToArray(), []));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId],
            Enumerable.Repeat(Attribute(PetId, 901, 1), InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray()));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId],
            [Attribute(PetId, 901, 1) with { Values = new uint[InventoryLimits.MaxInventorySlotsPerBag + 1] }]));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId], [Attribute(OtherItemId, 901, 1)]));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId], [null!]));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId],
            [Attribute(PetId, 901, 1) with { Values = null! }]));
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId],
            [Attribute(PetId, 901, 1) with { Unknown32 = null! }]));
        var text = new string('x', 16384);
        Assert.Throws<InvalidDataException>(() => new RoundEquipmentSnapshot([PetId],
            [Attribute(PetId, 901, 1) with { Unknown32 = text }, Attribute(PetId, 902, 2) with { Unknown32 = text }]));
        Assert.Equal(InventoryLimits.MaxEquippedSlots, new RoundEquipmentSnapshot(
            Enumerable.Range(1, InventoryLimits.MaxEquippedSlots).ToArray(), []).WornItemIds.Count);
    }

    /// <summary>Every worn pet skill must satisfy native catalog bounds and an unambiguous acquisition stage.</summary>
    [Fact]
    public void MalformedEarnedAttributesFailTheCompleteCalculation()
    {
        var policy = new PetRewardPolicy(Catalog(), Options());
        RoomItemAttribute[][] malformed =
        [
            [Attribute(PetId, 901, 1) with { Values = [0, 0, 0] }],
            [Attribute(PetId, 901, 1) with { Values = [1, 0, 0, 0] }],
            [Attribute(PetId, 901, 1) with { Values = [uint.MaxValue, 0, 0, 0] }],
            [Attribute(PetId, 999, 1)],
            [Attribute(PetId, uint.MaxValue, 1)],
            [Attribute(PetId, 901, 0)],
            [Attribute(PetId, 901, 3)],
            [Attribute(PetId, PetCatalog.BaseSelector, 1)],
            [Attribute(PetId, 901, 1), Attribute(PetId, 902, 1)],
        ];
        foreach (var attributes in malformed)
            Assert.Throws<InvalidDataException>(() => policy.Apply(1, new([PetId], attributes), BaseReward));
    }

    /// <summary>Signed nonreward words retain their bit pattern while another positional buff supplies a reward.</summary>
    [Fact]
    public void RewardReferencesCanFollowSignedNonrewardValues()
    {
        var snapshot = new RoundEquipmentSnapshot([PetId],
            [Attribute(PetId, 950, 1) with { Values = [unchecked((uint)-2), 0, 0, 0] }]);
        var result = new PetRewardPolicy(Catalog(), Options()).Apply(1, snapshot, BaseReward);
        Assert.Equal(new RoundRewardAmounts(20, 10, 7), result);
    }

    /// <summary>Reward catalogs must preserve their zero payload metadata instead of inventing stat values.</summary>
    [Fact]
    public void NonzeroNativeRewardPayloadMetadataFailsActivation()
    {
        Assert.Throws<InvalidDataException>(() => new PetRewardPolicy(Catalog(nonzeroRewardMetadata: true), Options()));
    }

    /// <summary>Invalid modes, enum values and any unsupported factor are rejected before hook registration.</summary>
    [Fact]
    public void InvalidConfigurationCannotCreateARewardPolicy()
    {
        PetRewardOptions[] invalid =
        [
            new() { Enabled = true },
            new() { ModeIds = null! },
            new() { ModeIds = [0] },
            new() { ModeIds = [65536] },
            new() { ModeIds = [1, 1] },
            new() { Stacking = (PetRewardStacking)99 },
            new() { Rounding = (PetRewardRounding)99 },
            new() { LucciBonus1Multiplier = 0.9m },
            new() { LucciBonus2Multiplier = 101m },
            new() { LucciBonus3Multiplier = decimal.MaxValue },
            new() { ExpBonus1Multiplier = -1m },
            new() { ExpBonus2Multiplier = 0m },
            new() { ExpBonus3Multiplier = 100.1m },
        ];
        foreach (var options in invalid)
            Assert.Throws<ConfigException>(() => new PetRewardPolicy(Catalog(), options));
        Assert.Empty(new PetRewardOptions().Validate());
    }

    /// <summary>Negative components and both multiplication and combined-credit overflows fail before settlement.</summary>
    [Fact]
    public void InvalidAmountsAndArithmeticOverflowAreNotClamped()
    {
        var policy = new PetRewardPolicy(Catalog(), Options());
        foreach (var reward in new[] { new RoundRewardAmounts(-1, 0, 0), new(0, -1, 0), new(0, 0, -1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => policy.Apply(1, Equipment(901), reward));
        Assert.Throws<OverflowException>(() => policy.Apply(1, Equipment(901), new(0, int.MaxValue, 0)));
        Assert.Throws<OverflowException>(() => policy.Apply(1, Equipment(904), new(int.MaxValue, 0, 0)));
        Assert.Throws<OverflowException>(() => policy.Apply(1, Equipment(901), new(0, int.MaxValue / 2, 2)));
        Assert.Throws<OverflowException>(() => new RoundRewardAmounts(0, int.MaxValue, 1).Validate());
        var options = Options();
        options.Stacking = PetRewardStacking.Multiplicative;
        options.LucciBonus1Multiplier = 100m;
        options.LucciBonus2Multiplier = 100m;
        var ids = Enumerable.Range(PetId, 10).ToArray();
        var attributes = ids.SelectMany(id => new[] { Attribute(id, 901, 1), Attribute(id, 902, 2) }).ToArray();
        Assert.Throws<OverflowException>(() => new PetRewardPolicy(Catalog(petCount: 10), options)
            .Apply(1, new(ids, attributes), BaseReward));
    }

    /// <summary>The hook adjusts the downstream reward, passes through other protocols and disappears on unload.</summary>
    [Fact]
    public async Task HookChainsAfterNextAndItsEffectUnloads()
    {
        await using var fixture = new HookFixture("game");
        PetRewardHook.Register(fixture.Context, Catalog(), "game", Options());
        using var downstream = fixture.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (_, next, ct) =>
        {
            var reward = await next(ct);
            return reward with { CompletionLucci = reward.CompletionLucci + 3 };
        }, priority: -1);
        var request = Request(Equipment(901));
        var terminals = 0;
        ValueTask<RoundRewardAmounts> Terminal(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            terminals++;
            return ValueTask.FromResult(request.BaseReward);
        }
        var result = await fixture.Events.WaterfallAsync<RoundRewardPreparing, RoundRewardAmounts>(request, Terminal, default);
        Assert.Equal(new RoundRewardAmounts(10, 26, 7), result);
        Assert.Equal(1, terminals);
        result = await fixture.Events.WaterfallAsync<RoundRewardPreparing, RoundRewardAmounts>(
            request with { Protocol = "other" }, Terminal, default);
        Assert.Equal(new RoundRewardAmounts(10, 13, 7), result);
        await fixture.Context.UnwindAsync();
        result = await fixture.Events.WaterfallAsync<RoundRewardPreparing, RoundRewardAmounts>(request, Terminal, default);
        Assert.Equal(new RoundRewardAmounts(10, 13, 7), result);
    }

    /// <summary>An enabled hook requires exactly one provider for its own protocol; disabled rewards require none.</summary>
    [Fact]
    public async Task CapabilityValidationIsProtocolScopedAndRejectsAmbiguity()
    {
        await using var absent = new HookFixture();
        Assert.Throws<ConfigException>(() => PetRewardHook.Register(absent.Context, Catalog(), "game", Options()));
        PetRewardHook.Register(absent.Context, Catalog(), "game", new PetRewardOptions());
        await using var wrong = new HookFixture("other");
        Assert.Throws<ConfigException>(() => PetRewardHook.Register(wrong.Context, Catalog(), "game", Options()));
        await using var duplicate = new HookFixture("game", "game");
        Assert.Throws<ConfigException>(() => PetRewardHook.Register(duplicate.Context, Catalog(), "game", Options()));
        await using var distinct = new HookFixture("game", "other");
        PetRewardHook.Register(distinct.Context, Catalog(), "game", Options());
    }

    /// <summary>A failed downstream preparation propagates without producing a reward result.</summary>
    [Fact]
    public async Task PreparationFailurePropagatesFromTheNextMiddleware()
    {
        await using var fixture = new HookFixture("game");
        PetRewardHook.Register(fixture.Context, Catalog(), "game", Options());
        await Assert.ThrowsAsync<IOException>(() => fixture.Events.WaterfallAsync<RoundRewardPreparing, RoundRewardAmounts>(
            Request(Equipment(901)), _ => ValueTask.FromException<RoundRewardAmounts>(new IOException("Preparation failed.")),
            default).AsTask());
    }

    /// <summary>One adjusted command credits both balances atomically and a repeated match returns its first receipt.</summary>
    [Fact]
    public async Task PreparedRewardUsesTheExistingSingleSettlementReceipt()
    {
        await using var fixture = new HookFixture("game");
        PetRewardHook.Register(fixture.Context, Catalog(), "game", Options());
        var request = Request(Equipment(901, 904));
        var reward = await fixture.Events.WaterfallAsync<RoundRewardPreparing, RoundRewardAmounts>(request,
            _ => ValueTask.FromResult(request.BaseReward), default);
        var table = new MemoryPlayerTable(1);
        var wallet = new MemoryPlayerWalletRepository(table);
        var progress = new MemoryPlayerProgressRepository(table);
        await wallet.GetOrCreateAsync(new PlayerWallet(7, 100, 0), default);
        var command = new RoundSettlementCommand(request.MatchId, 7, reward.TotalLucci,
            stored => stored with { Exp = checked(stored.Exp + reward.Experience) });
        var first = await progress.SettleRoundAsync(command, default);
        var replay = await progress.SettleRoundAsync(command with
        {
            LucciEarned = 999,
            Advance = _ => throw new InvalidOperationException("A stored receipt cannot recompute experience."),
        }, default);
        Assert.Equal(RoundSettlementOutcome.Settled, first.Outcome);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled, replay.Outcome);
        Assert.Equal(20, replay.After.Exp);
        Assert.Equal(27, replay.LucciEarned);
        Assert.Equal(127, (await wallet.FindByUserNumAsync(7, default))!.Lucci);
        Assert.Equal(new PlayerProgress(7, 1, 20), await progress.FindAsync(7, default));
    }

    /// <summary>A wallet overflow leaves adjusted experience untouched so the same command can retry successfully.</summary>
    [Fact]
    public async Task FailedAtomicSettlementDoesNotPartlyCreditPetExperience()
    {
        var reward = new PetRewardPolicy(Catalog(), Options()).Apply(1, Equipment(901, 904), BaseReward);
        var table = new MemoryPlayerTable(1);
        var wallet = new MemoryPlayerWalletRepository(table);
        var progress = new MemoryPlayerProgressRepository(table);
        await wallet.GetOrCreateAsync(new PlayerWallet(7, int.MaxValue, 0), default);
        var command = new RoundSettlementCommand("pet-round", 7, reward.TotalLucci,
            stored => stored with { Exp = checked(stored.Exp + reward.Experience) });
        await Assert.ThrowsAsync<OverflowException>(() => progress.SettleRoundAsync(command, default).AsTask());
        Assert.Equal(new PlayerProgress(7, 1, 0), await progress.FindAsync(7, default));
        Assert.Equal(int.MaxValue, (await wallet.FindByUserNumAsync(7, default))!.Lucci);
        await wallet.UpsertAsync(new PlayerWallet(7, 0, 0), default);
        Assert.Equal(RoundSettlementOutcome.Settled, (await progress.SettleRoundAsync(command, default)).Outcome);
        Assert.Equal(new PlayerProgress(7, 1, 20), await progress.FindAsync(7, default));
        Assert.Equal(27, (await wallet.FindByUserNumAsync(7, default))!.Lucci);
    }

    private static PetRewardOptions Options() => new() { Enabled = true, ModeIds = [1] };

    private static RoundRewardPreparing Request(RoundEquipmentSnapshot equipment) =>
        new("game", "pet-round", 7, 1, RoundEndReason.TimeLimit, equipment, BaseReward);

    private static RoundEquipmentSnapshot Equipment(params int[] selectors) => new([PetId],
        new[] { Attribute(PetId, PetCatalog.BaseSelector, 0) }
            .Concat(selectors.Select((selector, index) => Attribute(PetId, (uint)selector, (uint)(index + 1)))).ToArray());

    private static RoomItemAttribute Attribute(int itemId, uint selector, uint stage) =>
        new(itemId, selector, new uint[] { 0, 0, 0, 0 }, stage, "");

    private static PetCatalog Catalog(bool nonzeroRewardMetadata = false, int petCount = 1)
    {
        var items = Enumerable.Range(PetId, petCount).ToDictionary(id => id, id => new PetItem(id, 10001, 2, 777));
        items.Add(OtherItemId, new(OtherItemId, 19, 0, 777));
        var skills = new Dictionary<int, PetSkillDefinition>
        {
            [7] = new(7, 777, 0, 100, 0, [0, 18, 16, 0]),
            [27] = new(27, 777, 1, 1, 0, [400, 0, 0, 0]),
            [950] = new(950, 777, 1, 0, 0, [450, 0, 404, 0]),
        };
        var buffs = new Dictionary<int, PetBuffDefinition>
        {
            [16] = new(16, "gradeExp1", 6, 0, 10, 1, 0, 10),
            [17] = new(17, "gradeExp2", 7, 0, 100, 1, 0, 100),
            [18] = new(18, "hunger", 8, 86, 100, 1, 86, 100),
            [400] = new(400, "ordinaryCoinBoost", 70, 0, 0, 0, 0, 0),
            [450] = new(450, "ordinarySignedStat", 71, -2, -2, -0.1m, -2, -2),
        };
        string[] names = ["lucciBonus1", "lucciBonus2", "lucciBonus3", "expBonus1", "expBonus2", "expBonus3"];
        for (var index = 0; index < names.Length; index++)
        {
            var selector = 901 + index;
            var buffIndex = 401 + index;
            skills.Add(selector, new(selector, 777, 1, 0, 0, [buffIndex, 0, 0, 0]));
            buffs.Add(buffIndex, new(buffIndex, names[index], index < 3 ? 27 : 28, 0,
                nonzeroRewardMetadata ? 1 : 0, 0, 0, nonzeroRewardMetadata ? 1 : 0));
        }
        return new PetCatalog(items, skills, buffs, 777);
    }

    private sealed record Capability(string Protocol) : IPreparedRoundRewards;

    private sealed class HookFixture : IAsyncDisposable
    {
        internal HookFixture(params string[] protocols)
        {
            var services = new ServiceRegistry();
            Events = new EventBus(NullLogger.Instance);
            Context = new PluginContext(new PluginRow { Id = "pets", Plugin = "pets", Source = "test" },
                services, Events, NullLogger.Instance);
            foreach (var protocol in protocols)
                Context.Effect(services.RegisterMany<IPreparedRoundRewards>(new Capability(protocol)));
        }

        internal EventBus Events { get; }
        internal PluginContext Context { get; }

        /// <summary>Unloads the hook and its owned test capability registrations.</summary>
        public ValueTask DisposeAsync() => Context.UnwindAsync();
    }
}
