using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.ItemEnhancement.Data;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>Checks native modification selection, exact ownership and publication order through the shared runtime.</summary>
public sealed class ModificationRuntimeTests
{
    /// <summary>Existing modification effects remain present when spending is disabled and coexist with the independent enchantment selector.</summary>
    [Fact]
    public async Task MetadataPresentationKeepsSignedPairsGroupsAndTagsWithoutAnEconomy()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var setup = new PluginContext(new() { Id = "setup", Plugin = "setup", Source = "tests" }, services, events, NullLogger.Instance);
        var context = new PluginContext(new()
        {
            Id = "articles", Plugin = "item-enhancement.articles", Source = "tests", Config = new Dictionary<string, object?>()
            {
                ["enabled"] = true, ["protocol"] = "test/game", ["build_key"] = "test-catalog",
                ["weapon_attribute_id"] = 98, ["weapon_buff_id"] = 88,
                ["clothing_attribute_id"] = 99, ["clothing_buff_id"] = 89,
            },
        }, services, events, NullLogger.Instance);
        setup.Effect(services.Register<IClientDataStore>(new CatalogData()));
        setup.Effect(services.Register<IInventoryBuildScope>(new BuildScope(new("test-catalog", [202020, 190200, 190201, 190202, 190203]))));
        setup.Effect(services.Register<IItemEnhancementStore>(new MemoryInventoryRepository(new(1))));
        context.BindServices(typeof(EnhancementArticlesPlugin).GetCustomAttribute<PluginAttribute>()!);
        var slot = new InventorySlotRecord(1, InventoryBag.CharacterBag, 1, 202020, 1, 0)
        {
            Lifetime = InventoryLifetime.Permanent, ArticleIdentity = new(1, 100000000),
            Enhancements = ItemEnhancements.Empty.With("test-catalog", new(ItemEnhancementKind.Weapon, 1)),
            Modifications = ItemModifications.Empty.With("test-catalog", new(4, [new(101, 160, -40, 0, 0), new(101, 240, -60, 2, 1)])),
        };
        try
        {
            await new EnhancementArticlesPlugin().ActivateAsync(context, default);
            var presentation = await events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(new("test/game", slot),
                _ => ValueTask.FromResult(InventoryItemPresentation.Empty), default);
            Assert.Equal([98U, 101U, 101U], presentation.Attributes.Select(attribute => attribute.Kind));
            Assert.Equal(new uint[] { 160, unchecked((uint)-40) }, presentation.Attributes[1].Values);
            Assert.Equal(0U, presentation.Attributes[1].Unknown28);
            Assert.Equal("0", presentation.Attributes[1].Unknown32);
            Assert.Equal(2U, presentation.Attributes[2].Unknown28);
            Assert.Equal("1", presentation.Attributes[2].Unknown32);
            var foreign = await events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(new("other/game", slot),
                _ => ValueTask.FromResult(InventoryItemPresentation.Empty), default);
            Assert.Same(InventoryItemPresentation.Empty, foreign);
            await context.UnwindAsync();
            Assert.False(services.TryGet<IEquipmentArticleResolver>(out _));
            var unloaded = await events.WaterfallAsync<InventoryPresentationRequested, InventoryItemPresentation>(new("test/game", slot),
                _ => ValueTask.FromResult(InventoryItemPresentation.Empty), default);
            Assert.Same(InventoryItemPresentation.Empty, unloaded);
        }
        finally { await context.UnwindAsync(); await setup.UnwindAsync(); }
    }

    /// <summary>Three repeated selected parts aggregate into one debit and deletion publishes the revised inventory before its result.</summary>
    [Fact]
    public async Task RepeatedPartsAndDeletePublishCommittedWalletAndInventoryBeforeResult()
    {
        var rig = await Rig.CreateAsync();
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default);
        Assert.Equal(["balance", "inventory", "result"], rig.Order);
        Assert.Equal(new PlayerWallet(1, 900, 50), Assert.Single(rig.Balances).Wallet);
        Assert.Equal(new LabModificationCompleted(false, checked((ulong)rig.Key), 101, 0), Assert.Single(rig.Completed));
        var published = Assert.Single(rig.Published);
        Assert.True(published.IsCompleteSnapshot);
        Assert.Equal(3, published.CharacterBag.Single(row => row.ItemId == 190200).Quantity);
        Assert.Equal(new ItemModificationState(1, [new(101, 161, -40, 0, 0)]),
            published.CharacterBag.Single(row => row.ItemId == 202020).Modifications!.Find(rig.Scope.Key));
        Assert.Equal(1, rig.Rolls);
        rig.Order.Clear();
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationDelete, [rig.Key], 0), default);
        Assert.Equal(["balance", "inventory", "result"], rig.Order);
        Assert.Equal(new LabModificationCompleted(true, checked((ulong)rig.Key), 101, 0), rig.Completed[1]);
        Assert.Equal(new PlayerWallet(1, 875, 50), rig.Balances[1].Wallet);
        Assert.Equal(new ItemModificationState(2, []), rig.Published[1].CharacterBag.Single(row => row.ItemId == 202020).Modifications!.Find(rig.Scope.Key));
        Assert.Equal(1, rig.Rolls);
    }

    /// <summary>With Favorite enabled an installed grade awards the sheet's points for its first category, deletion awards nothing, and a disabled feature stays silent.</summary>
    [Theory]
    [InlineData(190200, 0, 24)]
    [InlineData(190202, 1, 12)]
    [InlineData(190200, -1, 0)]
    public async Task ModificationAwardsFavoritePointsByGradeIndexAndDeletionAwardsNone(int part, int tag, int expected)
    {
        var rig = await Rig.CreateAsync();
        if (tag >= 0)
            rig.Favorite = FavoriteWeaponCatalog.Parse([["Key", "Value"], ["IncreasePointEnchant", "30"], ["IncreasePointModification", "B,6"],
                ["IncreasePointModification", "A,12"], ["IncreasePointModification", "S,24"]],
                [["FavoriteGrade", "Point"], ["Normal", "120"], ["Expert", "420"], ["Special", "1260"], ["Master", "0"]], "test");
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationApply, [rig.Key, part, part, part], 0), default);
        var applied = Assert.Single(rig.Published).CharacterBag.Single(row => row.ItemId == 202020);
        Assert.Equal(expected == 0 ? null : (long?)expected, applied.Favorites?.Find(rig.Scope.Key));
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationDelete, [rig.Key], 0), default);
        Assert.Equal(expected == 0 ? null : (long?)expected, rig.Published[1].CharacterBag.Single(row => row.ItemId == 202020).Favorites?.Find(rig.Scope.Key));
    }
    /// <summary>A Modification that carries a weapon across a Favorite grade threshold sends one notice after its result; one that does not sends none.</summary>
    [Theory]
    [InlineData(20, true)]
    [InlineData(25, false)]
    public async Task ModificationGradeCrossingSendsOneNoticeAfterTheResult(int promotion, bool crosses)
    {
        var rig = await Rig.CreateAsync();
        rig.Favorite = FavoriteWeaponCatalog.Parse([["Key", "Value"], ["IncreasePointEnchant", "30"], ["IncreasePointModification", "B,6"],
            ["IncreasePointModification", "A,12"], ["IncreasePointModification", "S,24"]],
            [["FavoriteGrade", "Point"], ["Normal", promotion.ToString()], ["Expert", "420"], ["Special", "1260"], ["Master", "0"]], "test");
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default);
        if (!crosses) { Assert.Empty(rig.Notices); return; }
        Assert.Equal(new FavoriteWeaponGradeChanged(202020, 0, 1), Assert.Single(rig.Notices));
        Assert.Equal(["balance", "inventory", "result", "grade", "progress"], rig.Order);
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationDelete, [rig.Key], 0), default);
        Assert.Single(rig.Notices);
        Assert.Single(rig.Committed);
    }
    /// <summary>An installed effect excludes every matching part category, including another grade and a mixed three-part request.</summary>
    [Theory]
    [InlineData(190200)]
    [InlineData(190202)]
    public async Task InstalledEffectCategoryRejectsPartFromEitherNumericTag(int conflictingPart)
    {
        var rig = await Rig.CreateAsync(quantity: 30);
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default);
        Assert.Equal(new LabModificationCompleted(false, checked((ulong)rig.Key), 101, 0), Assert.Single(rig.Completed));
        var before = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        var wallet = await rig.Wallets.FindByUserNumAsync(1, default);
        rig.Order.Clear();
        rig.Completed.Clear();
        rig.Published.Clear();
        rig.Balances.Clear();

        await runtime.HandleAsync(rig.Session,
            new(LabOperation.ModificationApply, [rig.Key, conflictingPart, 190201, 190201], 0), default);

        Assert.Single(rig.Refused);
        Assert.Equal(["refused"], rig.Order);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Published);
        Assert.Empty(rig.Balances);
        Assert.Equal(1, rig.Rolls);
        var after = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        Assert.Equal(before.CharacterBag, after.CharacterBag);
        Assert.Equal(before.EquippedBag, after.EquippedBag);
        Assert.Equal(wallet, await rig.Wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>Malformed requests and unavailable equipment or parts cannot roll an outcome or debit any account state.</summary>
    [Theory]
    [InlineData("shape")]
    [InlineData("argument")]
    [InlineData("unknown_article")]
    [InlineData("unknown_part")]
    [InlineData("part_article")]
    [InlineData("insufficient")]
    [InlineData("expired")]
    [InlineData("phase")]
    [InlineData("worn")]
    [InlineData("missing_group")]
    [InlineData("invalid_group")]
    public async Task RefusedRequestCannotRollOrSpend(string reason)
    {
        var rig = await Rig.CreateAsync(expired: reason == "expired", quantity: reason == "insufficient" ? 5 : 9);
        if (reason == "phase") rig.Session.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Room);
        if (reason == "worn")
        {
            var current = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
            Assert.NotNull(await rig.Store.SelectArticleAsync(new(1, rig.Scope,
                current.CharacterBag.Single(row => row.ItemId == 202020), [], true, DateTimeOffset.UtcNow), default));
        }
        var before = await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default);
        var keys = reason == "shape" ? new long[] { rig.Key, 190200, 190200 }
            : new long[] { reason == "unknown_article" ? rig.Key + 99 : rig.Key,
                reason == "unknown_part" ? 999 : reason == "part_article" ? rig.Key : 190200, 190200, 190200 };
        var deleting = reason is "missing_group" or "invalid_group";
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(deleting ? LabOperation.ModificationDelete : LabOperation.ModificationApply,
            deleting ? [rig.Key] : keys, reason == "invalid_group" ? 3 : reason == "argument" ? 1 : 0), default);
        Assert.Single(rig.Refused);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Published);
        Assert.Empty(rig.Balances);
        Assert.Equal(0, rig.Rolls);
        Assert.Equal(before.CharacterBag, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag);
        Assert.Equal(new PlayerWallet(1, 1000, 50), await rig.Wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>The account's currency is validated inside the same transaction that would consume its selected parts.</summary>
    [Fact]
    public async Task InsufficientWalletCannotPublishOrConsumeParts()
    {
        var rig = await Rig.CreateAsync();
        await rig.Wallets.UpsertAsync(new(1, 99, 50), default);
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default);
        Assert.Single(rig.Refused);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Published);
        Assert.Empty(rig.Balances);
        Assert.Equal(9, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag.Single(row => row.ItemId == 190200).Quantity);
        Assert.Equal(new PlayerWallet(1, 99, 50), await rig.Wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>The three native groups stop further spending and the first vacant group is reused after a deletion.</summary>
    [Fact]
    public async Task FullWeaponRefusesAnotherApplyAndDeletionMakesItsGroupReusable()
    {
        var rig = await Rig.CreateAsync(quantity: 30);
        using var runtime = rig.Runtime();
        foreach (var part in new[] { 190200, 190201, 190204 })
            await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationApply, [rig.Key, part, part, part], 0), default);
        Assert.Equal([0, 1, 2], rig.Completed.Select(result => result.Group));
        var apply = new LabOperationRequested(LabOperation.ModificationApply, [rig.Key, 190201, 190201, 190201], 0);
        await runtime.HandleAsync(rig.Session, apply, default);
        Assert.Single(rig.Refused);
        Assert.Equal(3, rig.Rolls);
        Assert.Equal(new PlayerWallet(1, 700, 50), await rig.Wallets.FindByUserNumAsync(1, default));
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationDelete, [rig.Key], 1), default);
        await runtime.HandleAsync(rig.Session, apply, default);
        Assert.Equal(1, rig.Completed[^1].Group);
        var target = (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag.Single(row => row.ItemId == 202020);
        Assert.Equal(5, target.Modifications!.Find(rig.Scope.Key)!.Revision);
        Assert.Equal(3, target.Modifications.Find(rig.Scope.Key)!.Attributes.Count);
    }

    /// <summary>Inventory transport failure does not send a successful result after the committed debit.</summary>
    [Fact]
    public async Task FailedInventoryPublicationNeverSendsAnOutcome()
    {
        var rig = await Rig.CreateAsync();
        rig.FailPublication = true;
        using var runtime = rig.Runtime();
        await Assert.ThrowsAsync<IOException>(() => runtime.HandleAsync(rig.Session,
            new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default).AsTask());
        Assert.Empty(rig.Completed);
        Assert.Equal(new PlayerWallet(1, 900, 50), await rig.Wallets.FindByUserNumAsync(1, default));
        Assert.Equal(1, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag.Single(row => row.ItemId == 202020)
            .Modifications!.Find(rig.Scope.Key)!.Revision);
    }

    /// <summary>A scene or generation change while resolving the Article refuses the request before sampling or persistence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SceneAndGenerationChangesDuringResolutionCannotSpend(bool returnToMenu)
    {
        var rig = await Rig.CreateAsync();
        var resolver = new HeldResolver(rig.Articles);
        using var runtime = rig.Runtime(resolver);
        var pending = runtime.HandleAsync(rig.Session,
            new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var player = rig.Session.Features.Get<IPlayerSession>();
        player.MoveTo(SessionPhase.Room);
        if (returnToMenu)
        {
            player.MoveTo(SessionPhase.Plaza);
            player.MoveTo(SessionPhase.MainMenu);
        }
        resolver.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(rig.Refused);
        Assert.Empty(rig.Completed);
        Assert.Equal(0, rig.Rolls);
        Assert.Equal(new PlayerWallet(1, 1000, 50), await rig.Wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>A second operation from the active account cannot enter the modification pipeline while its first Article lookup is pending.</summary>
    [Fact]
    public async Task ActiveAccountRefusesConcurrentModificationWithoutQueuing()
    {
        var rig = await Rig.CreateAsync();
        var resolver = new HeldResolver(rig.Articles);
        using var runtime = rig.Runtime(resolver);
        var request = new LabOperationRequested(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0);
        var pending = runtime.HandleAsync(rig.Session, request, default).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.HandleAsync(rig.Session, request, default);
        Assert.Single(rig.Refused);
        Assert.Equal(1, resolver.Resolutions);
        resolver.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(rig.Completed);
        Assert.Equal(1, rig.Rolls);
    }

    /// <summary>Native category weights count each selected part and use the current installed-count column before choosing a selector.</summary>
    [Theory]
    [InlineData(0, 7, 17)]
    [InlineData(1, 25, 67)]
    [InlineData(2, 43, 77)]
    public void SelectionUsesSummedCategoriesAndIndependentSelectorSample(int installed, int tagBound, int effectBound)
    {
        var options = Policy();
        var policy = new ModificationPolicy(options, Catalog(), new("test-catalog", [202020, 190200, 190201, 190202, 190203, 190204, 190205]));
        var bounds = new List<int>();
        var samples = new Queue<int>([2 * options.TagWeights[0].Weights[installed], 0, 6]);
        var result = policy.Select([190200, 190200, 190203], installed, 2, bound => { bounds.Add(bound); return samples.Dequeue(); });
        Assert.Equal([tagBound, effectBound, 7], bounds);
        Assert.Equal(new ItemModificationAttribute(107, 261, -46, 2, 1), result);
        options.Parts[2].Value0 = 999;
        Assert.Equal(261, policy.Configuration.Parts.Single(part => part.ItemId == 190202).Value0);
    }

    /// <summary>Out-of-range random samples fail before any selected material or currency is consumed.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public async Task InvalidRandomSourceCannotCommit(int sample)
    {
        var rig = await Rig.CreateAsync();
        rig.Sample = sample;
        using var runtime = rig.Runtime();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.HandleAsync(rig.Session,
            new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default).AsTask());
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Balances);
        Assert.Equal(new PlayerWallet(1, 1000, 50), await rig.Wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>Admission rejects incomplete category matrices, undefined costs and malformed native effect policies.</summary>
    [Theory]
    [InlineData("price")]
    [InlineData("quantity")]
    [InlineData("matrix")]
    [InlineData("tag")]
    [InlineData("effect")]
    [InlineData("selector")]
    [InlineData("selector_owner")]
    [InlineData("negative")]
    [InlineData("weight")]
    [InlineData("weight_overflow")]
    [InlineData("selector_range")]
    public void InvalidPoliciesAreRejected(string reason)
    {
        var options = Policy();
        switch (reason)
        {
            case "price": options.DeleteLucciCost = null; break;
            case "quantity": options.PartQuantity = 0; break;
            case "matrix": options.Parts = options.Parts[..3]; break;
            case "tag": options.Parts[0].NumericTag = 3; break;
            case "effect": options.Parts[0].EffectCategory = 0; break;
            case "selector": options.Parts[0].AttributeIds[1] = 101; break;
            case "selector_owner":
                options.Parts[1].AttributeIds = options.Parts[0].AttributeIds.ToArray();
                options.Parts[1].NegativeValues = options.Parts[0].NegativeValues.ToArray();
                break;
            case "negative": options.Parts[0].NegativeValues[0].Value = 1; break;
            case "weight": options.TagWeights[0].Weights = [1, 2]; break;
            case "weight_overflow": options.TagWeights[0].Weights[0] = int.MaxValue / 3 + 1; break;
            case "selector_range": options.Parts[0].AttributeIds[0] = 157; break;
        }
        Assert.NotEmpty(options.Validate());
    }

    /// <summary>Policy accepts the signed native effect domain and the largest three-part debit and sampling sums representable by Int32.</summary>
    [Fact]
    public void PolicyBoundsFollowNativeFieldsAndIntegerArithmetic()
    {
        var options = Policy();
        options.PartQuantity = int.MaxValue / 3;
        options.Parts[0].Value0 = int.MaxValue;
        options.Parts[0].NegativeValues[0].Value = int.MinValue;
        options.Parts[0].NegativeValues[1].Value = 0;
        options.TagWeights[0].Weights = [int.MaxValue / 3, int.MaxValue / 3, int.MaxValue / 3];
        Assert.Empty(options.Validate());
        options.PartQuantity++;
        Assert.NotEmpty(options.Validate());
    }

    /// <summary>A scene change after the database commit suppresses old-scene sends without rolling back or replaying the debit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedModificationCannotPublishIntoAnotherScene(bool returnToMenu)
    {
        var rig = await Rig.CreateAsync();
        var store = new CommittedStore(rig.Store);
        using var runtime = rig.Runtime(modificationStore: store);
        var pending = runtime.HandleAsync(rig.Session,
            new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default).AsTask();
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
        Assert.Equal(new PlayerWallet(1, 900, 50), await rig.Wallets.FindByUserNumAsync(1, default));
        Assert.Equal(3, (await rig.Store.ReadEnhancementInventoryAsync(1, rig.Scope, default)).CharacterBag.Single(row => row.ItemId == 190200).Quantity);
        Assert.Null(await rig.Store.TryModifyAsync(store.Command!, default));
        Assert.Equal(new PlayerWallet(1, 900, 50), await rig.Wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>Each completed publication rechecks the generation before continuing the result sequence.</summary>
    [Theory]
    [InlineData("balance", 1)]
    [InlineData("inventory", 2)]
    public async Task SceneChangeDuringPublicationStopsLaterModificationFrames(string stage, int published)
    {
        var rig = await Rig.CreateAsync();
        rig.Publication = current =>
        {
            if (current != stage) return;
            var player = rig.Session.Features.Get<IPlayerSession>();
            player.MoveTo(SessionPhase.Room);
            player.MoveTo(SessionPhase.Plaza);
            player.MoveTo(SessionPhase.MainMenu);
        };
        using var runtime = rig.Runtime();
        await runtime.HandleAsync(rig.Session, new(LabOperation.ModificationApply, [rig.Key, 190200, 190200, 190200], 0), default);
        Assert.Equal(published, rig.Order.Count);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Refused);
        Assert.Equal(new PlayerWallet(1, 900, 50), await rig.Wallets.FindByUserNumAsync(1, default));
    }

    private sealed class CommittedStore(IItemModificationStore inner) : IItemModificationStore
    {
        internal TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ItemModificationCommand? Command { get; private set; }
        public async ValueTask<ItemModificationResult?> TryModifyAsync(ItemModificationCommand command, CancellationToken ct)
        {
            Command = command;
            var result = await inner.TryModifyAsync(command, ct);
            Assert.NotNull(result);
            Committed.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return result;
        }
    }

    private static ItemModificationOptions Policy() => new()
    {
        Enabled = true, PartQuantity = 2, ApplyLucciCost = 100, DeleteLucciCost = 25,
        Parts = new[] { (Tag: 0, Effect: 1), (Tag: 0, Effect: 2), (Tag: 1, Effect: 1),
            (Tag: 1, Effect: 2), (Tag: 0, Effect: 3), (Tag: 1, Effect: 3) }.Select((categories, index) => new ModificationPartOptions
        {
            ItemId = 190200 + index, NumericTag = categories.Tag, EffectCategory = categories.Effect,
            AttributeIds = Enumerable.Range(101 + (categories.Effect - 1) * 7, 7).ToArray(), Value0 = 160 + categories.Tag * 100 + categories.Effect,
            NegativeValues = Enumerable.Range(101 + (categories.Effect - 1) * 7, 7)
                .Select(id => new ModificationNegativeOptions { AttributeId = id, Value = -40 - (id - 101) % 7 }).ToArray(),
        }).ToArray(),
        TagWeights = [new() { Key = 0, Weights = [2, 7, 13] }, new() { Key = 1, Weights = [3, 11, 17] }],
        EffectWeights = [new() { Key = 1, Weights = [5, 19, 23] }, new() { Key = 2, Weights = [7, 29, 31] },
            new() { Key = 3, Weights = [11, 37, 41] }],
    };

    private static EnhancementCatalog Catalog() => EnhancementCatalog.Load(new CatalogData(), "catalog", 30000, 100, 100, 100);

    private sealed class Rig
    {
        private readonly MemoryPlayerTable _table = new(1);
        private IDisposable? _subscription;
        private readonly ItemEnhancementOptions _options = new() { Enabled = true, Modification = Policy() };
        internal MemoryInventoryRepository Store { get; private set; } = null!;
        internal MemoryPlayerWalletRepository Wallets { get; private set; } = null!;
        internal InventoryLoadoutScope Scope { get; } = new("test-catalog", [202020, 190200, 190201, 190202, 190203, 190204, 190205]);
        internal Session Session { get; } = new();
        internal List<string> Order { get; } = [];
        internal List<InventoryReplaced> Published { get; } = [];
        internal List<LabModificationCompleted> Completed { get; } = [];
        internal List<LabOperationRefused> Refused { get; } = [];
        internal List<ShopBalanceUpdated> Balances { get; } = [];
        internal EnhancementArticles Articles { get; private set; } = null!;
        internal EnhancementCatalog ItemCatalog { get; private set; } = null!;
        internal long Key { get; private set; }
        internal int Rolls { get; private set; }
        internal int Sample { get; set; }
        internal bool FailPublication { get; set; }
        internal FavoriteWeaponCatalog? Favorite { get; set; }
        internal List<FavoriteWeaponGradeChanged> Notices { get; } = [];
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal List<FavoriteProgressCommitted> Committed { get; } = [];
        internal Action<string>? Publication { get; set; }

        internal static async Task<Rig> CreateAsync(bool expired = false, int quantity = 9)
        {
            var rig = new Rig();
            rig.Store = new(rig._table);
            rig.Wallets = new(rig._table);
            await rig.Wallets.GetOrCreateAsync(new(1, 1000, 50), default);
            rig.ItemCatalog = Catalog();
            var rows = rig.Scope.SupportedItemIds.Select((id, index) =>
                new InventorySlotRecord(1, InventoryBag.CharacterBag, index + 1, id, id == 202020 ? 1 : quantity, 0)
                { Lifetime = expired && id == 190200 ? InventoryLifetime.Until(DateTimeOffset.UtcNow.AddMinutes(-1)) : InventoryLifetime.Permanent }).ToArray();
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
            rig.Articles = new(rig.ItemCatalog, rig.Store, rig.Scope, TimeProvider.System);
            var snapshot = await rig.Articles.PrepareAsync(1, default);
            rig.Key = checked((long)snapshot.CharacterBag.Single(row => row.ItemId == 202020).ArticleIdentity!.ArticleKey);
            return rig;
        }

        internal EnhancementRuntime Runtime(IEquipmentArticleResolver? resolver = null, IItemModificationStore? modificationStore = null)
        {
            _subscription ??= Events.OnAsync<FavoriteProgressCommitted>((message, _) =>
            { Order.Add("progress"); Committed.Add(message); return ValueTask.CompletedTask; });
            var refusals = new Sender<LabOperationRefused>(message => { Order.Add("refused"); Refused.Add(message); });
            var inventories = new Sender<InventoryReplaced>(message =>
            {
                if (FailPublication) throw new IOException("Inventory transport failed.");
                Order.Add("inventory"); Published.Add(message); Publication?.Invoke("inventory");
            });
            var modification = new ModificationRuntime(_options.Modification, new(_options.Modification, ItemCatalog, Scope),
                ItemCatalog, modificationStore ?? Store, Store, resolver ?? Articles, Scope, refusals,
                new Sender<LabModificationCompleted>(message => { Order.Add("result"); Completed.Add(message); }), inventories,
                new Sender<ShopBalanceUpdated>(message => { Order.Add("balance"); Balances.Add(message); Publication?.Invoke("balance"); }),
                TimeProvider.System, _ => { Rolls++; return Sample; }, null, Favorite,
                Favorite is null ? null : new FavoriteAwards(Favorite, new Sender<FavoriteWeaponGradeChanged>(message => { Order.Add("grade"); Notices.Add(message); }),
                    Events, "test/game", Scope.Key));
            return new(_options, ItemCatalog, Store, resolver ?? Articles, Scope, refusals,
                new Sender<LabEnchantCompleted>(_ => throw new InvalidOperationException("Unexpected enchantment response.")),
                inventories, TimeProvider.System, () => throw new InvalidOperationException("Unexpected enchantment roll."), null, modification);
        }
    }

    private sealed class HeldResolver(IEquipmentArticleResolver inner) : IEquipmentArticleResolver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Resolutions { get; private set; }
        public ArticleInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) => inner.CreateInitialization(userNum, now);
        public async ValueTask<InventorySlotRecord?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct)
        {
            Resolutions++;
            var result = await inner.ResolveAsync(userNum, articleKey, ct);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return result;
        }
    }

    private sealed class Sender<T>(Action<T> send) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); send(message); return ValueTask.CompletedTask; }
    }

    private sealed class Session : ISessionWithFeatures
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new("test", "kr", 1, 0, 0, "Synthetic test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(1);
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record BuildScope(InventoryLoadoutScope Scope) : IInventoryBuildScope;

    private sealed class CatalogData : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path)
        {
            XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
            XElement Sheet(string name, params string[][] rows) => new(ns + "Worksheet", new XAttribute(ns + "Name", name),
                new XElement(ns + "Table", rows.Select(row => new XElement(ns + "Row", row.Select(cell =>
                    new XElement(ns + "Cell", new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), cell)))))));
            var items = new List<string[]> { new[] { "itemId", "mainCategory", "subCategory", "durationType", "isEnchant", "isModificatable" },
                new[] { "202020", "Weapons", "Weapon_Rifle", "day", "1", "1" } };
            items.AddRange(Enumerable.Range(190200, 6).Select(id => new[] { id.ToString(CultureInfo.InvariantCulture), "", "SienceLab", "count", "0", "0" }));
            var attributes = new List<string[]> { new[] { "index", "value", "category", "grade", "weight", "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4" } };
            attributes.Add(["98", "2", "300", "1", "1", "88", "0", "0", "0"]);
            attributes.Add(["99", "2", "400", "1", "1", "89", "0", "0", "0"]);
            attributes.AddRange(Enumerable.Range(101, 21).Select(id => new[] { id.ToString(CultureInfo.InvariantCulture), "3", "300", "1", "1", "90", "91", "0", "0" }));
            return Encoding.UTF8.GetBytes(new XElement(ns + "Workbook",
                Sheet("MainCategory", ["id", "name"], ["300", "Weapons"], ["400", "Clothes"]),
                Sheet("SubCategory", ["id", "name"], ["302", "Weapon_Rifle"], ["507", "SienceLab"]),
                Sheet("ShopItem", items.ToArray()), Sheet("ItemAttr", attributes.ToArray()),
                Sheet("RandomTable", ["buffIndex", "buffName", "min", "max", "conv"],
                    ["88", "enchantWeapon1st", "0", "0", "0"], ["89", "enchantArmor", "0", "0", "0"],
                    ["90", "modificationDamage", "0", "0", "0.01"], ["91", "modificationAccuracy", "0", "0", "-0.01"])).ToString());
        }
    }
}
