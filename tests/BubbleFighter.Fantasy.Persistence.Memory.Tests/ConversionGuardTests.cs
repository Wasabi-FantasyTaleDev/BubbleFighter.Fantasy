using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>
/// Same-identity permanent conversion: one counted card is spent and the timed target becomes permanent in place,
/// judged under the account lock at the later of the command instant and the current time, so stale, short, expired, Article, worn and already
/// permanent targets spend nothing.
/// </summary>
public sealed class ConversionGuardTests
{
    private const int Card = 190151;
    private const int Target = 190035;
    private const long Day = 86_400_000;
    private static readonly DateTimeOffset Now = new(2099, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [Card, Target]);

    [Fact]
    public async Task ConcurrentConversionsSpendOneCardAndKeepTheRowInPlace()
    {
        var (store, rows) = await Seed(Now.AddDays(10));
        var command = Command(rows);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await store.TryLaboratoryAsync(command, default))));
        var result = Assert.Single(attempts, attempt => attempt is not null)!;
        var converted = Assert.Single(result.GrantedItems);
        Assert.Equal(rows[1].SlotKey, converted.SlotKey);
        Assert.Equal(rows[1].Flags, converted.Flags);
        Assert.Equal(InventoryLifetimeKind.Permanent, converted.Lifetime.Kind);
        Assert.Equal(4, result.Inventory.CharacterBag.Single(row => row.ItemId == Card).Quantity);

        // A request rebuilt from the converted row is refused: the target is no longer timed.
        var rebuilt = Command([result.Inventory.CharacterBag.Single(row => row.ItemId == Card), converted]);
        Assert.Null(await store.TryLaboratoryAsync(rebuilt, default));
        Assert.Equal(4, (await store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag.Single(row => row.ItemId == Card).Quantity);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("expired_while_waiting")]
    [InlineData("article")]
    [InlineData("worn")]
    [InlineData("stale")]
    [InlineData("beyond_timed_range")]
    public async Task IneligibleTargetsSpendNothing(string reason)
    {
        var deadline = reason switch
        {
            "short" => Now.AddMilliseconds(30 * Day - 501),
            "beyond_timed_range" => Now.AddMilliseconds(10_001 * Day),
            _ => Now.AddDays(10),
        };
        var (store, rows) = await Seed(deadline, reason == "worn" ? target => target with { Flags = InventorySlotRecord.WornFlagBit } : null,
            article: reason == "article");
        // A command judged after the deadline stands for a timer that ran out during a lock wait.
        var command = Command(rows, minimumDays: reason == "short" ? 30 : 1, at: reason == "expired_while_waiting" ? Now.AddDays(10) : Now);
        if (reason == "stale") await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [rows[0], rows[1] with { Quantity = 2 }], default);
        var before = (await store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag;

        Assert.Null(await store.TryLaboratoryAsync(command, default));
        Assert.Equal(before, (await store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag);
    }

    [Fact]
    public async Task TheMinimumIncludesTheNativeHalfSecond()
    {
        var (store, rows) = await Seed(Now.AddMilliseconds(30 * Day - 500));
        Assert.NotNull(await store.TryLaboratoryAsync(Command(rows, minimumDays: 30), default));
    }

    [Fact]
    public void AConversionMustGrantOnlyItsOwnTargetOverItsRow()
    {
        var card = new InventorySlotRecord(1, InventoryBag.CharacterBag, 1, Card, 5, 0) { Lifetime = InventoryLifetime.Permanent };
        var target = new InventorySlotRecord(1, InventoryBag.CharacterBag, 2, Target, 1, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(5)) };
        var guard = new InventoryConversionGuard(Target, Day, 0x79);
        Assert.Throws<ArgumentException>(() => new InventoryLaboratoryCommand(1, Scope, [new(card, 1, false)],
            [new(new(Target, InventoryGrantKind.Days, 1, 100), target)], Now) { Conversions = [guard] }.Validate());
        Assert.Throws<ArgumentException>(() => new InventoryLaboratoryCommand(1, Scope, [new(card, 1, false)],
            [new(new(Target, InventoryGrantKind.Permanent, 1, 1), null)], Now) { Conversions = [guard] }.Validate());
        Assert.Throws<ArgumentException>(() => new InventoryLaboratoryCommand(1, Scope, [new(target, 0, true)],
            [new(new(Target, InventoryGrantKind.Permanent, 1, 1), target)], Now) { Conversions = [guard] }.Validate());
    }

    [Fact]
    public async Task ASetOfTargetsCommitsTogetherOrNotAtAll()
    {
        const int Second = Target + 1;
        var scope = new InventoryLoadoutScope("conversion-set-test", [Card, Target, Second]);
        var store = new MemoryInventoryRepository(new(1));
        InventorySlotRecord[] rows =
        [
            new(1, InventoryBag.CharacterBag, 1, Card, 5, 0) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, Target, 1, 0x02) { Lifetime = InventoryLifetime.Until(Now.AddDays(5)) },
            new(1, InventoryBag.CharacterBag, 3, Second, 1, 0x02) { Lifetime = InventoryLifetime.Until(Now.AddDays(5)) },
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        InventoryLaboratoryCommand Set(InventoryConversionGuard second, DateTimeOffset at) => new(1, scope, [new(rows[0], 2, false)],
            [new(new(Target, InventoryGrantKind.Permanent, 1, 1), rows[1]), new(new(Second, InventoryGrantKind.Permanent, 1, 1), rows[2])], at)
        {
            Conversions = [new(Target, Day, 0x79), second],
        };

        // The second member's own guard refuses, so neither member converts and no card is spent.
        var before = (await store.ReadLaboratoryInventoryAsync(1, scope, default)).CharacterBag;
        Assert.Null(await store.TryLaboratoryAsync(Set(new(Second, 10 * Day, 0x79), Now), default));
        Assert.Equal(before, (await store.ReadLaboratoryInventoryAsync(1, scope, default)).CharacterBag);

        var committed = await store.TryLaboratoryAsync(Set(new(Second, Day, 0x79), Now), default);
        Assert.NotNull(committed);
        Assert.Equal(2, committed.GrantedItems.Count);
        Assert.All(committed.GrantedItems, row => Assert.Equal(InventoryLifetimeKind.Permanent, row.Lifetime.Kind));
        Assert.Equal(3, committed.Inventory.CharacterBag.Single(row => row.ItemId == Card).Quantity);
    }

    [Fact]
    public void AConversionNeedsOneDistinctGuardPerGrantedTarget()
    {
        var card = new InventorySlotRecord(1, InventoryBag.CharacterBag, 1, Card, 5, 0) { Lifetime = InventoryLifetime.Permanent };
        var target = new InventorySlotRecord(1, InventoryBag.CharacterBag, 2, Target, 1, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(5)) };
        var guard = new InventoryConversionGuard(Target, Day, 0x79);
        InventoryLaboratoryGrant[] grants = [new(new(Target, InventoryGrantKind.Permanent, 1, 1), target)];
        Assert.Throws<ArgumentException>(() => new InventoryLaboratoryCommand(1, Scope, [new(card, 1, false)], grants, Now)
        { Conversions = [guard, guard] }.Validate());
        Assert.Throws<ArgumentException>(() => new InventoryLaboratoryCommand(1, Scope, [new(card, 1, false)], grants, Now)
        { Conversions = [new(Card, Day, 0x79)] }.Validate());
    }

    private static async Task<(MemoryInventoryRepository Store, InventorySlotRecord[] Rows)> Seed(DateTimeOffset deadline,
        Func<InventorySlotRecord, InventorySlotRecord>? edit = null, bool article = false)
    {
        var store = new MemoryInventoryRepository(new(1));
        var target = new InventorySlotRecord(1, InventoryBag.CharacterBag, 2, Target, 1, 0x02) { Lifetime = InventoryLifetime.Until(deadline) };
        InventorySlotRecord[] rows =
        [
            new(1, InventoryBag.CharacterBag, 1, Card, 5, 0) { Lifetime = InventoryLifetime.Permanent },
            edit?.Invoke(target) ?? target,
        ];
        if (article)
        {
            // Article identity is only initialized on permanent ownership; a timed Article row is a later lifetime change.
            await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [rows[0], rows[1] with { Lifetime = InventoryLifetime.Permanent }], default);
            var initialized = await store.InitializeArticlesAsync(new(1, Scope, [Target], Now), default);
            rows = initialized.CharacterBag.Select(row => row.ItemId == Target ? row with { Lifetime = InventoryLifetime.Until(deadline) } : row)
                .OrderBy(row => row.SlotKey).ToArray();
            Assert.NotNull(rows[1].ArticleIdentity);
        }
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        return (store, rows);
    }

    private static InventoryLaboratoryCommand Command(IReadOnlyList<InventorySlotRecord> rows, int minimumDays = 1, DateTimeOffset? at = null) =>
        new(1, Scope, [new(rows[0], 1, false)], [new(new(Target, InventoryGrantKind.Permanent, 1, 1), rows[1])], at ?? Now)
        {
            Conversions = [new(Target, minimumDays * Day, 0x79)],
        };
}
