using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Lobby;
using System.Buffers.Binary;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

public sealed class EquipmentSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyDenialLeavesInventoryUnchangedForServiceAndClientRequests(bool clientRequest)
    {
        using var h = await Harness.CreateAsync();
        var before = await h.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        var calls = 0;
        using var policy = h.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>((e, _, _) =>
        {
            Assert.Equal("kr-1256/game", e.Protocol);
            Assert.Equal(42, e.UserNum);
            calls++;
            return ValueTask.FromResult(new EquipmentSelectionDecision(false, e.ItemId, e.Worn, "test restriction"));
        });
        if (clientRequest) await h.WearAsync(203073);
        else Assert.Null(await h.Selection.ApplyAsync(42, 203073, true, default));
        Assert.Equal(1, calls);
        Assert.Equal(before, await h.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
    }

    [Fact]
    public async Task PolicyRetargetUsesTheAtomicSelectionAndIsIdempotent()
    {
        using var h = await Harness.CreateAsync();
        using var policy = h.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(async (_, next, ct) =>
            (await next(ct)) with { ItemId = 300027 });
        var first = await h.Selection.ApplyAsync(42, 203073, true, default);
        var second = await h.Selection.ApplyAsync(42, 203073, true, default);
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Contains(300027, Worn(first));
        Assert.DoesNotContain(300001, Worn(first));
        Assert.DoesNotContain(203073, Worn(first));
    }

    [Fact]
    public async Task InvalidPolicyDecisionFailsBeforeWriting()
    {
        using var h = await Harness.CreateAsync();
        var before = await h.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        using var policy = h.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(
            (_, _, _) => ValueTask.FromResult(new EquipmentSelectionDecision(true, 0, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Selection.ApplyAsync(42, 203073, true, default).AsTask());
        Assert.Equal(before, await h.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
    }

    [Fact]
    public async Task CancellationAfterPolicyAndRemovingPolicyRespectTheServiceBoundary()
    {
        using var h = await Harness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var before = await h.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        using (h.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>((e, _, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(new EquipmentSelectionDecision(true, e.ItemId, e.Worn));
        }))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Selection.ApplyAsync(42, 203073, true, cancellation.Token).AsTask());
        Assert.Equal(before, await h.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
        Assert.Contains(203073, Worn((await h.Selection.ApplyAsync(42, 203073, true, default))!));
    }

    [Fact]
    [Trait("Category", "LocalClientData")]
    public async Task MismatchedNativeCategoriesFailInsteadOfPublishingEmptyWearableRules()
    {
        var error = await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(
            LocalClientData.Extract("cn-279", "Config/ShopItem2.xml")));
        Assert.Contains("no wearable positions", error.Message);
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("id-1809")]
    [Trait("Category", "LocalClientData")]
    public async Task NativeWearablePartsReplaceOverlappingSuitAndPreserveOtherFlags(string build)
    {
        using var h = await Harness.CreateAsync(LocalClientData.Extract(build, "Config/ShopItem2.xml"),
            build == "cn-279" ? "\uC758 \uC0C1" : "Clothes",
            build == "cn-279" ? "\uC7A5 \uC2DD" : "Accessories");
        await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
            new[] { 100003, 100004, 100007, 100011 }
                .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0x80)).ToArray(), default);
        await h.Selection.ApplyAsync(42, 100003, true, default);
        await h.Selection.ApplyAsync(42, 100004, true, default);
        await h.Selection.ApplyAsync(42, 100007, true, default);
        var separate = await h.Selection.ReadAsync(42, default);
        Assert.All(new[] { 100004, 100007 }, id => Assert.Contains(id, Worn(separate)));
        Assert.DoesNotContain(100003, Worn(separate));
        var replacement = build == "cn-279" ? 100003 : 100011;
        await h.Selection.ApplyAsync(42, replacement, true, default);
        var suit = await h.Selection.ReadAsync(42, default);
        Assert.Contains(replacement, Worn(suit));
        Assert.DoesNotContain(100004, Worn(suit));
        if (build == "cn-279") Assert.Contains(100007, Worn(suit));
        else Assert.DoesNotContain(100007, Worn(suit));
        Assert.All(suit.Where(row => row.ItemId is 100003 or 100004 or 100007 or 100011),
            row => Assert.Equal(0x80, row.Flags & 0x80));
        Assert.Equal(suit, await h.Selection.ReadAsync(42, default));
    }

    [Theory]
    [InlineData("cn-406")]
    [InlineData("id-1809")]
    [Trait("Category", "LocalClientData")]
    public async Task NativeAccessorySlotsReplaceMatchingSlotAndKeepOtherSlots(string build)
    {
        using var h = await Harness.CreateAsync(LocalClientData.Extract(build, "Config/ShopItem2.xml"));
        await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
            new[] { 100046, 100111, 100141, 100144 }
                .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0x80)).ToArray(), default);
        await h.Selection.ApplyAsync(42, 100046, true, default);
        await h.Selection.ApplyAsync(42, 100141, true, default);
        await h.Selection.ApplyAsync(42, 100144, true, default);
        await h.Selection.ApplyAsync(42, 100111, true, default);
        var selected = await h.Selection.ReadAsync(42, default);
        Assert.DoesNotContain(100046, Worn(selected));
        Assert.All(new[] { 100111, 100141, 100144 }, id => Assert.Contains(id, Worn(selected)));
        await h.Selection.ApplyAsync(42, 100111, false, default);
        var removed = await h.Selection.ReadAsync(42, default);
        Assert.DoesNotContain(100111, Worn(removed));
        Assert.DoesNotContain(100046, Worn(removed));
        Assert.All(new[] { 100141, 100144 }, id => Assert.Contains(id, Worn(removed)));
        Assert.All(removed.Where(row => row.ItemId is 100046 or 100111 or 100141 or 100144),
            row => Assert.Equal(0x80, row.Flags & 0x80));
        Assert.Equal(removed, await h.Selection.ReadAsync(42, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyFormattedTailDoesNotChangeWearablePositions(bool populatedTail)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        var tail = populatedTail ? "<Data ss:Type=\"String\">unexpected</Data>" : "";
        await File.WriteAllTextAsync(path, """
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
            <Worksheet ss:Name="ShopItem"><Table>
            <Row><Cell><Data>itemId</Data></Cell><Cell><Data>mainCategory</Data></Cell><Cell><Data>baseLocation</Data></Cell><Cell><Data>slotLocation</Data></Cell><Cell><Data>partLocation</Data></Cell><Cell><Data>secPartLocation</Data></Cell></Row>
            <Row><Cell><Data>100510</Data></Cell><Cell><Data>Clothes</Data></Cell><Cell><Data>Head</Data></Cell><Cell ss:Index="16384">
            """ + tail + "</Cell></Row></Table></Worksheet></Workbook>");
        try
        {
            if (populatedTail)
                await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(path));
            else
            {
                using var h = await Harness.CreateAsync(path);
                Assert.NotNull(h.Selection);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "LocalClientData")]
    public async Task ClientBodyPartsReplaceDuplicateClothesAndKeepDifferentAccessorySlots()
    {
        using var h = await Harness.CreateAsync(LocalClientData.Extract("cn-324", "Config/ShopItem2.xml"));
        await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
            new[] { 100510, 100530, 100511, 100531, 100111, 100141, 100144 }
                .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0x81)).ToArray(), default);
        var normalized = await h.Selection.ReadAsync(42, default);
        Assert.DoesNotContain(100510, Worn(normalized));
        Assert.DoesNotContain(100511, Worn(normalized));
        Assert.Contains(100530, Worn(normalized));
        Assert.Contains(100531, Worn(normalized));
        Assert.All(new[] { 100111, 100141, 100144 }, id => Assert.Contains(id, Worn(normalized)));
        await h.Selection.ApplyAsync(42, 100510, true, default);
        var selected = await h.Selection.ReadAsync(42, default);
        Assert.Contains(100510, Worn(selected));
        Assert.DoesNotContain(100530, Worn(selected));
        await h.Selection.ApplyAsync(42, 100510, false, default);
        var removed = await h.Selection.ReadAsync(42, default);
        Assert.DoesNotContain(100510, Worn(removed));
        Assert.DoesNotContain(100530, Worn(removed));
        Assert.Equal(normalized.Count, removed.Count);
        Assert.All(removed.Where(row => row.ItemId is >= 100000 and < 200000), row => Assert.Equal(0x80, row.Flags & 0x80));
    }

    [Fact]
    public async Task LegacyAllWornSnapshotRepairsWithoutLosingOwnedItemsOrOtherFlags()
    {
        using var h = await Harness.CreateAsync();
        var old = StarterInventory.Slots(42, InventoryBag.CharacterBag, DateTimeOffset.UtcNow).Select(s => s with { Flags = 0x80 }).ToArray();
        await h.Store.ReplaceSlotsAsync(42, InventoryBag.CharacterBag, old, default);
        await h.Store.ReplaceSlotsAsync(42, InventoryBag.EquippedBag, old.Select(s => s with { Bag = InventoryBag.EquippedBag, Flags = 0x81 }).ToArray(), default);
        var repaired = await h.Selection.ReadAsync(42, default);
        Assert.Equal(old.Length, repaired.Count);
        Assert.Equal(new[] { 201001, 202001, 205001, 207001, 300001 }, Worn(repaired).Order());
        Assert.All(repaired, s => Assert.Equal(0x80, s.Flags & 0x80));
        Assert.Equal(repaired, await h.Selection.ReadAsync(42, default));
        var legacy = await h.Store.ListSlotsAsync(42, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        Assert.Equal(Worn(repaired), Worn(legacy));
    }

    [Theory]
    [InlineData(201003, 201001)]
    [InlineData(203073, 202001)]
    [InlineData(204003, 202001)]
    [InlineData(300027, 300001)]
    public async Task RealPluginEquipReplacesThePositionAndRefreshesTheView(int selected, int displaced)
    {
        using var h = await Harness.CreateAsync();
        await h.WearAsync(selected);
        Assert.Equal(new ushort[] { 811, 803, 836, 837, 806 }, h.Session.Sent.Select(p => p.Opcode));
        var rows = await h.Selection.ReadAsync(42, default);
        Assert.Contains(selected, Worn(rows));
        Assert.DoesNotContain(displaced, Worn(rows));
        Assert.Equal(Worn(rows).Order(), ReadFlags(h.Session.Sent[1].Payload).Order());
        var view = h.Session.Sent[4].Payload;
        Assert.Equal(selected >= 300000 ? selected : 300001, BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(4)));
        var count = BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(8));
        var ids = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(12 + 4 * i))).ToArray();
        Assert.Contains(selected, ids);
        Assert.DoesNotContain(displaced, ids);
        // The preset contains primary and special, not the first two arbitrary equipped ids.
        var preset = h.Session.Sent[3].Payload;
        Assert.Equal(selected is >= 202000 and < 205000 ? selected : 202001, BinaryPrimitives.ReadInt32LittleEndian(preset.AsSpan(9)));
        Assert.Equal(205001, BinaryPrimitives.ReadInt32LittleEndian(preset.AsSpan(13)));
        var push = h.Push();
        var relog = new RecordingSession(55, 42);
        await push.PushInventoryAsync(relog, 42, default);
        Assert.Equal(Worn(rows).Order(), ReadFlags(relog.Sent[1].Payload).Order());
    }

    [Fact]
    public async Task SelectionReplyUsesItsCommittedSnapshotWithoutReadingANewerSelection()
    {
        using var h = await Harness.CreateAsync();
        var committed = await h.Selection.ApplyAsync(42, 203073, true, default);
        Assert.NotNull(committed);
        await h.Selection.ApplyAsync(42, 204003, true, default);
        await h.View.SendSelectionAsync(h.Session, 42, 300001, Worn(committed), default);
        Assert.Equal(new ushort[] { 836, 837, 806 }, h.Session.Sent.Select(p => p.Opcode));
        Assert.Equal(203073, BinaryPrimitives.ReadInt32LittleEndian(h.Session.Sent[1].Payload.AsSpan(9)));
        var view = h.Session.Sent[2].Payload;
        var count = BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(8));
        var ids = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(12 + 4 * i))).ToArray();
        Assert.Contains(203073, ids);
        Assert.DoesNotContain(204003, ids);
    }

    [Fact]
    public async Task RoomEntryUsesStoredSelectionAndClosesWithTheEquippedView()
    {
        // Waiting-room entry closes with 836, 837, 806.
        using var h = await Harness.CreateAsync();
        await h.Selection.ApplyAsync(42, 203073, true, default);
        await h.Selection.ApplyAsync(42, 300027, true, default);
        // Entry callers can still hold the starter snapshot; the persisted selection wins.
        await h.View.SendAsync(h.Session, 42, 300001, [300001, 202001, 205001], default);
        Assert.Equal(new ushort[] { 836, 837, 806 }, h.Session.Sent.Select(p => p.Opcode));
        var preset = h.Session.Sent[1].Payload;
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(preset.AsSpan(1)));
        Assert.Equal(203073, BinaryPrimitives.ReadInt32LittleEndian(preset.AsSpan(9)));
        Assert.Equal(205001, BinaryPrimitives.ReadInt32LittleEndian(preset.AsSpan(13)));
        var view = h.Session.Sent[2].Payload;
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(view));
        Assert.Equal(300027, BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(4)));
        var count = BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(8));
        var ids = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(view.AsSpan(12 + 4 * i))).ToArray();
        Assert.Contains(203073, ids);
        Assert.Contains(300027, ids);
        Assert.DoesNotContain(202001, ids);
        Assert.DoesNotContain(300001, ids);
    }

    [Fact]
    public async Task RemovedGrenadeStaysAbsentAfterNormalizationAndInventoryPush()
    {
        using var h = await Harness.CreateAsync();
        await h.Selection.ReadAsync(42, default);
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(805, BitConverter.GetBytes(205001L)), default);
        Assert.DoesNotContain(205001, Worn(await h.Selection.ReadAsync(42, default)));
        var push = h.Push();
        var relog = new RecordingSession(55, 42);
        await push.PushInventoryAsync(relog, 42, default);
        Assert.DoesNotContain(205001, ReadFlags(relog.Sent[1].Payload));
    }

    [Theory]
    [InlineData(300001)]
    [InlineData(202001)]
    [InlineData(201001)]
    [InlineData(207001)]
    public async Task RequiredPositionReturnsToItsInitialItem(int itemId)
    {
        using var h = await Harness.CreateAsync();
        await h.Selection.ReadAsync(42, default);
        await h.Selection.ApplyAsync(42, itemId, false, default);
        Assert.Contains(itemId, Worn(await h.Selection.ReadAsync(42, default)));
    }

    [Fact]
    public async Task RemovingWeaponDoesNotReequipItFromTheLegacySnapshot()
    {
        using var h = await Harness.CreateAsync();
        await h.WearAsync(203073);
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(805, BitConverter.GetBytes(203073L)), default);
        var worn = Worn(await h.Selection.ReadAsync(42, default));
        Assert.DoesNotContain(203073, worn);
        Assert.Contains(202001, worn);
    }

    [Fact]
    public async Task UnownedItemAndCharacterRemovalDoNotMutateInventory()
    {
        using var h = await Harness.CreateAsync();
        var before = await h.Selection.ReadAsync(42, default);
        await h.WearAsync(204999);
        Assert.Single(h.Session.Sent);
        Assert.NotEqual(0, BinaryPrimitives.ReadInt32LittleEndian(h.Session.Sent[0].Payload));
        Assert.NotNull(await h.Selection.ApplyAsync(42, 300001, false, default));
        Assert.Equal(before, await h.Selection.ReadAsync(42, default));
    }

    [Fact]
    public async Task ConcurrentChangesLeaveOnePrimaryAndPreserveAnAppendedPurchase()
    {
        using var h = await Harness.CreateAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (Task)h.Selection.ApplyAsync(42, i % 2 == 0 ? 203073 : 204003, true, default).AsTask())
            .Append(h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
                [new(42, InventoryBag.CharacterBag, 999, 100123, 7, 0)], default).AsTask()));
        var rows = await h.Selection.ReadAsync(42, default);
        Assert.Single(rows, row => row.ItemId is >= 202000 and < 205000 && (row.Flags & 1) != 0);
        Assert.Contains(rows, row => row.ItemId == 100123);
    }

    private static int[] Worn(IReadOnlyList<InventorySlotRecord> rows) => rows.Where(s => (s.Flags & 1) != 0).Select(s => s.ItemId).ToArray();

    /// <summary>Writes the catalog-masks fixture pair: a ShopItem workbook with the four location columns and a FighterConfig name row.</summary>
    private static async Task<(string ShopItem, string FighterConfig)> WriteMaskFixturesAsync(string shopItemRows)
    {
        var shopItem = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        var fighterConfig = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        await File.WriteAllTextAsync(shopItem, """
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
            <Worksheet ss:Name="ShopItem"><Table>
            <Row><Cell><Data>itemId</Data></Cell><Cell><Data>mainCategory</Data></Cell><Cell><Data>baseLocation</Data></Cell><Cell><Data>slotLocation</Data></Cell><Cell><Data>partLocation</Data></Cell><Cell><Data>secPartLocation</Data></Cell><Cell><Data>allowChr</Data></Cell><Cell><Data>denyChr</Data></Cell></Row>
            """ + shopItemRows + """
            </Table></Worksheet>
            <Worksheet ss:Name="SlotLocation"><Table>
            <Row><Cell><Data>name</Data></Cell><Cell><Data>id</Data></Cell></Row>
            <Row><Cell><Data>sWeapon1</Data></Cell><Cell><Data>1</Data></Cell></Row>
            <Row><Cell><Data>sWeapon2</Data></Cell><Cell><Data>2</Data></Cell></Row>
            <Row><Cell><Data>sWeapon4</Data></Cell><Cell><Data>3</Data></Cell></Row>
            <Row><Cell><Data>sEye</Data></Cell><Cell><Data>4</Data></Cell></Row>
            <Row><Cell><Data>sMask</Data></Cell><Cell><Data>5</Data></Cell></Row>
            <Row><Cell><Data>sNeck</Data></Cell><Cell><Data>6</Data></Cell></Row>
            <Row><Cell><Data>sTail</Data></Cell><Cell><Data>7</Data></Cell></Row>
            <Row><Cell><Data>sTitle</Data></Cell><Cell><Data>8</Data></Cell></Row>
            <Row><Cell><Data>sTail2</Data></Cell><Cell><Data>9</Data></Cell></Row>
            <Row><Cell><Data>sHeadBC</Data></Cell><Cell><Data>10</Data></Cell></Row>
            <Row><Cell><Data>sWristL</Data></Cell><Cell><Data>11</Data></Cell></Row>
            </Table></Worksheet>
            <Worksheet ss:Name="PartLocation"><Table>
            <Row><Cell><Data>name</Data></Cell><Cell><Data>id</Data></Cell></Row>
            <Row><Cell><Data>Feet</Data></Cell><Cell><Data>1</Data></Cell></Row>
            <Row><Cell><Data>BodyU</Data></Cell><Cell><Data>2</Data></Cell></Row>
            <Row><Cell><Data>ArmL</Data></Cell><Cell><Data>3</Data></Cell></Row>
            <Row><Cell><Data>ArmR</Data></Cell><Cell><Data>4</Data></Cell></Row>
            <Row><Cell><Data>BodyD</Data></Cell><Cell><Data>5</Data></Cell></Row>
            <Row><Cell><Data>Legs</Data></Cell><Cell><Data>6</Data></Cell></Row>
            <Row><Cell><Data>HandL</Data></Cell><Cell><Data>7</Data></Cell></Row>
            <Row><Cell><Data>HandR</Data></Cell><Cell><Data>8</Data></Cell></Row>
            <Row><Cell><Data>Head</Data></Cell><Cell><Data>9</Data></Cell></Row>
            </Table></Worksheet>
            </Workbook>
            """);
        await File.WriteAllTextAsync(fighterConfig, """
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
            <Worksheet ss:Name="FighterConfig"><Table>
            <Row><Cell><Data>[Field]</Data></Cell><Cell><Data>[Desc]</Data></Cell><Cell><Data>1</Data></Cell><Cell><Data>3</Data></Cell><Cell><Data>5</Data></Cell><Cell><Data>9</Data></Cell></Row>
            <Row><Cell><Data>name</Data></Cell><Cell><Data></Data></Cell><Cell><Data>Dao</Data></Cell><Cell><Data>Bazzi</Data></Cell><Cell><Data>Uni</Data></Cell><Cell><Data>PinkBean</Data></Cell></Row>
            </Table></Worksheet>
            </Workbook>
            """);
        return (shopItem, fighterConfig);
    }

    private const string MaskRows = """
        <Row><Cell><Data>201001</Data></Cell><Cell><Data>Weapons</Data></Cell><Cell><Data>sWeapon2</Data></Cell><Cell><Data>sWeapon2</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>202001</Data></Cell><Cell><Data>Weapons</Data></Cell><Cell><Data>sWeapon1</Data></Cell><Cell><Data>sWeapon1</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>203001</Data></Cell><Cell><Data>Weapons</Data></Cell><Cell><Data>sWeapon1</Data></Cell><Cell><Data>sWeapon1</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>205001</Data></Cell><Cell><Data>Weapons</Data></Cell><Cell><Data>sWeapon4</Data></Cell><Cell><Data>sWeapon4</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>300001</Data></Cell><Cell><Data>Characters</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>300009</Data></Cell><Cell><Data>Characters</Data></Cell><Cell><Data></Data></Cell><Cell><Data>all -sHeadBC -sTail2</Data></Cell><Cell><Data>all</Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>100001</Data></Cell><Cell><Data>Clothes</Data></Cell><Cell><Data>Feet</Data></Cell><Cell><Data></Data></Cell><Cell><Data>Feet</Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>100004</Data></Cell><Cell><Data>Clothes</Data></Cell><Cell><Data>BodyU</Data></Cell><Cell><Data></Data></Cell><Cell><Data>BodyU ArmL ArmR BodyD Legs Feet HandL HandR</Data></Cell><Cell><Data>BodyD Legs Feet HandL HandR</Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>100005</Data></Cell><Cell><Data>Clothes</Data></Cell><Cell><Data>BodyU</Data></Cell><Cell><Data></Data></Cell><Cell><Data>BodyU ArmL ArmR</Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>100050</Data></Cell><Cell><Data>Clothes</Data></Cell><Cell><Data>Head</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>100689</Data></Cell><Cell><Data>Clothes</Data></Cell><Cell><Data>Feet</Data></Cell><Cell><Data></Data></Cell><Cell><Data>Feet</Data></Cell><Cell><Data></Data></Cell><Cell><Data>Uni</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>210001</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data>sEye</Data></Cell><Cell><Data>sEye sMask</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>210002</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data>sMask</Data></Cell><Cell><Data>sMask sNeck</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>210003</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data>sTail</Data></Cell><Cell><Data>sTail</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>210004</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data>sTitle</Data></Cell><Cell><Data>sTitle</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>Dao</Data></Cell></Row>
        <Row><Cell><Data>100051</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data>sWristL</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>220000</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data></Data></Cell><Cell><Data>all -sHeadBC -sTail2</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>220001</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data>sHeadBC</Data></Cell><Cell><Data>sHeadBC</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        <Row><Cell><Data>220002</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data>sTail2</Data></Cell><Cell><Data>sTail2</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
        """;

    [Fact]
    public async Task CatalogMasksSlotSetsIntersectingDisplaceAndDisjointSlotsStay()
    {
        var (shopItem, fighterConfig) = await WriteMaskFixturesAsync(MaskRows);
        try
        {
            using var h = await Harness.CreateAsync(shopItem, selection: "catalog_masks", fighterConfigPath: fighterConfig);
            await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
                new[] { 210001, 210002, 210003, 210004, 100051 }
                    .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0)).ToArray(), default);
            // Two primaries share sWeapon1 (kr-1256 sub_A17CF0 reads slotLocation); the second displaces the first.
            await h.Selection.ApplyAsync(42, 203001, true, default);
            var primary = Worn(await h.Selection.ReadAsync(42, default));
            Assert.Contains(203001, primary);
            Assert.DoesNotContain(202001, primary);
            Assert.Contains(201001, primary);
            // Disjoint weapon slots stay side by side.
            Assert.Contains(205001, primary);
            // Accessories: sMask joins both lists, so the later one displaces the earlier; sTail and sWristL stay.
            await h.Selection.ApplyAsync(42, 210001, true, default);
            await h.Selection.ApplyAsync(42, 210002, true, default);
            await h.Selection.ApplyAsync(42, 210003, true, default);
            await h.Selection.ApplyAsync(42, 100051, true, default);
            var worn = Worn(await h.Selection.ReadAsync(42, default));
            Assert.Contains(210002, worn);
            Assert.DoesNotContain(210001, worn);
            Assert.All(new[] { 210003, 100051 }, id => Assert.Contains(id, worn));
            Assert.Equal(worn, Worn(await h.Selection.ReadAsync(42, default)));
        }
        finally { File.Delete(shopItem); File.Delete(fighterConfig); }
    }

    [Fact]
    public async Task CatalogMasksPartSetsIntersectingDisplaceAcrossPrimaryAndSecondaryParts()
    {
        var (shopItem, fighterConfig) = await WriteMaskFixturesAsync(MaskRows);
        try
        {
            using var h = await Harness.CreateAsync(shopItem, selection: "catalog_masks", fighterConfigPath: fighterConfig);
            await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
                new[] { 100001, 100004, 100005, 100050 }
                    .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0)).ToArray(), default);
            // Shoes and a shirt cloth disjoint parts and are worn together; the head part comes from baseLocation alone.
            await h.Selection.ApplyAsync(42, 100001, true, default);
            await h.Selection.ApplyAsync(42, 100005, true, default);
            await h.Selection.ApplyAsync(42, 100050, true, default);
            var separate = Worn(await h.Selection.ReadAsync(42, default));
            Assert.All(new[] { 100001, 100005, 100050 }, id => Assert.Contains(id, separate));
            // The penguin costume shares Feet with the shoes and BodyU with the shirt; both give way.
            await h.Selection.ApplyAsync(42, 100004, true, default);
            var costume = Worn(await h.Selection.ReadAsync(42, default));
            Assert.Contains(100004, costume);
            Assert.DoesNotContain(100001, costume);
            Assert.DoesNotContain(100005, costume);
            Assert.Contains(100050, costume);
        }
        finally { File.Delete(shopItem); File.Delete(fighterConfig); }
    }

    [Fact]
    public async Task CatalogMasksRefuseCharacterExclusiveWearAndAcceptItForTheAllowedCharacter()
    {
        var (shopItem, fighterConfig) = await WriteMaskFixturesAsync(MaskRows);
        try
        {
            using var h = await Harness.CreateAsync(shopItem, selection: "catalog_masks", fighterConfigPath: fighterConfig);
            await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
                new[] { 100689, 210004, 300003, 300005 }
                    .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0)).ToArray(), default);
            var before = await h.Selection.ReadAsync(42, default);
            // allowChr "Uni LXUni" excludes the worn Dao; denyChr "Dao" refuses a title for the same character.
            Assert.Null(await h.Selection.ApplyAsync(42, 100689, true, default));
            Assert.Null(await h.Selection.ApplyAsync(42, 210004, true, default));
            Assert.Equal(before, await h.Selection.ReadAsync(42, default));
            // After the seat moves to Uni the same article is accepted; the title stays refused for Dao and is
            // accepted once the seat is Bazzi.
            await h.Selection.ApplyAsync(42, 300005, true, default);
            Assert.NotNull(await h.Selection.ApplyAsync(42, 100689, true, default));
            await h.Selection.ApplyAsync(42, 300003, true, default);
            Assert.NotNull(await h.Selection.ApplyAsync(42, 210004, true, default));
            var worn = Worn(await h.Selection.ReadAsync(42, default));
            Assert.All(new[] { 300003, 100689, 210004 }, id => Assert.Contains(id, worn));
            Assert.DoesNotContain(300005, worn);
        }
        finally { File.Delete(shopItem); File.Delete(fighterConfig); }
    }

    [Fact]
    public async Task CatalogMasksAllExpressionCoversTheVocabularyMinusExclusionsAndIgnoresSeatColumns()
    {
        var (shopItem, fighterConfig) = await WriteMaskFixturesAsync(MaskRows);
        try
        {
            using var h = await Harness.CreateAsync(shopItem, selection: "catalog_masks", fighterConfigPath: fighterConfig);
            await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
                new[] { 220000, 220001, 220002, 300009 }
                    .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0)).ToArray(), default);
            await h.Selection.ApplyAsync(42, 220000, true, default);
            var all = Worn(await h.Selection.ReadAsync(42, default));
            // "all -sHeadBC -sTail2" holds every vocabulary slot except the two named ones: the worn primary and
            // special weapons give way, and the excluded positions still accept their own articles afterwards.
            Assert.Contains(220000, all);
            Assert.DoesNotContain(202001, all);
            Assert.DoesNotContain(205001, all);
            await h.Selection.ApplyAsync(42, 220001, true, default);
            await h.Selection.ApplyAsync(42, 220002, true, default);
            var exceptions = Worn(await h.Selection.ReadAsync(42, default));
            Assert.All(new[] { 220000, 220001, 220002 }, id => Assert.Contains(id, exceptions));
            // The seat row's own all-expression (300009, a Characters row) describes the model it replaces, not
            // bag conflicts: wearing that character leaves every other worn position untouched.
            await h.Selection.ApplyAsync(42, 300009, true, default);
            var seat = Worn(await h.Selection.ReadAsync(42, default));
            Assert.Contains(300009, seat);
            Assert.DoesNotContain(300001, seat);
            Assert.All(new[] { 220000, 220001, 220002, 207001 }, id => Assert.Contains(id, seat));
        }
        finally { File.Delete(shopItem); File.Delete(fighterConfig); }
    }

    [Theory]
    [InlineData(201001)]
    [InlineData(300001)]
    public async Task CatalogMasksRejectDuplicateUnrestrictedAndCharacterRows(int itemId)
    {
        var duplicate = $"<Row><Cell><Data>{itemId}</Data></Cell><Cell><Data>Weapons</Data></Cell><Cell><Data>sWeapon2</Data></Cell><Cell><Data>sWeapon2</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>";
        var files = await WriteMaskFixturesAsync(MaskRows + duplicate);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(files.ShopItem,
                selection: "catalog_masks", fighterConfigPath: files.FighterConfig));
            Assert.Contains($"repeats itemId {itemId}", error.Message);
        }
        finally
        {
            File.Delete(files.ShopItem);
            File.Delete(files.FighterConfig);
        }
    }

    [Fact]
    public async Task CatalogMasksConfigurationFailuresAreLoud()
    {
        // selection catalog_masks without its workbooks fails validation at bind time.
        var missing = await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(selection: "catalog_masks"));
        Assert.Contains("catalog_masks requires exactly one of item_locations_client_path or item_locations_path", missing.Message);
        Assert.Contains("fighter_config_client_path or fighter_config_path", missing.Message);
        // A shop workbook without the fighter names fails the same way, whatever file the item path names.
        var dummy = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        await File.WriteAllTextAsync(dummy, "not a workbook");
        try
        {
            var noNames = await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(dummy, selection: "catalog_masks"));
            Assert.Contains("fighter_config_client_path or fighter_config_path", noNames.Message);
        }
        finally { File.Delete(dummy); }
        // A workbook without the vocabulary sheets, a subtraction without all, and an unknown allowChr name all
        // name their value instead of degrading to the id-range behaviour.
        var (shopItem, fighterConfig) = await WriteMaskFixturesAsync(MaskRows);
        try
        {
            var stripped = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
            await File.WriteAllTextAsync(stripped, """
                <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
                <Worksheet ss:Name="ShopItem"><Table>
                <Row><Cell><Data>itemId</Data></Cell><Cell><Data>baseLocation</Data></Cell><Cell><Data>slotLocation</Data></Cell><Cell><Data>partLocation</Data></Cell><Cell><Data>secPartLocation</Data></Cell><Cell><Data>allowChr</Data></Cell><Cell><Data>denyChr</Data></Cell></Row>
                <Row><Cell><Data>202001</Data></Cell><Cell><Data>sWeapon1</Data></Cell><Cell><Data>sWeapon1</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>
                </Table></Worksheet>
                </Workbook>
                """);
            var missingSheet = await Assert.ThrowsAsync<ConfigException>(
                () => Harness.CreateAsync(stripped, selection: "catalog_masks", fighterConfigPath: fighterConfig));
            Assert.Contains("SlotLocation", missingSheet.Message);
            File.Delete(stripped);
            var subtraction = await WriteMaskFixturesAsync(
                MaskRows + "<Row><Cell><Data>230000</Data></Cell><Cell><Data>Accessories</Data></Cell><Cell><Data></Data></Cell><Cell><Data>-sEye</Data></Cell><Cell><Data></Data></Cell><Cell><Data></Data></Cell><Cell><Data>all</Data></Cell><Cell><Data>none</Data></Cell></Row>\n");
            var withoutAll = await Assert.ThrowsAsync<ConfigException>(
                () => Harness.CreateAsync(subtraction.ShopItem, selection: "catalog_masks", fighterConfigPath: subtraction.FighterConfig));
            Assert.Contains("subtracts names without stating all", withoutAll.Message);
            File.Delete(subtraction.ShopItem);
            File.Delete(subtraction.FighterConfig);
            var unknown = await WriteMaskFixturesAsync(
                MaskRows + "<Row><Cell><Data>230001</Data></Cell><Cell><Data>Clothes</Data></Cell><Cell><Data>Feet</Data></Cell><Cell><Data></Data></Cell><Cell><Data>Feet</Data></Cell><Cell><Data></Data></Cell><Cell><Data>Dao UnknownHero</Data></Cell><Cell><Data>none</Data></Cell></Row>\n");
            var unresolved = await Assert.ThrowsAsync<ConfigException>(
                () => Harness.CreateAsync(unknown.ShopItem, selection: "catalog_masks", fighterConfigPath: unknown.FighterConfig));
            Assert.Contains("UnknownHero", unresolved.Message);
            Assert.Contains("FighterConfig", unresolved.Message);
            File.Delete(unknown.ShopItem);
            File.Delete(unknown.FighterConfig);
        }
        finally { File.Delete(shopItem); File.Delete(fighterConfig); }
    }

    [Fact]
    [Trait("Category", "LocalClientData")]
    public async Task Kr1256CatalogMasksReproduceTheNativeConflicts()
    {
        using var h = await Harness.CreateAsync(LocalClientData.Extract("kr-1256", "Config/ShopItem2.xml"),
            selection: "catalog_masks", fighterConfigPath: LocalClientData.Extract("kr-1256", "Config/FighterConfig.xml"));
        await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
            new[] { 100001, 100004, 100689, 103451, 300005, 300009 }
                .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0)).ToArray(), default);
        // 203073 and 202001 share slotLocation sWeapon1; the worn practice rifle gives way.
        await h.Selection.ApplyAsync(42, 203073, true, default);
        var armed = Worn(await h.Selection.ReadAsync(42, default));
        Assert.Contains(203073, armed);
        Assert.DoesNotContain(202001, armed);
        // The penguin costume's parts cover the shoes' Feet; shoes give way, and the shop workbook's own
        // secondary part list is what displaces them.
        await h.Selection.ApplyAsync(42, 100001, true, default);
        await h.Selection.ApplyAsync(42, 100004, true, default);
        var dressed = Worn(await h.Selection.ReadAsync(42, default));
        Assert.DoesNotContain(100001, dressed);
        // 100689 allowChr "Uni LXUni" refuses Dao (300001) and accepts Uni (300005); 103451 denyChr "PinkBean"
        // refuses 300009 and accepts Dao.
        Assert.Null(await h.Selection.ApplyAsync(42, 100689, true, default));
        await h.Selection.ApplyAsync(42, 300005, true, default);
        Assert.NotNull(await h.Selection.ApplyAsync(42, 100689, true, default));
        await h.Selection.ApplyAsync(42, 300009, true, default);
        Assert.Null(await h.Selection.ApplyAsync(42, 103451, true, default));
        await h.Selection.ApplyAsync(42, 300001, true, default);
        Assert.NotNull(await h.Selection.ApplyAsync(42, 103451, true, default));
        // The Uni shoes took the costume's Feet part when they were accepted, so the costume gave way.
        var final = Worn(await h.Selection.ReadAsync(42, default));
        Assert.All(new[] { 300001, 103451, 203073, 100689 }, id => Assert.Contains(id, final));
        Assert.DoesNotContain(100004, final);
    }

    [Fact]
    [Trait("Category", "LocalClientData")]
    public async Task Legacy812CatalogMasksShareTheLocationColumnSet()
    {
        using var h = await Harness.CreateAsync(LocalClientData.Extract("kr-812", "Config/ShopItem2.xml"),
            selection: "catalog_masks", fighterConfigPath: LocalClientData.Extract("kr-812", "Config/FighterConfig.xml"));
        await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
            new[] { 100001, 100004, 202002 }
                .Select(id => new InventorySlotRecord(42, InventoryBag.CharacterBag, id, id, 1, 0)).ToArray(), default);
        // The legacy workbook carries the same columns: two sWeapon1 rifles and the same Feet-covering costume.
        await h.Selection.ApplyAsync(42, 202002, true, default);
        var armed = Worn(await h.Selection.ReadAsync(42, default));
        Assert.Contains(202002, armed);
        Assert.DoesNotContain(202001, armed);
        await h.Selection.ApplyAsync(42, 100001, true, default);
        await h.Selection.ApplyAsync(42, 100004, true, default);
        var dressed = Worn(await h.Selection.ReadAsync(42, default));
        Assert.DoesNotContain(100001, dressed);
        Assert.Contains(100004, dressed);
    }

    private static int[] ReadFlags(byte[] body)
    {
        using var source = new MemoryStream(body, 4, body.Length - 4);
        using var zip = new ZLibStream(source, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zip.CopyTo(plain);
        var r = new PacketReader(plain.ToArray());
        var count = r.ReadUInt32();
        var worn = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadInt32(); r.ReadInt32(); r.ReadInt64(); r.ReadWideStringPrefixed();
            Assert.Equal(0u, r.ReadUInt32());
            if ((r.ReadByte() & 1) != 0) worn.Add(id);
        }
        return worn.ToArray();
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        internal IEventBus Events => _events;
        private readonly List<PluginContext> _contexts = [];
        internal MemoryInventoryRepository Store { get; } = new(new MemoryPlayerTable(8));
        internal RecordingSession Session { get; } = new(11, 42);
        internal IEquipmentSelection Selection => _services.Get<IEquipmentSelection>();
        internal IEquippedViewPush View => _services.Get<IEquippedViewPush>();
        internal IPacketRouter Router => _services.Get<IPacketRouter>();
        /// <summary>The lobby push service over the senders the shared message row published.</summary>
        internal InventoryPush Push() => new(
            new LoginInventoryResolver(Store, true, Selection),
            _services.GetAll<IMessageSender<InventoryReplaced>>().Single(),
            _services.GetAll<IMessageSender<InventoryPurchaseApplied>>().Single(),
            _services.GetAll<IMessageSender<InventorySlotsUpdated>>().Single());
        private PluginContext Context(string name, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(name == "dialect.messages" ? SharedDialectRows.Kr1256(config) : new PluginRow { Id = name, Plugin = name, Source = "test", Config = config }, _services, _events, NullLogger.Instance);
            _contexts.Add(context); return context;
        }
        internal static async Task<Harness> CreateAsync(string itemLocationsPath = "",
            string clothingCategory = "Clothes", string accessoryCategory = "Accessories",
            string selection = "id_ranges", string fighterConfigPath = "")
        {
            var h = new Harness();
            var root = h.Context("root");
            root.Effect(h._services.Register<IInventoryRepository>(h.Store));
            await new PacketRouterPlugin().ActivateAsync(h.Context("router"), default);
            await new OpcodeTableRegistryPlugin().ActivateAsync(h.Context("tables"), default);
            root.Effect(h._services.Get<IOpcodeTableRegistry>().Register(EquipmentOpcodeTests.LoadTable()));
            await new MessageRouterPlugin().ActivateAsync(h.Context("core.message-router"), default);
            await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(
                h.Context("dialect.messages", new() { ["inventory"] = true }), default);
            await new InventoryViewPlugin().ActivateAsync(h.Context("inventoryview", new()
            {
                ["enabled"] = true, ["publish_equipped_view_push"] = true, ["item_locations_path"] = itemLocationsPath,
                ["clothing_category"] = clothingCategory, ["accessory_category"] = accessoryCategory,
                ["selection"] = selection, ["fighter_config_path"] = fighterConfigPath,
            }), default);
            root.Effect(h._services.Register<IInventoryPushService>(h.Push()));
            await new EquipmentPlugin().ActivateAsync(h.Context("equipment"), default);
            await h.Store.ReplaceSlotsAsync(42, InventoryBag.CharacterBag, StarterInventory.Slots(42, InventoryBag.CharacterBag, DateTimeOffset.UtcNow), default);
            await h.Store.ReplaceSlotsAsync(42, InventoryBag.EquippedBag, StarterInventory.Slots(42, InventoryBag.EquippedBag, DateTimeOffset.UtcNow), default);
            await h.Store.AppendMissingItemsAsync(42, InventoryBag.CharacterBag,
                [new(42, InventoryBag.CharacterBag, 777, 203073, 7, 0), new(42, InventoryBag.CharacterBag, 778, 300027, 0, 0)], default);
            return h;
        }
        internal Task WearAsync(int id) => Router.DispatchAsync("kr-1256/game", Session, new(804, BitConverter.GetBytes((long)id)), default).AsTask();
        public void Dispose() { for (var i = _contexts.Count - 1; i >= 0; i--) _contexts[i].Unwind(); }
    }
}
