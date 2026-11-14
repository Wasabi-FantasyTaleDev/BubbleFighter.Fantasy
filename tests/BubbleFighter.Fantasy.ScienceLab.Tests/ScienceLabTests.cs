using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.ScienceLab.Tests;

public sealed class ScienceLabTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly int[] ItemIds = [11, 12, 13, 14, 101, 102, 103, 201, 202, 203, 204, 205, 206, 401, 402, 403];
    private static readonly InventoryLoadoutScope Scope = new("kr-812", ItemIds);

    [Fact]
    public void GroupedSelectionUsesStockIdentityAndExactOrderedNativeMembers()
    {
        var catalog = Catalog();
        var request = new LaboratoryRequested(11, LaboratoryOperation.Synthesis, 1, true,
            [-7001, 201, 202, -7002, 203, 204, -7003, 205, 206], [-1, -1]);
        var selected = Assert.IsAssignableFrom<IReadOnlyList<LaboratorySelection>>(catalog.Select(request));
        Assert.Equal([7001, 7002, 7003], selected.Select(selection => selection.Reference));
        Assert.Equal([201, 202], selected[0].Members);
        foreach (var materials in new int[][]
        {
            [-6001, 201, 202, -7002, 203, 204, -7003, 205, 206],
            [-7001, 202, 201, -7002, 203, 204, -7003, 205, 206],
            [-7001, 201, 202, -7002, 203, 204, -7003, 205],
            [-7001, 201, 202, 103, -7003, 205, 206],
            [-7001, 201, 202, -7001, 201, 202, -7003, 205, 206],
        })
            Assert.Null(catalog.Select(new(11, LaboratoryOperation.Synthesis, 1, true, materials, [-1, -1])));
        Assert.Null(catalog.Select(new(11, LaboratoryOperation.Synthesis, 2, true, request.Materials, [-1, -1])));
    }

    [Fact]
    public void NativeStockIdentityMayHaveDifferentExactMembershipAlternatives()
    {
        var items = Catalog().Items;
        var catalog = new LaboratoryCatalog(items, new Dictionary<int, IReadOnlyList<int[]>>
        { [7001] = new[] { new[] { 201, 202 }, new[] { 203, 204 } } }, new Dictionary<int, LaboratoryAssistant>());
        var selected = Assert.Single(catalog.Select(new(12, LaboratoryOperation.Decomposition, 1, true,
            [-7001, 203, 204], [-1, -1]))!);
        Assert.Equal([203, 204], selected.Members);
        var ambiguous = new LaboratoryCatalog(items, new Dictionary<int, IReadOnlyList<int[]>>
        { [7001] = new[] { new[] { 201 }, new[] { 201, 202 } } }, new Dictionary<int, LaboratoryAssistant>());
        Assert.Null(ambiguous.Select(new(12, LaboratoryOperation.Decomposition, 1, true, [-7001, 201, 202], [-1, -1])));
    }

    [Theory]
    [InlineData(10, 20, InventoryGrantKind.Days, 6.75, LaboratoryOutcome.Ordinary)]
    [InlineData(5, 20, InventoryGrantKind.Days, 5, LaboratoryOutcome.DurationCapped)]
    [InlineData(5, 6, InventoryGrantKind.Permanent, 1, LaboratoryOutcome.BecamePermanent)]
    public void SynthesisRetainsFractionalDaysAndAppliesOnlyExplicitPolicy(double maximum, double permanentAt,
        InventoryGrantKind kind, double amount, LaboratoryOutcome outcome)
    {
        var options = Options();
        options.SynthesisMaximumDays = maximum;
        options.SynthesisPermanentAtDays = permanentAt;
        var plan = Assert.IsType<LaboratoryRecipePlan>(Plan(options, Rows(), Request(), _ => throw new InvalidOperationException()));
        Assert.Equal(101, plan.PrimaryReference);
        Assert.Equal(outcome, plan.Outcome);
        var grant = Assert.Single(plan.Command.Grants);
        Assert.Equal(kind, grant.Term.Kind);
        Assert.Equal(amount, grant.Term.Amount);
        Assert.Equal(4, plan.Command.Debits.Count);
    }

    [Theory]
    [InlineData("missing_card")]
    [InlineData("missing_additive")]
    [InlineData("worn_material")]
    [InlineData("expired_material")]
    [InlineData("unknown_lifetime")]
    [InlineData("duplicate_material")]
    [InlineData("invalid_additive")]
    public void InvalidInputsAreRejectedBeforeRandomSelection(string reason)
    {
        var options = Options();
        options.SynthesisSurvivor = "uniform";
        options.BonusAdditiveItemId = 14;
        options.BonusAdditiveQuantity = 1;
        options.BonusReward = Reward(401, InventoryGrantKind.Count, 2, 100);
        var rows = Rows().ToList();
        var request = Request();
        if (reason == "missing_card") rows.RemoveAll(row => row.ItemId == 11);
        if (reason == "missing_additive") request = Request(additives: [14, -1]);
        if (reason is "worn_material" or "expired_material" or "unknown_lifetime")
        {
            var index = rows.FindIndex(row => row.ItemId == 102);
            rows[index] = reason switch
            {
                "worn_material" => rows[index] with { Flags = InventorySlotRecord.WornFlagBit },
                "expired_material" => rows[index] with { Lifetime = InventoryLifetime.Until(Now.AddSeconds(-1)) },
                _ => rows[index] with { Lifetime = default },
            };
        }
        if (reason == "duplicate_material") request = Request(materials: [101, 102, 101]);
        if (reason == "invalid_additive") request = Request(additives: [13, -1]);
        Assert.Null(Plan(options, rows, request, _ => throw new InvalidOperationException("Invalid request used randomness.")));
    }

    [Theory]
    [InlineData(0, InventoryGrantKind.Permanent, LaboratoryOutcome.AdditivePermanent)]
    [InlineData(9999, InventoryGrantKind.Days, LaboratoryOutcome.AdditiveBonus)]
    public void PermanentAdditiveDebitsOnceAndUsesConfiguredFailureReward(int sample, InventoryGrantKind kind, LaboratoryOutcome outcome)
    {
        var options = Options();
        options.PermanentAdditiveItemId = 13;
        options.PermanentAdditiveQuantity = 2;
        options.PermanentAdditiveBasisPoints = 5000;
        options.PermanentFailureReward = Reward(401, InventoryGrantKind.Count, 4, 100);
        var rows = Rows().Append(Counted(13, 2)).ToArray();
        var plan = Assert.IsType<LaboratoryRecipePlan>(Plan(options, rows, Request(additives: [13, -1]), count =>
        { Assert.Equal(10000, count); return sample; }));
        Assert.Equal(outcome, plan.Outcome);
        Assert.Equal(kind, plan.Command.Grants.Single(grant => grant.Term.ItemId == 101).Term.Kind);
        Assert.Equal(2, plan.Command.Debits.Single(debit => debit.ExpectedSlot.ItemId == 13).Amount);
        Assert.Equal(sample == 9999, plan.Command.Grants.Any(grant => grant.Term.ItemId == 401));
    }

    [Theory]
    [InlineData(0, 401, InventoryGrantKind.Count)]
    [InlineData(1, 402, InventoryGrantKind.Days)]
    [InlineData(3, 403, InventoryGrantKind.Days)]
    public void DecompositionRequiresPermanentOwnershipAndSelectsConfiguredWeightedReward(int sample, int item, InventoryGrantKind kind)
    {
        var options = Options();
        options.Decomposition = [new() { CardItemId = 12, Category = 2, Rewards =
            [Reward(401, InventoryGrantKind.Count, 3, 100), Reward(402, InventoryGrantKind.Days, 2, 50, 2),
                Reward(403, InventoryGrantKind.Days, 7, 50)] }];
        var request = new LaboratoryRequested(12, LaboratoryOperation.Decomposition, 2, false, [101], [-1, -1]);
        var rows = Rows().Append(Counted(12, 2)).Select(row => row.ItemId == 101 ? row with { Lifetime = InventoryLifetime.Permanent } : row).ToArray();
        var plan = Assert.IsType<LaboratoryRecipePlan>(Plan(options, rows, request, count => { Assert.Equal(4, count); return sample; }));
        Assert.Equal(options.Decomposition[0].Rewards.Single(reward => reward.ItemId == item).StockId, plan.PrimaryReference);
        Assert.Equal([item], plan.PrimaryMembers);
        Assert.Equal(kind, Assert.Single(plan.Command.Grants).Term.Kind);
        Assert.Equal(2, plan.Command.Debits.Count);
        Assert.Null(Plan(options, Rows().Append(Counted(12, 2)).ToArray(), request, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void DeploymentRecipeBudgetsAndSamplingRangeAreValidated()
    {
        var options = Options();
        options.Decomposition = [new() { CardItemId = 12, Category = 2, Rewards =
            Enumerable.Range(1000, 129).Select(id => Reward(id, InventoryGrantKind.Count, 1, 2)).ToArray() }];
        Assert.Contains(options.Validate(), result => result.ErrorMessage!.Contains("max_rewards_per_recipe", StringComparison.Ordinal));
        options.MaxRewardsPerRecipe = 129;
        Assert.Empty(options.Validate());
        options.Decomposition[0].Rewards[0].Weight = int.MaxValue;
        Assert.Contains(options.Validate(), result => result.ErrorMessage!.Contains("Int32", StringComparison.Ordinal));
        options.Decomposition[0].Rewards = [Reward(401, InventoryGrantKind.Count, 1, 2, int.MaxValue)];
        Assert.Empty(options.Validate());
        options.CardQuantity = null;
        Assert.Contains(options.Validate(), result => result.ErrorMessage!.Contains("card_quantity", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("rows")]
    [InlineData("columns")]
    [InlineData("cell")]
    [InlineData("assistant_count")]
    public void NativeCatalogLoadingHonorsConfiguredBudgetsAndRejectsInconsistentAssistants(string reason)
    {
        var options = Options();
        if (reason == "bytes") options.MaxWorkbookBytes = 4;
        if (reason == "rows") options.MaxCatalogRows = 3;
        if (reason == "columns") options.MaxCatalogColumns = 2;
        if (reason == "cell") options.MaxCellCharacters = 2;
        Assert.Throws<InvalidDataException>(() => LaboratoryCatalog.Load(new CatalogData(reason == "assistant_count"), options));
    }

    [Fact]
    public void StyledEmptyColumnsPreserveSparsePositionsWithoutConsumingTheDataColumnBudget()
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        var options = Options();
        options.MaxCatalogColumns = 8;
        var catalog = LaboratoryCatalog.Load(new CatalogData(alterWorkbook: workbook =>
        {
            var sheet = workbook.Elements(ns + "Worksheet").Single(value => (string?)value.Attribute(ns + "Name") == "ShopItem");
            foreach (var row in sheet.Descendants(ns + "Row"))
            {
                var first = row.Elements(ns + "Cell").First();
                var second = row.Elements(ns + "Cell").ElementAt(1);
                second.SetAttributeValue(ns + "Index", 4);
                first.AddAfterSelf(new XElement(ns + "Cell", new XAttribute(ns + "StyleID", "empty")));
                row.Add(Enumerable.Range(0, 300).Select(_ => new XElement(ns + "Cell", new XAttribute(ns + "StyleID", "empty"))));
                row.Add(new XElement(ns + "Cell", new XAttribute(ns + "Index", 16384), new XAttribute(ns + "StyleID", "empty")));
            }
        }), options);
        Assert.Equal(2, catalog.Items[101].Category);
        Assert.Equal("day", catalog.Items[101].DurationType);
        Assert.Equal(999, catalog.Items[101].Maximum);
        Assert.True(catalog.Items[101].Available);
        catalog.RequirePolicy(options, Scope);
    }

    [Theory]
    [InlineData("value", "max_catalog_columns")]
    [InlineData("overlap", "overlap")]
    [InlineData("backward", "backwards")]
    [InlineData("overflow", "overflows")]
    public void SkippedEmptyColumnsStillRejectInvalidIndicesAndLaterUnboundedData(string shape, string error)
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        var store = new CatalogData(alterWorkbook: workbook =>
        {
            var row = workbook.Elements(ns + "Worksheet").Single(value => (string?)value.Attribute(ns + "Name") == "ShopItem")
                .Descendants(ns + "Row").First();
            row.Add(new XElement(ns + "Cell", new XAttribute(ns + "Index", shape == "overflow" ? int.MaxValue : 257)));
            var following = new XElement(ns + "Cell");
            if (shape == "value") following.Add(new XElement(ns + "Data", "out of bounds"));
            if (shape is "overlap" or "backward") following.SetAttributeValue(ns + "Index", shape == "overlap" ? 257 : 7);
            row.Add(following);
        });
        var exception = Assert.Throws<InvalidDataException>(() => LaboratoryCatalog.Load(store, Options()));
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("equivalent")]
    [InlineData("item")]
    [InlineData("duration")]
    public void RepeatedRewardStockRequiresEquivalentNativeResultTerms(string difference)
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        var reward = Reward(401, InventoryGrantKind.Count, 3, 100);
        var options = Options();
        options.Decomposition = [new() { CardItemId = 12, Category = 2, Rewards = [reward] }];
        var store = new CatalogData(alterWorkbook: workbook =>
        {
            var table = workbook.Elements(ns + "Worksheet").Single(value => (string?)value.Attribute(ns + "Name") == "Stock")
                .Element(ns + "Table")!;
            var source = table.Elements(ns + "Row").Single(row => row.Elements(ns + "Cell").First().Element(ns + "Data")!.Value
                == reward.StockId.ToString(CultureInfo.InvariantCulture));
            var duplicate = new XElement(source);
            if (difference != "equivalent")
                duplicate.Elements(ns + "Cell").ElementAt(difference == "item" ? 1 : 2).Element(ns + "Data")!.Value
                    = difference == "item" ? "402" : "4";
            table.Add(duplicate);
        });
        if (difference == "equivalent")
        {
            var catalog = LaboratoryCatalog.Load(store, options);
            catalog.RequirePolicy(options, Scope);
            catalog.RequireRewardStock(reward);
        }
        else
        {
            var exception = Assert.Throws<InvalidDataException>(() => LaboratoryCatalog.Load(store, options));
            Assert.Contains("conflicting terms", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NativeCardOperationAndRewardDurationMustMatchTheConfiguredPolicy()
    {
        var options = Options();
        var catalog = Catalog();
        catalog.RequirePolicy(options, Scope);
        options.SynthesisCardItemId = 12;
        Assert.Throws<InvalidDataException>(() => catalog.RequirePolicy(options, Scope));
        options.SynthesisCardItemId = 11;
        options.BonusReward = Reward(401, InventoryGrantKind.Days, 1, 100);
        Assert.Throws<InvalidDataException>(() => catalog.RequirePolicy(options, Scope));
    }

    [Theory]
    [InlineData("base_item")]
    [InlineData("missing_stock")]
    [InlineData("wrong_item")]
    [InlineData("wrong_quantity")]
    [InlineData("fractional_stock_days")]
    [InlineData("permanent_stock")]
    public void RewardsRequireExactRepresentableNativeStockTerms(string reason)
    {
        var options = Options();
        var reward = Reward(401, InventoryGrantKind.Count, 3, 100);
        if (reason == "base_item") reward.StockId = 401;
        if (reason == "missing_stock") reward.StockId = 1999999;
        if (reason == "wrong_item") reward.StockId = 1040203;
        if (reason == "wrong_quantity") reward.StockId = 1040102;
        if (reason == "fractional_stock_days") reward = Reward(402, InventoryGrantKind.Days, 1.5, 100);
        if (reason == "permanent_stock") reward = Reward(403, InventoryGrantKind.Permanent, 1, 1);
        options.BonusReward = reward;
        Assert.Throws<InvalidDataException>(() => Catalog().RequirePolicy(options, Scope));
    }

    [Fact]
    public async Task DecompositionDisplaysAwardedStockTermsInsteadOfItemIdsOrAccumulatedOwnership()
    {
        var rig = await Rig.Create();
        var options = Options();
        var reward = Reward(401, InventoryGrantKind.Count, 3, 100);
        var bonus = Reward(402, InventoryGrantKind.Days, 2, 100);
        options.Decomposition = [new() { CardItemId = 12, Category = 2, Rewards = [reward] }];
        options.BonusAdditiveItemId = 14;
        options.BonusAdditiveQuantity = 1;
        options.BonusReward = bonus;
        Catalog().RequirePolicy(options, Scope);
        await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
            [Counted(12, 2), Counted(14, 2), Counted(101, 1), Counted(401, 10),
                Counted(402, 5) with { Lifetime = InventoryLifetime.Until(Now.AddDays(5)) }], default);
        using var runtime = rig.Runtime(options: options, choose: _ => 0);
        await runtime.HandleAsync(rig.Session, new(12, LaboratoryOperation.Decomposition, 2, false, [101], [14, -1]), default);
        var result = Assert.Single(rig.Completed);
        Assert.Equal(reward.StockId, result.PrimaryReference);
        Assert.Equal(bonus.StockId, result.SecondReference);
        Assert.Equal(0, result.ThirdReference);
        var inventory = Assert.Single(rig.Inventories).CharacterBag;
        Assert.Equal(13, inventory.Single(row => row.ItemId == 401).Quantity);
        Assert.Equal(Now.AddDays(7).ToUnixTimeMilliseconds(), inventory.Single(row => row.ItemId == 402).Lifetime.ExpiresAtUnixMilliseconds);
        Assert.Equal(["inventory", "result"], rig.Order);
    }

    [Fact]
    public async Task SynthesisKeepsItsItemReferenceWhileBothAdditionalRewardsUseStockIds()
    {
        var rig = await Rig.Create();
        var options = Options();
        options.PermanentAdditiveItemId = 13;
        options.PermanentAdditiveQuantity = 1;
        options.PermanentAdditiveBasisPoints = 0;
        options.PermanentFailureReward = Reward(401, InventoryGrantKind.Count, 4, 100);
        options.BonusAdditiveItemId = 14;
        options.BonusAdditiveQuantity = 1;
        options.BonusReward = Reward(402, InventoryGrantKind.Days, 2, 100);
        Catalog().RequirePolicy(options, Scope);
        await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [.. Rows(), Counted(13, 2), Counted(14, 2)], default);
        using var runtime = rig.Runtime(options: options, choose: _ => 9999);
        await runtime.HandleAsync(rig.Session, Request(additives: [13, 14]), default);
        var result = Assert.Single(rig.Completed);
        Assert.Equal(101, result.PrimaryReference);
        Assert.Equal(options.PermanentFailureReward.StockId, result.SecondReference);
        Assert.Equal(options.BonusReward.StockId, result.ThirdReference);
        Assert.Equal(LaboratoryOutcome.AdditiveBonus, result.Outcome);
    }

    [Fact]
    public async Task RuntimePublishesCompleteInventoryBeforeNativeResultAndReplaysCannotDebitAgain()
    {
        var rig = await Rig.Create();
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, Request(), default);
        Assert.Equal(["inventory", "result"], rig.Order);
        Assert.True(Assert.Single(rig.Inventories).IsCompleteSnapshot);
        Assert.Equal(101, Assert.Single(rig.Completed).PrimaryReference);
        Assert.Equal(Now.AddDays(6.75).ToUnixTimeMilliseconds(), rig.Completed[0].Lifetime.ExpiresAtUnixMilliseconds);
        await runtime.HandleAsync(rig.Session, Request(), default);
        Assert.Single(rig.Refused);
        Assert.Single(rig.Completed);
        Assert.Equal(1, (await rig.Store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag.Single(row => row.ItemId == 11).Quantity);
    }

    [Fact]
    public async Task FailedInventoryPublicationCannotAnnounceSuccessOrConsumeAgain()
    {
        var rig = await Rig.Create();
        rig.FailPublication = true;
        using var runtime = rig.Runtime();
        await Assert.ThrowsAsync<IOException>(() => runtime.HandleAsync(rig.Session, Request(), default).AsTask());
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Refused);
        rig.FailPublication = false;
        await runtime.HandleAsync(rig.Session, Request(), default);
        Assert.Single(rig.Refused);
        Assert.Empty(rig.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionChangeAndCapacityRefuseBeforeAnyTransaction(bool changeSession)
    {
        var rig = await Rig.Create();
        var store = new HeldStore(rig.Store);
        using var runtime = rig.Runtime(store);
        var pending = runtime.HandleAsync(rig.Session, Request(), default).AsTask();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (changeSession) rig.Session.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Room);
        else await runtime.HandleAsync(new Session(2), Request(), default);
        store.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(rig.Refused);
        Assert.Equal(changeSession ? 0 : 1, store.Writes);
        Assert.Equal(changeSession ? 0 : 1, rig.Completed.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedLaboratoryRecipeCannotPublishIntoAnotherScene(bool returnToMenu)
    {
        var rig = await Rig.Create();
        var store = new CommittedStore(rig.Store);
        using var runtime = rig.Runtime(store);
        var pending = runtime.HandleAsync(rig.Session, Request(), default).AsTask();
        await store.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var player = rig.Session.Features.Get<IPlayerSession>();
        player.MoveTo(SessionPhase.Room);
        if (returnToMenu)
        {
            player.MoveTo(SessionPhase.Plaza);
            player.MoveTo(SessionPhase.MainMenu);
        }
        store.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(rig.Order);
        var snapshot = await rig.Store.ReadLaboratoryInventoryAsync(1, Scope, default);
        Assert.Equal(1, snapshot.CharacterBag.Single(row => row.ItemId == 11).Quantity);
        Assert.Equal(Now.AddDays(6.75).ToUnixTimeMilliseconds(), snapshot.CharacterBag.Single(row => row.ItemId == 101).Lifetime.ExpiresAtUnixMilliseconds);
        Assert.Null(await rig.Store.TryLaboratoryAsync(store.Command!, default));
        Assert.Equal(snapshot.CharacterBag, (await rig.Store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag);
    }

    [Fact]
    public async Task SceneChangeDuringInventoryPublicationSuppressesLaboratoryCompletion()
    {
        var rig = await Rig.Create();
        rig.Publication = () =>
        {
            var player = rig.Session.Features.Get<IPlayerSession>();
            player.MoveTo(SessionPhase.Room);
            player.MoveTo(SessionPhase.Plaza);
            player.MoveTo(SessionPhase.MainMenu);
        };
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, Request(), default);
        Assert.Equal(["inventory"], rig.Order);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Refused);
        Assert.Equal(1, (await rig.Store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag.Single(row => row.ItemId == 11).Quantity);
    }

    [Fact]
    public async Task AlternateSetsSharingAStockReferenceRetainTheExactSelectedSurvivorMembers()
    {
        var rig = await Rig.Create();
        var catalog = new LaboratoryCatalog(Catalog().Items, new Dictionary<int, IReadOnlyList<int[]>>
        {
            [7001] = new[] { new[] { 201, 202 }, new[] { 203, 204 } },
            [7003] = new[] { new[] { 205, 206 } },
        }, new Dictionary<int, LaboratoryAssistant>());
        var rows = new[] { Counted(11, 2) }.Concat(Enumerable.Range(201, 6).Select(id =>
            new InventorySlotRecord(1, InventoryBag.CharacterBag, id, id, 2, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(1.25)) })).ToArray();
        await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        using var runtime = rig.Runtime(catalog: catalog);
        await runtime.HandleAsync(rig.Session, new(11, LaboratoryOperation.Synthesis, 1, true,
            [-7001, 201, 202, -7001, 203, 204, -7003, 205, 206], [-1, -1]), default);
        var completion = Assert.Single(rig.Completed);
        Assert.Equal(7001, completion.PrimaryReference);
        Assert.Equal(0, completion.SecondReference);
        Assert.Equal(0, completion.ThirdReference);
        Assert.Equal([11, 201, 202], Assert.Single(rig.Inventories).CharacterBag.Select(row => row.ItemId).Order());
        Assert.Equal(Now.AddDays(3.75).ToUnixTimeMilliseconds(), completion.Lifetime.ExpiresAtUnixMilliseconds);
    }

    private sealed class CommittedStore(ILaboratoryInventoryStore inner) : ILaboratoryInventoryStore
    {
        internal TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InventoryLaboratoryCommand? Command { get; private set; }
        public ValueTask<InventorySnapshot> ReadLaboratoryInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
            inner.ReadLaboratoryInventoryAsync(userNum, scope, ct);
        public async ValueTask<InventoryLaboratoryResult?> TryLaboratoryAsync(InventoryLaboratoryCommand command, CancellationToken ct)
        {
            Command = command;
            var result = await inner.TryLaboratoryAsync(command, ct);
            Assert.NotNull(result);
            Committed.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return result;
        }
    }

    private static ScienceLabOptions Options() => new()
    { Enabled = true, BuildKey = Scope.Key, Protocol = "test/game", SynthesisCardItemId = 11, CardQuantity = 1,
        SynthesisSurvivor = "first", SynthesisMaximumDays = 729, SynthesisPermanentAtDays = 730, MaxInFlightAccounts = 1 };
    private static LaboratoryRewardOptions Reward(int id, InventoryGrantKind kind, double amount, double maximum, int weight = 1) =>
        new() { StockId = 1000000 + id * 100 + (kind == InventoryGrantKind.Permanent ? 99 : checked((int)amount)),
            ItemId = id, Kind = kind, Amount = amount, Maximum = maximum, Weight = weight };
    private static LaboratoryCatalog Catalog() => LaboratoryCatalog.Load(new CatalogData(), Options());
    private static LaboratoryRequested Request(int[]? materials = null, int[]? additives = null) =>
        new(11, LaboratoryOperation.Synthesis, 2, false, materials ?? [101, 102, 103], additives ?? [-1, -1]);
    private static InventorySlotRecord Counted(int id, int quantity) =>
        new(1, InventoryBag.CharacterBag, id, id, quantity, 0) { Lifetime = InventoryLifetime.Permanent };
    private static InventorySlotRecord[] Rows() => [Counted(11, 2),
        new(1, InventoryBag.CharacterBag, 101, 101, 2, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(1.25)) },
        new(1, InventoryBag.CharacterBag, 102, 102, 3, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(2.25)) },
        new(1, InventoryBag.CharacterBag, 103, 103, 4, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(3.25)) }];
    private static LaboratoryRecipePlan? Plan(ScienceLabOptions options, IReadOnlyList<InventorySlotRecord> rows,
        LaboratoryRequested request, Func<int, int> choose) => LaboratoryPlanner.Plan(options, Catalog(), Scope, 1, new(rows, []), request, Now, choose);

    private sealed class Rig
    {
        internal MemoryInventoryRepository Store { get; } = new(new(2));
        internal Session Session { get; } = new();
        internal List<string> Order { get; } = [];
        internal List<InventoryReplaced> Inventories { get; } = [];
        internal List<LaboratoryCompleted> Completed { get; } = [];
        internal List<LaboratoryRefused> Refused { get; } = [];
        internal bool FailPublication { get; set; }
        internal Action? Publication { get; set; }
        internal static async Task<Rig> Create()
        {
            var rig = new Rig();
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, Rows(), default);
            return rig;
        }
        internal LaboratoryRuntime Runtime(ILaboratoryInventoryStore? store = null, LaboratoryCatalog? catalog = null,
            ScienceLabOptions? options = null, Func<int, int>? choose = null) => new(options ?? Options(), catalog ?? Catalog(), Scope, store ?? Store,
            new Sender<LaboratoryRefused>(message => { Refused.Add(message); Order.Add("refused"); }),
            new Sender<LaboratoryCompleted>(message => { Completed.Add(message); Order.Add("result"); }),
            new Sender<InventoryReplaced>(message =>
            {
                if (FailPublication) throw new IOException("Inventory transport failed.");
                Inventories.Add(message); Order.Add("inventory"); Publication?.Invoke();
            }), new Clock(), choose ?? (_ => throw new InvalidOperationException("No random selection configured.")));
    }

    private sealed class HeldStore(ILaboratoryInventoryStore inner) : ILaboratoryInventoryStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Writes { get; private set; }
        public async ValueTask<InventorySnapshot> ReadLaboratoryInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
        {
            var snapshot = await inner.ReadLaboratoryInventoryAsync(userNum, scope, ct);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return snapshot;
        }
        public ValueTask<InventoryLaboratoryResult?> TryLaboratoryAsync(InventoryLaboratoryCommand command, CancellationToken ct)
        { Writes++; return inner.TryLaboratoryAsync(command, ct); }
    }
    private sealed class Sender<T>(Action<T> send) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); send(message); return ValueTask.CompletedTask; }
    }
    private sealed class Session(long user = 1) : ISessionWithFeatures
    {
        public long Id => user;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new("test", "kr", 1, 0, 0, "Science-room test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(user);
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class CatalogData(bool wrongAssistantCount = false, Action<XElement>? alterWorkbook = null) : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path)
        {
            if (path.EndsWith(".ini", StringComparison.Ordinal)) return Encoding.UTF8.GetBytes(
                $"[ASSISTINFO]\nCOUNT={(wrongAssistantCount ? 3 : 4)}\nASSISTITEM_1=11#1#0#0#0#0#0#0\nASSISTITEM_2=12#2#0#0#0#0#0#0\n"
                + "ASSISTITEM_3=13#0#1#3#0#0#0#0\nASSISTITEM_4=14#0#1#2#0#0#0#0\n");
            XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
            XElement Sheet(string name, params string[][] rows) => new(ns + "Worksheet", new XAttribute(ns + "Name", name),
                new XElement(ns + "Table", rows.Select(row => new XElement(ns + "Row", row.Select(cell =>
                    new XElement(ns + "Cell", new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), cell)))))));
            var items = new List<string[]> { new[] { "itemId", "subCategory", "durationType", "maxDuration", "using", "lockType" } };
            items.AddRange(ItemIds.Select(id => new[] { id.ToString(CultureInfo.InvariantCulture), id is >= 201 and <= 206 ? "Clothes" : "Weapons",
                id is 11 or 12 or 13 or 14 or 401 ? "count" : "day", "999", "1", "0" }));
            var stocks = new List<string[]> { new[] { "stockId", "itemId", "duration" } };
            stocks.AddRange(ItemIds.SelectMany(id => new[] { 1, 2, 3, 4, 7 }.Select(amount => new[] {
                (1000000 + id * 100 + amount).ToString(CultureInfo.InvariantCulture), id.ToString(CultureInfo.InvariantCulture),
                amount.ToString(CultureInfo.InvariantCulture) })));
            stocks.Add(["1040399", "403", "-1"]);
            var workbook = new XElement(ns + "Workbook", Sheet("SubCategory", ["id", "name"], ["102", "Clothes"], ["202", "Weapons"]),
                Sheet("ShopItem", items.ToArray()), Sheet("Stock", stocks.ToArray()), Sheet("SetItem", ["setId", "setCnt", "stockSetId", "ShopItemID01", "ShopItemID02"],
                    ["6001", "2", "7001", "201", "202"], ["6002", "2", "7002", "203", "204"], ["6003", "2", "7003", "205", "206"]));
            alterWorkbook?.Invoke(workbook);
            return Encoding.UTF8.GetBytes(workbook.ToString());
        }
    }
}
