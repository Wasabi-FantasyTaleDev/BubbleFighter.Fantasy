using BubbleFighter.Fantasy.Contracts.Couples;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks durable mutual membership, invitation ownership, generation fences and transaction rollback.</summary>
public sealed class SqliteCoupleStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-couples-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    private static CoupleInvitationDraft Draft(CoupleAccount target) => new(Guid.NewGuid(), target, "hello", Now, Now.AddMinutes(3))
        { RingItemId = 100604 };

    private static async Task<CoupleAccount[]> Players(SqliteDatabase db)
    {
        var repository = new SqliteRepositories(db);
        var result = new List<CoupleAccount>();
        foreach (var name in new[] { "a", "b", "c", "d" })
            result.Add(new("players", (await repository.CreateAsync(name, name, default)).UserNum));
        return result.ToArray();
    }

    [Fact]
    public async Task RelationshipsAndRingSurviveReopenButPendingConsentDoesNot()
    {
        CoupleAccount[] accounts;
        CoupleRelationship expected;
        using (var db = new SqliteDatabase(Options))
        {
            accounts = await Players(db);
            var store = new SqliteCoupleStore(db);
            var draft = Draft(accounts[1]);
            Assert.Equal(CoupleStoreResult.Pending, (await store.CreateInvitationAsync(accounts[0], draft, default)).Result);
            var accepted = await store.AnswerInvitationAsync(accounts[1], draft.Id, true, Now, default);
            Assert.Equal(CoupleStoreResult.Accepted, accepted.Result);
            Assert.Equal(accepted.Relationship, await store.FindRelationshipAsync(accounts[0], default));
            Assert.Equal(accepted, await store.AnswerInvitationAsync(accounts[1], draft.Id, false, Now, default));
            var changed = await store.ChangeRingAsync(accounts[1], draft.Id, 100984, default);
            expected = Assert.IsType<CoupleRelationship>(changed.Relationship);
            Assert.Equal(100984, expected.RingItemId);
            Assert.Equal(expected, (await store.AnswerInvitationAsync(accounts[1], draft.Id, true, Now, default)).Relationship);
            await store.CreateInvitationAsync(accounts[2], Draft(accounts[3]), default);
        }
        using var reopened = new SqliteDatabase(Options);
        var persisted = new SqliteCoupleStore(reopened);
        Assert.True(persisted.PersistsState);
        Assert.Equal(expected, await persisted.FindRelationshipAsync(accounts[0], default));
        Assert.Equal(expected, await persisted.FindRelationshipAsync(accounts[1], default));
        Assert.Empty(await persisted.ListIncomingAsync(accounts[3], Now, default));
    }

    [Fact]
    public async Task DifferentStoreInstancesCannotPairOneAccountInOppositePositions()
    {
        using var db = new SqliteDatabase(Options);
        var a = await Players(db);
        var first = new SqliteCoupleStore(db);
        var second = new SqliteCoupleStore(db);
        var ab = Draft(a[1]);
        var ca = Draft(a[0]);
        await first.CreateInvitationAsync(a[0], ab, default);
        await second.CreateInvitationAsync(a[2], ca, default);
        var results = await Task.WhenAll(
            Task.Run(async () => await first.AnswerInvitationAsync(a[1], ab.Id, true, Now, default)),
            Task.Run(async () => await second.AnswerInvitationAsync(a[0], ca.Id, true, Now, default)));
        var winner = Assert.Single(results, r => r.Result == CoupleStoreResult.Accepted);
        Assert.Single(results, r => r.Result == CoupleStoreResult.AlreadyPaired);
        Assert.Equal(winner.Relationship, await first.FindRelationshipAsync(a[0], default));
        Assert.Equal(2L, db.RawScalar("SELECT COUNT(*) FROM couple_members"));
    }

    [Fact]
    public async Task StaleBreakupAndRingChangeCannotAffectAReplacementPair()
    {
        using var db = new SqliteDatabase(Options);
        var a = await Players(db);
        var store = new SqliteCoupleStore(db);
        var ab = Draft(a[1]);
        await store.CreateInvitationAsync(a[0], ab, default);
        await store.AnswerInvitationAsync(a[1], ab.Id, true, Now, default);
        Assert.Equal(CoupleStoreResult.NotFound, (await store.BreakUpAsync(a[2], ab.Id, default)).Result);
        Assert.Equal(CoupleStoreResult.BrokenUp, (await store.BreakUpAsync(a[1], ab.Id, default)).Result);
        Assert.Null(await store.FindRelationshipAsync(a[0], default));
        Assert.Null(await store.FindRelationshipAsync(a[1], default));
        var ac = Draft(a[2]);
        await store.CreateInvitationAsync(a[0], ac, default);
        var current = await store.AnswerInvitationAsync(a[2], ac.Id, true, Now, default);
        Assert.Equal(CoupleStoreResult.Conflict, (await store.BreakUpAsync(a[0], ab.Id, default)).Result);
        Assert.Equal(CoupleStoreResult.Conflict, (await store.ChangeRingAsync(a[0], ab.Id, 100984, default)).Result);
        Assert.Equal(CoupleStoreResult.BrokenUp, (await store.AnswerInvitationAsync(a[1], ab.Id, true, Now, default)).Result);
        Assert.Equal(current.Relationship, await store.FindRelationshipAsync(a[0], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPairInsertionRollsBackBothMembershipsAndKeepsConsentRetryable(bool secondMembership)
    {
        using var db = new SqliteDatabase(Options);
        var a = await Players(db);
        var store = new SqliteCoupleStore(db);
        var draft = Draft(a[1]);
        await store.CreateInvitationAsync(a[0], draft, default);
        db.RawExecute(secondMembership
            ? $"CREATE TRIGGER refuse_couple BEFORE INSERT ON couple_members WHEN NEW.user_num={a[1].UserNum} BEGIN SELECT RAISE(ABORT,'injected'); END;"
            : "CREATE TRIGGER refuse_couple BEFORE INSERT ON couples BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAnyAsync<Exception>(async () => await store.AnswerInvitationAsync(a[1], draft.Id, true, Now, default));
        Assert.Equal(0L, db.RawScalar("SELECT COUNT(*) FROM couples"));
        Assert.Equal(0L, db.RawScalar("SELECT COUNT(*) FROM couple_members"));
        Assert.Single(await store.ListIncomingAsync(a[1], Now, default));
        db.RawExecute("DROP TRIGGER refuse_couple");
        Assert.Equal(CoupleStoreResult.Accepted, (await store.AnswerInvitationAsync(a[1], draft.Id, true, Now, default)).Result);
    }

    [Fact]
    public async Task ForgedRepliesAuthoritiesAndExpiredConsentCannotFormAPair()
    {
        using var db = new SqliteDatabase(Options);
        var a = await Players(db);
        var store = new SqliteCoupleStore(db);
        var draft = Draft(a[1]);
        await store.CreateInvitationAsync(a[0], draft, default);
        foreach (var actor in new[] { a[0], a[2], a[1] with { Authority = "other" } })
            Assert.Equal(CoupleStoreResult.Forbidden, (await store.AnswerInvitationAsync(actor, draft.Id, true, Now, default)).Result);
        Assert.Equal(CoupleStoreResult.Forbidden, (await store.CancelInvitationAsync(a[1], draft.Id, Now, default)).Result);
        Assert.Equal(CoupleStoreResult.Conflict, (await store.CreateInvitationAsync(a[0], draft with { Message = "changed" }, default)).Result);
        Assert.Equal(CoupleStoreResult.Expired, (await store.AnswerInvitationAsync(a[1], draft.Id, true, draft.ExpiresAt, default)).Result);
        Assert.Null(await store.FindRelationshipAsync(a[0], default));
        var again = Draft(a[1]);
        await store.CreateInvitationAsync(a[0], again, default);
        await store.AnswerInvitationAsync(a[1], again.Id, true, Now, default);
        Assert.Null(await store.FindRelationshipAsync(a[0] with { Authority = "other" }, default));
    }

    [Fact]
    public async Task PendingAndReceiptBoundsDoNotEvictLiveRelationships()
    {
        var options = Options;
        options.CoupleInvitations = new() { MaxPendingInvitations = 1, MaxPendingPerAccount = 1, MaxCompletedInvitations = 1 };
        using var db = new SqliteDatabase(options);
        var a = await Players(db);
        var store = new SqliteCoupleStore(db);
        var ab = Draft(a[1]);
        await store.CreateInvitationAsync(a[0], ab, default);
        Assert.Equal(CoupleStoreResult.CapacityReached, (await store.CreateInvitationAsync(a[2], Draft(a[3]), default)).Result);
        await store.AnswerInvitationAsync(a[1], ab.Id, true, Now, default);
        var cd = Draft(a[3]);
        await store.CreateInvitationAsync(a[2], cd, default);
        await store.AnswerInvitationAsync(a[3], cd.Id, false, Now, default);
        Assert.Equal(CoupleStoreResult.NotFound, (await store.AnswerInvitationAsync(a[1], ab.Id, true, Now, default)).Result);
        Assert.NotNull(await store.FindRelationshipAsync(a[0], default));
    }

    [Fact]
    public async Task Schema14UpgradesWithoutChangingExistingPlayers()
    {
        CoupleAccount[] accounts;
        using (var db = new SqliteDatabase(Options))
        {
            accounts = await Players(db);
            db.RawExecute("DROP TABLE couple_members; DROP TABLE couples; PRAGMA user_version=14");
        }
        using var upgraded = new SqliteDatabase(Options);
        Assert.Equal(18L, upgraded.RawScalar("PRAGMA user_version"));
        Assert.Equal(4L, upgraded.RawScalar("SELECT COUNT(*) FROM players"));
        var store = new SqliteCoupleStore(upgraded);
        var draft = Draft(accounts[1]);
        await store.CreateInvitationAsync(accounts[0], draft, default);
        Assert.Equal(CoupleStoreResult.Accepted, (await store.AnswerInvitationAsync(accounts[1], draft.Id, true, Now, default)).Result);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
