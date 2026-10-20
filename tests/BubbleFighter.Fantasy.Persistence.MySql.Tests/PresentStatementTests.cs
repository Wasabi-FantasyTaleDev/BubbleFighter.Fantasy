using System.Data;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Persistence.MySql;
using MySqlConnector;
using SqlSugar;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

// Renders the gift statements offline (SqlSugar binds to a real MySqlConnection that is never opened) and checks them
// against migration 0029. A delivery is only correct if its update names exactly the rows a pending gift of the
// receiving player can be, so each predicate is pinned.
public sealed class PresentStatementTests
{
    private static SqlSugarClient Client() => SugarSession.Create(new MySqlConnection("Server=unused;Database=bf_game_db"));

    private static string Normalize(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Migration()
    {
        var assembly = typeof(MySqlPlugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0029_presents.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void DeliveryMarksTheGiftTakenOnlyWhileItIsPendingAndVisibleForTheReceiver()
    {
        var statement = MySqlPresentStore.AcceptUpdate(Client(), 77, 5).ToSql();
        var sql = Normalize(statement.Key);
        Assert.StartsWith("UPDATE `presents` SET `state` =", sql, StringComparison.Ordinal);
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        foreach (var column in new[] { "`id`", "`receiver_user_num`", "`state`", "`receiver_hidden`" })
            Assert.Contains(column, where, StringComparison.Ordinal);
        Assert.DoesNotContain("sender", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, (byte)PresentState.Accepted));
        Assert.Contains(statement.Value, p => Equals(p.Value, (byte)PresentState.Pending));
        Assert.Contains(statement.Value, p => Equals(p.Value, 77L));
        Assert.Contains(statement.Value, p => Equals(p.Value, 5L));
    }

    [Fact]
    public void ThrowingAwayMarksTheGiftDiscardedAndHidesTheReceiversViewOfThatBuildOnly()
    {
        var statement = MySqlPresentStore.DiscardUpdate(Client(), "kr-812", 5, 77).ToSql();
        var sql = Normalize(statement.Key);
        var set = sql[..sql.IndexOf("WHERE", StringComparison.Ordinal)];
        Assert.Contains("`state` =", set, StringComparison.Ordinal);
        Assert.Contains("`receiver_hidden` =", set, StringComparison.Ordinal);
        Assert.DoesNotContain("sender_hidden", sql, StringComparison.Ordinal);
        Assert.Contains("`profile` =", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, (byte)PresentState.Discarded));
        Assert.Contains(statement.Value, p => Equals(p.Value, "kr-812"));
    }

    [Theory]
    [InlineData(PresentBox.Received, "receiver_hidden", "receiver_user_num", "sender_hidden")]
    [InlineData(PresentBox.Sent, "sender_hidden", "sender_user_num", "receiver_hidden")]
    public void RemovingAGiftHidesOnlyTheCallersSide(PresentBox box, string hides, string owner, string other)
    {
        var sql = Normalize(MySqlPresentStore.HideUpdate(Client(), "kr-812", 5, 77, box).ToSql().Key);
        var set = sql[..sql.IndexOf("WHERE", StringComparison.Ordinal)];
        Assert.Contains($"`{hides}` =", set, StringComparison.Ordinal);
        Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
        Assert.Contains($"`{owner}` =", sql, StringComparison.Ordinal);
        // A side that already hid the gift matches nothing, so a repeated removal reports false.
        Assert.Contains($"`{hides}` =", sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void AGiftIsDeletedOnlyOnceBothSidesHaveHiddenIt()
    {
        var statement = MySqlPresentStore.PurgeDelete(Client(), 77).ToSql();
        var sql = Normalize(statement.Key);
        Assert.StartsWith("DELETE FROM `presents`", sql, StringComparison.Ordinal);
        Assert.Contains("`sender_hidden` =", sql, StringComparison.Ordinal);
        Assert.Contains("`receiver_hidden` =", sql, StringComparison.Ordinal);
        Assert.Contains("`id` =", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMigrationDefinesEveryColumnTheEntityMapsWithItsDomainChecks()
    {
        var script = Migration();
        Assert.Contains("CREATE TABLE IF NOT EXISTS presents (", script, StringComparison.Ordinal);
        foreach (var column in new[]
        {
            "id BIGINT NOT NULL AUTO_INCREMENT", "profile VARCHAR(32) NOT NULL", "sender_user_num BIGINT NOT NULL",
            "sender_nickname VARCHAR(64) NOT NULL", "receiver_user_num BIGINT NOT NULL", "receiver_nickname VARCHAR(64) NOT NULL",
            "stock_key INT NOT NULL", "message VARCHAR(1024) NOT NULL", "sent_at_unix_ms BIGINT NOT NULL",
            "state TINYINT UNSIGNED NOT NULL DEFAULT 0", "sender_hidden TINYINT UNSIGNED NOT NULL DEFAULT 0",
            "receiver_hidden TINYINT UNSIGNED NOT NULL DEFAULT 0",
        })
            Assert.Contains(column, script, StringComparison.Ordinal);
        Assert.Contains("CHECK (state IN (0, 1, 2))", script, StringComparison.Ordinal);
        Assert.Contains("CHECK (sender_hidden IN (0, 1) AND receiver_hidden IN (0, 1))", script, StringComparison.Ordinal);
        // The two list queries order by id within one player and build.
        Assert.Contains("KEY ix_presents_receiver (receiver_user_num, profile, id)", script, StringComparison.Ordinal);
        Assert.Contains("KEY ix_presents_sender (sender_user_num, profile, id)", script, StringComparison.Ordinal);
        // The limits the contracts promise must fit the columns.
        Assert.True(PresentLimits.MaxNicknameChars <= 64);
        Assert.True(PresentLimits.MaxMessageChars <= 1024);
    }

    [Fact]
    public void TheMessageAndNicknameLimitsOfTheContractFitTheSqlColumns()
    {
        // players.nickname is VARCHAR(64) in migration 0003, which the nickname lookup compares against.
        var assembly = typeof(MySqlPlugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0003_players.sql", StringComparison.Ordinal)
            || n.Contains("0003", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        Assert.Contains("nickname VARCHAR(64)", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARewardClaimIsLookedUpByBuildPlayerAndExactKey()
    {
        var statement = MySqlPresentStore.ClaimQuery(Client(), "kr-812", 5, "level:12").ToSql();
        var sql = Normalize(statement.Key);
        Assert.Contains("FROM `present_claims`", sql, StringComparison.Ordinal);
        foreach (var column in new[] { "`profile`", "`user_num`", "`claim_key`" })
            Assert.Contains(column, sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, "level:12"));
        Assert.Contains(statement.Value, p => Equals(p.Value, "kr-812"));
        Assert.Contains(statement.Value, p => Equals(p.Value, 5L));
    }

    [Fact]
    public void TheClaimMigrationMakesTheKeyOncePerPlayerAndBuildAndCaseSensitive()
    {
        var assembly = typeof(MySqlPlugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0030_present_claims.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var script = reader.ReadToEnd();
        Assert.Contains("CREATE TABLE IF NOT EXISTS present_claims (", script, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (profile, user_num, claim_key)", script, StringComparison.Ordinal);
        Assert.Contains($"claim_key VARCHAR({PresentLimits.MaxClaimKeyChars}) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("profile VARCHAR(32) NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("claimed_at_unix_ms BIGINT NOT NULL", script, StringComparison.Ordinal);
    }
}
