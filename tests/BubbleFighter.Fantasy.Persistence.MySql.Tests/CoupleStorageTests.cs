using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Couples;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks provider activation metadata, invitation bounds and the durable membership lookup.</summary>
public sealed class CoupleStorageTests
{
    private sealed class UnusedFactory : BubbleFighter.Fantasy.Abstractions.Persistence.IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("No database should be opened for invalid options.");
    }

    [Fact]
    public void ProviderAdvertisesCoupleStore()
    {
        var plugin = Assert.IsType<PluginAttribute>(Attribute.GetCustomAttribute(typeof(MySqlPlugin), typeof(PluginAttribute)));
        Assert.Contains(typeof(ICoupleStore), plugin.Provides);
    }

    [Fact]
    public void MembershipLookupReadsPairAndMembershipInOneStatement()
    {
        using var db = SugarSession.Create(new MySqlConnection("Server=unused;Database=unused"));
        var (sql, values) = MySqlCoupleStore.MemberQuery(db, new("players", 72)).ToSql();
        Assert.Contains("couple_members", sql, StringComparison.Ordinal);
        Assert.Contains("couples", sql, StringComparison.Ordinal);
        Assert.Contains("authority", sql, StringComparison.Ordinal);
        Assert.Contains(values, value => Equals(value.Value, "players"));
        Assert.Contains(values, value => Equals(value.Value, 72L));
    }

    [Theory]
    [InlineData(0, 16, 1024, 512)]
    [InlineData(1024, 0, 1024, 512)]
    [InlineData(1024, 16, 0, 512)]
    [InlineData(1024, 16, 1024, 4097)]
    public void InvalidBudgetsFailBeforeOpeningAnyDatabase(int pending, int perAccount, int completed, int message)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MySqlCoupleStore(new UnusedFactory(), new()
        {
            MaxPendingInvitations = pending, MaxPendingPerAccount = perAccount,
            MaxCompletedInvitations = completed, MaxMessageLength = message,
        }));
    }
}
