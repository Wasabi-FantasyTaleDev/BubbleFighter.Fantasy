using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class AccountOwnershipTests
{
    private const string Domain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void OwnershipConnectionsCannotReusePooledSessions()
    {
        var options = new MySqlAccountOwnershipOptions
        {
            ConnectionString = "Server=localhost;User ID=test;Password=unused;Database=scratch;Pooling=true;Default Command Timeout=100",
            PersistentDomainId = Domain, ConnectTimeoutSeconds = 4, CommandTimeoutSeconds = 2,
        };
        Assert.Empty(options.Validate());
        var builder = options.CreateBuilder();
        Assert.False(builder.Pooling);
        Assert.Equal(4u, builder.ConnectionTimeout);
        Assert.Equal(2u, builder.DefaultCommandTimeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("not-an-authority")]
    public void InvalidDomainFailsValidation(string domain)
    {
        var options = new MySqlAccountOwnershipOptions
        { ConnectionString = "Server=localhost;User ID=test;Password=unused;Database=scratch", PersistentDomainId = domain };
        Assert.Contains(options.Validate(), result => result.MemberNames.Contains(nameof(options.PersistentDomainId)));
    }

    [Fact]
    public void OwnershipKeysSeparateDomainsAndPlayersAndNormalizeMarkerCase()
    {
        var key = MySqlAccountOwnershipStore.LockName(Domain, 7);
        Assert.Equal(64, key.Length);
        Assert.All(key, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        Assert.Equal(key, MySqlAccountOwnershipStore.LockName(Domain.ToUpperInvariant(), 7));
        Assert.NotEqual(key, MySqlAccountOwnershipStore.LockName(Domain, 8));
        Assert.NotEqual(key, MySqlAccountOwnershipStore.LockName("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 7));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidUserDoesNotOpenADatabaseConnection(long userNum)
    {
        var factory = new BlockingFactory();
        await using var store = new MySqlAccountOwnershipStore(factory, Domain, 1, 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.TryAcquireAsync(userNum, default));
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task CancelledAcquisitionDoesNotOpenADatabaseConnection()
    {
        var factory = new BlockingFactory();
        await using var store = new MySqlAccountOwnershipStore(factory, Domain, 1, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.TryAcquireAsync(7, new CancellationToken(true)));
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task PendingAcquisitionConsumesCapacityAndUnloadDrainsIt()
    {
        var factory = new BlockingFactory();
        var store = new MySqlAccountOwnershipStore(factory, Domain, 1, 1);
        var pending = store.TryAcquireAsync(7, default).AsTask();
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.TryAcquireAsync(8, default));
        Assert.Equal(1, factory.Opens);
        var unload = store.DisposeAsync().AsTask();
        Assert.False(unload.IsCompleted);
        factory.Completion.SetException(new IOException("isolated connection failure"));
        await Assert.ThrowsAsync<IOException>(async () => await pending);
        await unload.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.TryAcquireAsync(7, default));
        await store.DisposeAsync();
    }

    [Fact]
    public void ResourceLimitsAreValidatedByTheConfigurationBinder()
    {
        var options = new MySqlAccountOwnershipOptions { MaxOwners = 0, CommandTimeoutSeconds = 0, ConnectTimeoutSeconds = 0 };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
        Assert.Contains(results, error => error.MemberNames.Contains(nameof(options.MaxOwners)));
        Assert.Contains(results, error => error.MemberNames.Contains(nameof(options.CommandTimeoutSeconds)));
        Assert.Contains(results, error => error.MemberNames.Contains(nameof(options.ConnectTimeoutSeconds)));
    }

    [Theory]
    [InlineData("NODE-A")]
    [InlineData("node a")]
    [InlineData("node/a")]
    public void HandoffNodeMustMatchTheDirectoryIdentityFormat(string node)
    {
        var options = new MySqlAccountOwnershipOptions { NodeId = node };
        Assert.Contains(options.Validate(), error => error.MemberNames.Contains(nameof(options.NodeId)));
    }

    [Fact]
    public async Task UnconfiguredNodeCannotClaimOrWithdrawEvenWithTheMigrationIdentity()
    {
        var factory = new BlockingFactory();
        await using var store = new MySqlAccountOwnershipStore(factory, Domain, 1, 1);
        var migration = new string('a', 32);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.TryClaimAsync(7, migration, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.TryWithdrawAsync(7, migration, default));
        Assert.Equal(0, factory.Opens);
    }

    private sealed class BlockingFactory : IDbConnectionFactory
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<DbConnection> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Opens;

        public async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Opens);
            Entered.TrySetResult();
            return await Completion.Task.WaitAsync(ct);
        }
    }
}
