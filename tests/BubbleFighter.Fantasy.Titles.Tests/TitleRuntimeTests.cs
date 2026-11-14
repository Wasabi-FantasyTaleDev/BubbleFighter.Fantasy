using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Titles;
using Xunit;

namespace BubbleFighter.Fantasy.Titles.Tests;

/// <summary>Checks native title operation outcomes against committed inventory and medal state.</summary>
public sealed class TitleRuntimeTests
{
    /// <summary>Both native schemas spend every recipe cost and install inventory before medal and title replies.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimPublishesTheActualGrantAndEveryDebitBeforeSuccess(bool modern)
    {
        using var rig = await TitleTestRig.CreateAsync(modern);
        using var runtime = rig.Runtime();
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);

        Assert.Equal(["inventory", "medals", "make"], rig.Order);
        var reply = Assert.Single(rig.Made);
        Assert.Equal(new TitleMakeReplied(0, 400001, 1200001), reply);
        var snapshot = Assert.Single(rig.Inventories);
        Assert.True(snapshot.IsCompleteSnapshot);
        var title = Assert.Single(snapshot.CharacterBag, row => row.ItemId == 400901);
        Assert.Equal(InventoryLifetime.Permanent, title.Lifetime);
        Assert.Equal(1, title.Quantity);
        Assert.Equal(0, title.Flags & InventorySlotRecord.WornFlagBit);
        Assert.DoesNotContain(snapshot.CharacterBag, row => row.ItemId is 400001 or 1200001);
        Assert.Equal(snapshot.CharacterBag, await rig.BagAsync());
        Assert.Equal(1, Assert.Single(rig.Balances).Result);
        Assert.Equal(new[] { new TitleMedalCount(30007, 10), new TitleMedalCount(30008, 15) }, rig.Balances[0].Medals);
        Assert.Equal(7, (await rig.Store.ReadAsync(1, rig.Scope.Key, default)).Single(row => row.MedalId == 99999).Count);
        Assert.Empty(rig.Selection.Calls);
    }

    /// <summary>Unaudited acquisition conditions and foreign requests cannot create ownership or spend medals.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("unknown")]
    [InlineData("phase")]
    [InlineData("concealed")]
    [InlineData("no-medals")]
    [InlineData("date")]
    [InlineData("conditional")]
    [InlineData("partner")]
    [InlineData("bonus")]
    [InlineData("empty-recipe")]
    public async Task UnsupportedClaimsLeaveInventoryAndBalancesUntouched(string reason)
    {
        using var rig = await TitleTestRig.CreateAsync(true);
        switch (reason)
        {
            case "phase": rig.Session.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Room); break;
            case "concealed": rig.CatalogData.Definitions[0][3] = "1"; break;
            case "no-medals": rig.CatalogData.Definitions[0][5] = "0"; break;
            case "date": rig.CatalogData.Definitions[0][4] = "2012/03/28/16:35~2012/03/28/16:50"; break;
            case "conditional": rig.CatalogData.Definitions[0][2] = "3"; break;
            case "partner": rig.CatalogData.Definitions[0][2] = "11"; break;
            case "bonus": rig.CatalogData.Bonuses.Add(["400001", "0", "30007"]); break;
            case "empty-recipe": rig.CatalogData.Requirements.RemoveAll(row => row[0] == "400001"); break;
        }
        var before = await rig.BagAsync();
        var medals = await rig.Store.ReadAsync(1, rig.Scope.Key, default);
        using var runtime = rig.Runtime();
        await runtime.HandleMakeAsync(rig.Session, new(reason == "owner" ? 2 : 1, reason == "unknown" ? 999 : 400001), default);

        Assert.Equal(reason == "date" ? -10 : -1, Assert.Single(rig.Made).Result);
        Assert.Empty(rig.Medals.Claims);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Balances);
        Assert.Equal(before, await rig.BagAsync());
        Assert.Equal(medals, await rig.Store.ReadAsync(1, rig.Scope.Key, default));
    }

    /// <summary>A configured native acquisition window admits a current local date while preserving ordinary inventory-before-result ordering.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAcquisitionWindowAdmitsCurrentAnnualDate(bool modern)
    {
        using var rig = await TitleTestRig.CreateAsync(modern);
        rig.CatalogData.Definitions[0][4] = "2012/11/10/12:00~2012/11/10/12:01";
        rig.Options.AcquisitionTimeZone = "UTC";
        using var runtime = rig.Runtime();
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);
        Assert.Equal(0, Assert.Single(rig.Made).Result);
        Assert.Equal(["inventory", "medals", "make"], rig.Order);
    }

    /// <summary>A provider without durable acquisition receipts rejects bonus titles before any inventory or medal access.</summary>
    [Fact]
    public async Task UnsupportedBonusProviderRefusesBeforeStorageAccess()
    {
        using var rig = await TitleTestRig.CreateAsync();
        rig.CatalogData.Bonuses.Add(["400001", "0", "30007"]);
        rig.Options.BonusMedalQuantity = 1;
        rig.Medals.SupportsAcquisitionBonuses = false;
        using var runtime = rig.Runtime();
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);
        Assert.Equal(-1, Assert.Single(rig.Made).Result);
        Assert.Empty(rig.Medals.Claims);
        Assert.Empty(rig.Medals.ReadOwners);
        Assert.Equal(0, rig.Inventory.Reads);
        Assert.Empty(rig.Inventories);
    }

    /// <summary>An unaffordable recipe or active ownership rejects all costs together.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsufficientMedalsAndOwnedTitlesCannotPartiallyDebit(bool owned)
    {
        using var rig = await TitleTestRig.CreateAsync();
        if (owned) await rig.AddTitleAsync();
        else rig.CatalogData.Requirements[1][2] = "21";
        var before = await rig.BagAsync();
        var medals = await rig.Store.ReadAsync(1, rig.Scope.Key, default);
        using var runtime = rig.Runtime();
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);

        Assert.Equal(-1, Assert.Single(rig.Made).Result);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Balances);
        Assert.Equal(before, await rig.BagAsync());
        Assert.Equal(medals, await rig.Store.ReadAsync(1, rig.Scope.Key, default));
    }

    /// <summary>The exact preflight row prevents a competing grant from triggering a second charge.</summary>
    [Fact]
    public async Task ACompetingOwnershipChangeRefusesTheStaleClaim()
    {
        using var rig = await TitleTestRig.CreateAsync();
        rig.Medals.BeforeClaim = async (_, _) => await rig.AddTitleAsync();
        using var runtime = rig.Runtime();
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);

        Assert.Null(Assert.Single(rig.Medals.Claims).ExpectedTitle);
        Assert.Equal(-1, Assert.Single(rig.Made).Result);
        Assert.Empty(rig.Inventories);
        Assert.Equal(30, (await rig.Store.ReadAsync(1, rig.Scope.Key, default))[0].Count);
        Assert.Single(await rig.BagAsync(), row => row.ItemId == 400901);
    }

    /// <summary>Late ordinary and pet metadata is initialized within the same successful title transaction.</summary>
    [Fact]
    public async Task ClaimPublishesCompleteInitializedInventoryWithoutASecondMutation()
    {
        using var rig = await TitleTestRig.CreateAsync();
        using var runtime = rig.Runtime(new InitializationOnlyPets(rig.Scope), new InitializationOnlyArticles(rig.Scope));
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);

        var command = Assert.Single(rig.Medals.Claims);
        Assert.Equal(1, command.ArticleInitialization!.UserNum);
        Assert.Equal(1, command.PetInitialization!.UserNum);
        Assert.Equal(TitleTestRig.Now, command.Now);
        var snapshot = Assert.Single(rig.Inventories);
        Assert.NotNull(snapshot.CharacterBag.Single(row => row.ItemId == 400901).ArticleIdentity);
        foreach (var bag in new[] { snapshot.CharacterBag, snapshot.EquippedBag })
        {
            Assert.NotNull(bag.Single(row => row.ItemId == 300100).ArticleIdentity);
            Assert.NotNull(bag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.NotNull(bag.Single(row => row.ItemId == 301001).Pet!.Find(rig.Scope.Key));
        }
        Assert.Equal(snapshot.CharacterBag, await rig.BagAsync());
        Assert.Equal(["inventory", "medals", "make"], rig.Order);
    }

    /// <summary>Any failed delivery after commit preserves the single debit and cannot acknowledge an unsuccessful replay.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("medals")]
    [InlineData("make")]
    public async Task FailedPublicationCannotRepeatTheCommittedClaim(string stage)
    {
        using var rig = await TitleTestRig.CreateAsync();
        rig.FailAt = stage;
        using var runtime = rig.Runtime();
        await Assert.ThrowsAsync<IOException>(() => runtime.HandleMakeAsync(rig.Session, new(1, 400001), default).AsTask());
        Assert.Empty(rig.Made);
        Assert.Single(await rig.BagAsync(), row => row.ItemId == 400901);
        var afterCommit = await rig.Store.ReadAsync(1, rig.Scope.Key, default);
        Assert.Equal(10, afterCommit[0].Count);

        rig.FailAt = null;
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);
        Assert.Equal(-1, Assert.Single(rig.Made).Result);
        Assert.Equal(afterCommit, await rig.Store.ReadAsync(1, rig.Scope.Key, default));
    }

    /// <summary>A durable title claim keeps its rewards and costs when its original main-menu scene ends before publication.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedClaimCannotPublishIntoAnotherScene(bool returnToMenu)
    {
        using var rig = await TitleTestRig.CreateAsync();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Medals.AfterClaim = async (result, ct) =>
        {
            Assert.NotNull(result);
            committed.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        using var runtime = rig.Runtime();
        var pending = runtime.HandleMakeAsync(rig.Session, new(1, 400001), default).AsTask();
        IReadOnlyList<TitleMedalCount>? savedMedals = null;
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(await rig.BagAsync(), row => row.ItemId == 400901);
            savedMedals = await rig.Store.ReadAsync(1, rig.Scope.Key, default);
            Assert.Equal(10, savedMedals.Single(row => row.MedalId == 30007).Count);
            ChangeClaimScene(rig, returnToMenu);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Empty(rig.Order);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Balances);
        Assert.Empty(rig.Made);
        Assert.Single(await rig.BagAsync(), row => row.ItemId == 400901);
        Assert.Equal(savedMedals, await rig.Store.ReadAsync(1, rig.Scope.Key, default));
        Assert.Null(await rig.Store.TryClaimAsync(Assert.Single(rig.Medals.Claims), default));
        Assert.Equal(savedMedals, await rig.Store.ReadAsync(1, rig.Scope.Key, default));
    }

    /// <summary>Scene or generation changes after either published snapshot stop later claim frames without changing the durable grant.</summary>
    [Theory]
    [InlineData("inventory", 1, false)]
    [InlineData("inventory", 1, true)]
    [InlineData("medals", 2, false)]
    [InlineData("medals", 2, true)]
    public async Task SceneChangeDuringClaimPublicationSuppressesRemainingFrames(string stage, int published, bool returnToMenu)
    {
        using var rig = await TitleTestRig.CreateAsync();
        rig.AfterPublication = current =>
        {
            if (current == stage) ChangeClaimScene(rig, returnToMenu);
        };
        using var runtime = rig.Runtime();
        await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);
        Assert.Equal(new[] { "inventory", "medals" }.Take(published), rig.Order);
        Assert.Single(rig.Inventories);
        Assert.Equal(published - 1, rig.Balances.Count);
        Assert.Empty(rig.Made);
        Assert.Single(await rig.BagAsync(), row => row.ItemId == 400901);
        var savedMedals = await rig.Store.ReadAsync(1, rig.Scope.Key, default);
        Assert.Equal(10, savedMedals.Single(row => row.MedalId == 30007).Count);
        Assert.Null(await rig.Store.TryClaimAsync(Assert.Single(rig.Medals.Claims), default));
        Assert.Equal(savedMedals, await rig.Store.ReadAsync(1, rig.Scope.Key, default));
    }

    private static void ChangeClaimScene(TitleTestRig rig, bool returnToMenu)
    {
        var player = rig.Session.Features.Get<IPlayerSession>();
        player.MoveTo(SessionPhase.Room);
        if (!returnToMenu) return;
        player.MoveTo(SessionPhase.Plaza);
        player.MoveTo(SessionPhase.MainMenu);
    }

    /// <summary>The account remains guarded through publication, including concurrent equipment and duplicate make requests.</summary>
    [Fact]
    public async Task ConcurrentRequestsCannotInterleaveWhileClaimPublicationIsPending()
    {
        using var rig = await TitleTestRig.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeInventory = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var runtime = rig.Runtime();
        var pending = runtime.HandleMakeAsync(rig.Session, new(1, 400001), default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);
            await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);
            Assert.Equal(-1, Assert.Single(rig.Made).Result);
            Assert.Equal(0, Assert.Single(rig.Equipped).Result);
            Assert.Single(rig.Medals.Claims);
            Assert.Empty(rig.Selection.Calls);
        }
        finally { release.TrySetResult(); await pending; }
        Assert.Single(rig.Made, reply => reply.Result == 0);
        Assert.Equal(10, (await rig.Store.ReadAsync(1, rig.Scope.Key, default))[0].Count);
        await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);
        Assert.Single(rig.Equipped, reply => reply.Result == 1);
    }

    /// <summary>A full operation budget refuses another account immediately and releases capacity after publication.</summary>
    [Fact]
    public async Task CapacityIsBoundedAndReleasedForOtherAccounts()
    {
        using var rig = await TitleTestRig.CreateAsync();
        rig.Options.MaxInFlightAccounts = 1;
        await rig.SeedUserAsync(2);
        var second = new TitleTestSession(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeInventory = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var runtime = rig.Runtime();
        var pending = runtime.HandleMakeAsync(rig.Session, new(1, 400001), default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.HandleMakeAsync(second, new(2, 400001), default);
            Assert.Equal(-1, Assert.Single(rig.Made).Result);
            Assert.Single(rig.Medals.Claims);
            Assert.Equal(30, (await rig.Store.ReadAsync(2, rig.Scope.Key, default))[0].Count);
        }
        finally { release.TrySetResult(); await pending; }
        await runtime.HandleMakeAsync(second, new(2, 400001), default);
        Assert.Equal(2, rig.Made.Count(reply => reply.Result == 0));
        Assert.Equal(10, (await rig.Store.ReadAsync(2, rig.Scope.Key, default))[0].Count);
    }

    /// <summary>A scene round trip during the ownership read invalidates the operation even when the current phase is the menu.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedGenerationDuringOwnershipReadCannotCommit(bool equip)
    {
        using var rig = await TitleTestRig.CreateAsync();
        if (equip) await rig.AddTitleAsync();
        rig.Inventory.AfterRead = _ =>
        {
            var player = rig.Session.Features.Get<IPlayerSession>();
            player.MoveTo(SessionPhase.Plaza);
            player.MoveTo(SessionPhase.MainMenu);
            return ValueTask.CompletedTask;
        };
        using var runtime = rig.Runtime();
        if (equip) await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);
        else await runtime.HandleMakeAsync(rig.Session, new(1, 400001), default);

        Assert.Empty(rig.Medals.Claims);
        Assert.Empty(rig.Selection.Calls);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Balances);
        Assert.Equal(equip ? ["starting", "set", "settled"] : ["make"], rig.Order);
    }

    /// <summary>Equip and remove publish the exact committed title item without opening the medal window.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task EquipmentPublishesCommittedAppearanceBeforeTheNativeReply(int action, int result)
    {
        using var rig = await TitleTestRig.CreateAsync();
        await rig.AddTitleAsync(flags: action == 1 ? (byte)0x41 : (byte)0x40);
        await rig.AddTitleAsync(400909, flags: action == 0 ? (byte)0x41 : (byte)0x40);
        using var presentation = rig.Events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(async (request, next, ct) =>
            request.Slot.ItemId == 400901 ? new([new RoomItemAttribute(400901, 1, [7], 0, "")], null) : await next(ct));
        using var runtime = rig.Runtime();
        await runtime.HandleSetAsync(rig.Session, new(1, action, 400001), default);

        Assert.Equal(["starting", "committed", "inventory", "view", "set", "settled"], rig.Order);
        Assert.Equal((1L, 400901, action == 0), Assert.Single(rig.Selection.Calls));
        Assert.Equal(new TitleSetReplied(result, 400001), Assert.Single(rig.Equipped));
        Assert.Empty(rig.Balances);
        Assert.Empty(rig.Medals.Claims);
        var inventory = Assert.Single(rig.Inventories);
        Assert.True(inventory.IsCompleteSnapshot);
        Assert.Equal(inventory.CharacterBag, Assert.Single(rig.Views));
        Assert.Equal(inventory.CharacterBag, await rig.BagAsync());
        foreach (var bag in new[] { inventory.CharacterBag, inventory.EquippedBag })
        {
            Assert.Equal(action == 0 ? 0x41 : 0x40, bag.Single(row => row.ItemId == 400901).Flags);
            Assert.Equal(0x40, bag.Single(row => row.ItemId == 400909).Flags);
            Assert.Equal(0x40, bag.Single(row => row.ItemId == 400905).Flags);
        }
        var committed = Assert.Single(rig.Committed);
        Assert.Equal(300001, committed.CharacterId);
        Assert.Equal(action == 0, committed.EquippedItemIds.Contains(400901));
        Assert.NotNull(committed.ItemAttributes);
        Assert.Equal(action == 0 ? 1 : 0, committed.ItemAttributes.Count);
        Assert.Equal(30, (await rig.Store.ReadAsync(1, rig.Scope.Key, default))[0].Count);
    }

    /// <summary>Acquisition dates and conditional rewards do not prevent wearing an already owned ordinary title.</summary>
    [Theory]
    [InlineData("date")]
    [InlineData("conditional")]
    [InlineData("bonus")]
    public async Task AcquisitionOnlyConditionsDoNotBlockOwnedEquipment(string condition)
    {
        using var rig = await TitleTestRig.CreateAsync(true);
        if (condition == "date") rig.CatalogData.Definitions[0][4] = "2012/03/28/16:35~2012/03/28/16:50";
        if (condition == "conditional") rig.CatalogData.Definitions[0][2] = "3";
        if (condition == "bonus") rig.CatalogData.Bonuses.Add(["400001", "0", "30007"]);
        await rig.AddTitleAsync();
        using var runtime = rig.Runtime();
        await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);
        Assert.Equal(1, Assert.Single(rig.Equipped).Result);
        Assert.Empty(rig.Balances);
    }

    /// <summary>Foreign, malformed, unavailable and partner-specific equipment requests never reach a mutation.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("action")]
    [InlineData("unknown")]
    [InlineData("phase")]
    [InlineData("partner")]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("unspecified")]
    [InlineData("expired")]
    [InlineData("duplicate")]
    public async Task InvalidEquipmentRequestsCannotChangeWornState(string reason)
    {
        using var rig = await TitleTestRig.CreateAsync();
        if (reason != "absent")
            await rig.AddTitleAsync(quantity: reason == "empty" ? 0 : 1, lifetime: reason switch
            {
                "unspecified" => default(InventoryLifetime),
                "expired" => InventoryLifetime.Until(TitleTestRig.Now),
                _ => InventoryLifetime.Permanent,
            });
        if (reason == "partner") rig.CatalogData.Definitions[0][2] = "11";
        if (reason == "phase") rig.Session.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Room);
        if (reason == "duplicate")
        {
            var rows = await rig.BagAsync();
            await rig.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
                [.. rows, rows.Single(row => row.ItemId == 400901) with { SlotKey = 987654 }], default);
        }
        var before = await rig.BagAsync();
        using var runtime = rig.Runtime();
        await runtime.HandleSetAsync(rig.Session,
            new(reason == "owner" ? 2 : 1, reason == "action" ? 2 : 0, reason == "unknown" ? 999 : 400001), default);

        Assert.Equal(0, Assert.Single(rig.Equipped).Result);
        Assert.Empty(rig.Selection.Calls);
        Assert.Empty(rig.Committed);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Balances);
        Assert.Equal(before, await rig.BagAsync());
        Assert.Equal(rig.Order.Count(stage => stage == "starting"), rig.Order.Count(stage => stage == "settled"));
    }

    /// <summary>A store refusal settles the selection without publishing a successful appearance or reply.</summary>
    [Fact]
    public async Task RefusedSelectionDoesNotPublishSuccess()
    {
        using var rig = await TitleTestRig.CreateAsync();
        await rig.AddTitleAsync();
        rig.Selection.Refuse = true;
        using var runtime = rig.Runtime();
        await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);
        Assert.Equal(["starting", "set", "settled"], rig.Order);
        Assert.Equal(0, Assert.Single(rig.Equipped).Result);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Views);
    }

    /// <summary>A replacement or depleted title cannot inherit an equipment request for the previous ownership row.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionRetainsItsExactOwnershipPreconditionUntilCommit(bool depleted)
    {
        using var rig = await TitleTestRig.CreateAsync();
        await rig.AddTitleAsync();
        var expected = (await rig.BagAsync()).Single(row => row.ItemId == 400901);
        rig.Selection.BeforeApply = async ct =>
        {
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var rows = await rig.Store.ListSlotsAsync(1, bag, InventoryLimits.MaxInventorySlotsPerBag, ct);
                await rig.Store.ReplaceSlotsAsync(1, bag, rows.Select(row => row.ItemId != 400901 ? row
                    : depleted ? row with { Quantity = 0 } : row with { SlotKey = 987654 }).ToArray(), ct);
            }
        };
        using var runtime = rig.Runtime();
        await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);

        Assert.Equal(expected, Assert.Single(rig.Selection.Targets));
        Assert.Equal(0, Assert.Single(rig.Equipped).Result);
        Assert.Equal(["starting", "set", "settled"], rig.Order);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Committed);
        Assert.Equal(0, (await rig.BagAsync()).Single(row => row.ItemId == 400901).Flags & InventorySlotRecord.WornFlagBit);
    }

    /// <summary>Every started selection settles after publication failure and releases its account guard for a retry.</summary>
    [Theory]
    [InlineData("starting")]
    [InlineData("committed")]
    [InlineData("inventory")]
    [InlineData("view")]
    [InlineData("set")]
    public async Task SelectionFailuresAlwaysSettleAndReleaseTheAccount(string stage)
    {
        using var rig = await TitleTestRig.CreateAsync();
        await rig.AddTitleAsync();
        rig.FailAt = stage;
        using var runtime = rig.Runtime();
        await Assert.ThrowsAsync<IOException>(() => runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default).AsTask());
        Assert.Equal("settled", rig.Order[^1]);
        Assert.Empty(rig.Equipped);
        Assert.Equal(stage != "starting", ((await rig.BagAsync()).Single(row => row.ItemId == 400901).Flags & InventorySlotRecord.WornFlagBit) != 0);
        rig.FailAt = null;
        await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);
        Assert.Equal(1, Assert.Single(rig.Equipped).Result);
        Assert.Equal(2, rig.Order.Count(value => value == "settled"));
    }

    /// <summary>Request cancellation cannot cancel the mandatory settled notification or retain the account guard.</summary>
    [Fact]
    public async Task CancellationDuringStartingStillSettlesWithoutMutation()
    {
        using var rig = await TitleTestRig.CreateAsync();
        await rig.AddTitleAsync();
        using var cancellation = new CancellationTokenSource();
        var subscription = rig.Events.OnAsync<EquipmentSelectionStarting>((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellation.Token);
        });
        using var runtime = rig.Runtime();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), cancellation.Token).AsTask());
        }
        finally { subscription.Dispose(); }
        Assert.Equal(["starting", "settled"], rig.Order);
        Assert.Empty(rig.Selection.Calls);
        await runtime.HandleSetAsync(rig.Session, new(1, 0, 400001), default);
        Assert.Equal(1, Assert.Single(rig.Equipped).Result);
    }

    /// <summary>Positive selected-player queries read that player's catalog map and suppress unknown medal definitions.</summary>
    [Fact]
    public async Task MedalQueryReadsRemotePlayerWithoutCreatingOwnership()
    {
        using var rig = await TitleTestRig.CreateAsync();
        await rig.SeedUserAsync(2);
        await rig.Store.GrantAsync(new(2, rig.Scope.Key, "tests", "remote", [new(30007, 7)]), default);
        using var runtime = rig.Runtime();
        await runtime.HandleMedalsAsync(rig.Session, new(2), default);
        Assert.Equal([2L], rig.Medals.ReadOwners);
        Assert.Equal(1, Assert.Single(rig.Balances).Result);
        Assert.Equal(new[] { new TitleMedalCount(30007, 37), new TitleMedalCount(30008, 20) }, rig.Balances[0].Medals);
        Assert.Empty(rig.Made);
        Assert.Empty(rig.Inventories);
        Assert.Empty(rig.Medals.Claims);
    }

    /// <summary>Invalid targets and migrating sessions cannot read another account's medals.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(2, true)]
    public async Task IneligibleMedalQueriesReturnTheNativeFailureWithoutStorageRead(int target, bool migrating)
    {
        using var rig = await TitleTestRig.CreateAsync();
        if (migrating) rig.Session.Features.Get<IPlayerSession>().MoveTo(SessionPhase.Migrating);
        using var runtime = rig.Runtime();
        await runtime.HandleMedalsAsync(rig.Session, new(target), default);
        Assert.Equal(2, Assert.Single(rig.Balances).Result);
        Assert.Empty(rig.Balances[0].Medals);
        Assert.Empty(rig.Medals.ReadOwners);
    }

    /// <summary>Missing authentication is an explicit routing error for every title operation.</summary>
    [Fact]
    public async Task AnonymousSessionsCannotUseTitleOperations()
    {
        using var rig = await TitleTestRig.CreateAsync();
        using var runtime = rig.Runtime();
        var session = new TitleTestSession(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.HandleMakeAsync(session, new(1, 400001), default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.HandleSetAsync(session, new(1, 0, 400001), default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.HandleMedalsAsync(session, new(1), default).AsTask());
        Assert.Empty(rig.Order);
    }

    private sealed class InitializationOnlyArticles(InventoryLoadoutScope scope) : IEquipmentArticleResolver
    {
        /// <inheritdoc />
        public ArticleInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) =>
            new(userNum, scope, [300100, 400901], now);
        /// <inheritdoc />
        public ValueTask<InventorySlotRecord?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class InitializationOnlyPets(InventoryLoadoutScope scope) : IPetEntitlements
    {
        /// <inheritdoc />
        public string BuildKey => scope.Key;
        /// <inheritdoc />
        public bool IsPetItem(int itemId) => itemId == 301001;
        /// <inheritdoc />
        public PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) =>
            new(userNum, scope, [301001], new(0, 0, now, null, null, null), now);
        /// <inheritdoc />
        public ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct) => throw new NotSupportedException();
        /// <inheritdoc />
        public ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct) => throw new NotSupportedException();
        /// <inheritdoc />
        public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) => throw new NotSupportedException();
        /// <inheritdoc />
        public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) => throw new NotSupportedException();
    }
}
