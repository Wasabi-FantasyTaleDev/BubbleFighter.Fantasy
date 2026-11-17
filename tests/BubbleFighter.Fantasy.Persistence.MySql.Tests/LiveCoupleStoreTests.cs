using BubbleFighter.Fantasy.Contracts.Couples;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>Two provider instances contend on one player in opposite positions; only one relationship may commit.</summary>
    [Fact]
    public async Task CouplesSerializeBothPlayersAcrossIndependentStoreInstances()
    {
        if (await FactoryAsync() is not { } factory) return;
        var a = new CoupleAccount("players", await NewPlayerAsync());
        var b = new CoupleAccount("players", await NewPlayerAsync());
        var c = new CoupleAccount("players", await NewPlayerAsync());
        var now = DateTimeOffset.UtcNow;
        var ab = new CoupleInvitationDraft(Guid.NewGuid(), b, "hello", now, now.AddMinutes(3)) { RingItemId = 100604 };
        var ca = new CoupleInvitationDraft(Guid.NewGuid(), a, "hello", now, now.AddMinutes(3)) { RingItemId = 100984 };
        var first = new MySqlCoupleStore(factory, new());
        var second = new MySqlCoupleStore(factory, new());
        await first.CreateInvitationAsync(a, ab, default);
        await second.CreateInvitationAsync(c, ca, default);
        var results = await Task.WhenAll(first.AnswerInvitationAsync(b, ab.Id, true, now, default).AsTask(),
            second.AnswerInvitationAsync(a, ca.Id, true, now, default).AsTask());
        var winner = Assert.Single(results, result => result.Result == CoupleStoreResult.Accepted);
        Assert.Single(results, result => result.Result == CoupleStoreResult.AlreadyPaired);
        var pair = Assert.IsType<CoupleRelationship>(winner.Relationship);
        var reopened = new MySqlCoupleStore(factory, new());
        Assert.Equal(pair, await reopened.FindRelationshipAsync(pair.First, default));
        Assert.Equal(pair, await reopened.FindRelationshipAsync(pair.Second, default));
        Assert.Null(await reopened.FindRelationshipAsync(a with { Authority = "other" }, default));
        var changed = await reopened.ChangeRingAsync(pair.First, pair.Id, 101034, default);
        Assert.Equal(101034, changed.Relationship!.RingItemId);
        Assert.Equal(changed.Relationship, await first.FindRelationshipAsync(pair.Second, default));
        Assert.Equal(CoupleStoreResult.Conflict, (await reopened.BreakUpAsync(pair.First, Guid.NewGuid(), default)).Result);
        Assert.Equal(CoupleStoreResult.BrokenUp, (await reopened.BreakUpAsync(pair.Second, pair.Id, default)).Result);
        Assert.Null(await first.FindRelationshipAsync(pair.First, default));
        Assert.Null(await second.FindRelationshipAsync(pair.Second, default));
    }

    /// <summary>A failed second membership insert rolls back the parent and first member and leaves the invitation retryable.</summary>
    [Fact]
    public async Task CoupleTransactionRollbackKeepsPendingConsent()
    {
        if (await FactoryAsync() is not { } factory) return;
        var a = new CoupleAccount("players", await NewPlayerAsync());
        var b = new CoupleAccount("players", await NewPlayerAsync());
        var now = DateTimeOffset.UtcNow;
        var draft = new CoupleInvitationDraft(Guid.NewGuid(), b, "hello", now, now.AddMinutes(3)) { RingItemId = 100604 };
        var store = new MySqlCoupleStore(factory, new());
        await store.CreateInvitationAsync(a, draft, default);
        await using var raw = await RawAsync();
        var name = "test_couple_" + Guid.NewGuid().ToString("N");
        await using (var create = raw.CreateCommand())
        {
            create.CommandText = $"CREATE TRIGGER {name} BEFORE INSERT ON couple_members FOR EACH ROW BEGIN IF NEW.user_num={b.UserNum} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected couple rollback'; END IF; END";
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await store.AnswerInvitationAsync(b, draft.Id, true, now, default));
            Assert.Null(await store.FindRelationshipAsync(a, default));
            Assert.Null(await store.FindRelationshipAsync(b, default));
            Assert.Single(await store.ListIncomingAsync(b, now, default));
        }
        finally
        {
            await using var drop = raw.CreateCommand();
            drop.CommandText = $"DROP TRIGGER {name}";
            await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal(CoupleStoreResult.Accepted, (await store.AnswerInvitationAsync(b, draft.Id, true, now, default)).Result);
        Assert.Equal(CoupleStoreResult.Accepted, (await store.AnswerInvitationAsync(b, draft.Id, false, now, default)).Result);
        await store.BreakUpAsync(a, draft.Id, default);
        Assert.Equal(CoupleStoreResult.BrokenUp, (await store.AnswerInvitationAsync(b, draft.Id, true, now, default)).Result);
    }
}
