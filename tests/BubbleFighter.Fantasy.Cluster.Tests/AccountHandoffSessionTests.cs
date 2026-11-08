using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class AccountHandoffSessionTests
{
    private const string Migration = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task HandedOffConnectionLosesWritesButStaysOpenUntilTheDestinationClaimsTheAccount()
    {
        var authority = new Authority();
        var mutations = new Mutations();
        await using var source = Sessions(authority, mutations);
        await using var destination = Sessions(authority);
        var player = new Session(1);
        Assert.True(await source.TryAdmitAsync(player, 7, default));

        await source.HandOffAsync(player, Handoff(), default);

        Assert.True(mutations.Removed);
        Assert.Empty(authority.Holds);
        Assert.True(player.TryFeature<IAccountSession>(out var account));
        Assert.False(await account!.IsHeldAsync(default));
        await source.VerifyAsync(default);
        Assert.False(player.Connection.IsClosed);
        Assert.False(await destination.TryAdmitAsync(new Session(2), 7, default));
        Assert.False(await destination.TryClaimAsync(new Session(3), 7, "ffffffffffffffffffffffffffffffff", default));
        var arrived = new Session(4);
        Assert.True(await destination.TryClaimAsync(arrived, 7, Migration, default));
        Assert.Contains(7, authority.Holds.Keys);
        Assert.False(await source.TryWithdrawAsync(player, Migration, default));
        Assert.False(await destination.TryClaimAsync(new Session(5), 7, Migration, default));
    }

    [Fact]
    public async Task WithdrawalRestoresAuthorityOnTheSameConnectionAndRevokesTheMigration()
    {
        var authority = new Authority();
        var mutations = new Mutations();
        await using var source = Sessions(authority, mutations);
        await using var destination = Sessions(authority);
        var player = new Session(1);
        Assert.True(await source.TryAdmitAsync(player, 7, default));
        await source.HandOffAsync(player, Handoff(), default);

        Assert.True(await source.TryWithdrawAsync(player, Migration, default));

        Assert.Equal(2, mutations.Binds);
        Assert.True(player.TryFeature<IAccountSession>(out var account));
        Assert.True(await account!.IsHeldAsync(default));
        Assert.False(await destination.TryClaimAsync(new Session(2), 7, Migration, default));
        Assert.False(await source.TryWithdrawAsync(player, Migration, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.HandOffAsync(new Session(9), Handoff(), default).AsTask());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VerificationOfTheOldHoldCannotCloseAHandoffConnection(bool withdraw, bool failVerification)
    {
        var authority = new Authority();
        await using var source = Sessions(authority);
        var player = new Session(1);
        Assert.True(await source.TryAdmitAsync(player, 7, default));
        var original = authority.Holds[7];
        original.PauseVerification = true;
        original.FailVerification = failVerification;
        var verification = source.VerifyAsync(default).AsTask();
        try
        {
            await original.Verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.HandOffAsync(player, Handoff(), default);
            if (withdraw) Assert.True(await source.TryWithdrawAsync(player, Migration, default));
        }
        finally { original.ContinueVerification.TrySetResult(); }
        await verification.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(player.Connection.IsClosed);
        Assert.True(player.TryFeature<IAccountSession>(out var account));
        Assert.Equal(withdraw, await account!.IsHeldAsync(default));
    }

    [Fact]
    public async Task UnrecordedHandoffTakesTheAccountBackOrClosesTheConnection()
    {
        var authority = new Authority { FailRecord = true };
        await using var source = Sessions(authority);
        var player = new Session(1);
        Assert.True(await source.TryAdmitAsync(player, 7, default));
        await Assert.ThrowsAsync<IOException>(() => source.HandOffAsync(player, Handoff(), default).AsTask());
        Assert.False(player.Connection.IsClosed);
        Assert.True(player.TryFeature<IAccountSession>(out var account));
        Assert.True(await account!.IsHeldAsync(default));

        authority.StealOnRecord = 7;
        await Assert.ThrowsAsync<IOException>(() => source.HandOffAsync(player, Handoff(), default).AsTask());
        Assert.True(player.Connection.IsClosed);
    }

    [Theory]
    [InlineData("", "a", "b")]
    [InlineData(Migration, "a", "a")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", "a", "b")]
    [InlineData(Migration, "a b", "b")]
    public async Task MalformedHandoffsAreRejectedBeforeTheHoldIsReleased(string migration, string source, string destination)
    {
        var authority = new Authority();
        await using var sessions = Sessions(authority);
        var player = new Session(1);
        Assert.True(await sessions.TryAdmitAsync(player, 7, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sessions.HandOffAsync(player, new(7, migration, source, destination, TimeSpan.FromSeconds(5)), default).AsTask());
        Assert.Contains(7, authority.Holds.Keys);
        Assert.False(new AccountHandoff(7, Migration, "a", "b", TimeSpan.FromMinutes(3)).IsValid());
    }

    private static AccountHandoff Handoff() => new(7, Migration, "node-a", "node-b", TimeSpan.FromSeconds(10));

    private static AccountSessions Sessions(Authority authority, Mutations? mutations = null) =>
        new(authority, new AccountSessionOptions(), NullLogger.Instance, mutations, authority);

    // Models the shared authority: one hold per account and one handoff row per account.
    private sealed class Authority : IAccountOwnershipStore, IAccountHandoffStore
    {
        private readonly Lock _gate = new();
        internal ConcurrentDictionary<long, Hold> Holds { get; } = new();
        internal Dictionary<long, (string Migration, string Destination, string State)> Rows { get; } = [];
        internal bool FailRecord { get; set; }
        internal long? StealOnRecord { get; set; }

        public ValueTask<IAccountOwnership?> TryAcquireAsync(long userNum, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Rows.TryGetValue(userNum, out var row) && row.State == "pending") return ValueTask.FromResult<IAccountOwnership?>(null);
                return ValueTask.FromResult(Take(userNum));
            }
        }

        public ValueTask<IAccountOwnership?> TryClaimAsync(long userNum, string migrationId, CancellationToken cancellationToken) =>
            Settle(userNum, migrationId, "claimed");

        public ValueTask<IAccountOwnership?> TryWithdrawAsync(long userNum, string migrationId, CancellationToken cancellationToken) =>
            Settle(userNum, migrationId, "withdrawn");

        public async ValueTask HandOffAsync(IAccountOwnership ownership, AccountHandoff handoff, CancellationToken cancellationToken)
        {
            try
            {
                if (FailRecord) { FailRecord = false; throw new IOException("record failed"); }
                lock (_gate) Rows[handoff.UserNum] = (handoff.MigrationId, handoff.DestinationNode, "pending");
            }
            finally
            {
                await ownership.DisposeAsync();
                if (StealOnRecord is { } stolen) { StealOnRecord = null; Take(stolen); throw new IOException("record failed after theft"); }
            }
        }

        private ValueTask<IAccountOwnership?> Settle(long userNum, string migrationId, string state)
        {
            lock (_gate)
            {
                if (!Rows.TryGetValue(userNum, out var row) || row.State != "pending" || row.Migration != migrationId
                    || Holds.ContainsKey(userNum)) return ValueTask.FromResult<IAccountOwnership?>(null);
                Rows[userNum] = row with { State = state };
                return ValueTask.FromResult(Take(userNum));
            }
        }

        private IAccountOwnership? Take(long userNum)
        {
            var hold = new Hold(this, userNum);
            return Holds.TryAdd(userNum, hold) ? hold : null;
        }
    }

    private sealed class Hold(Authority authority, long userNum) : IAccountOwnership
    {
        private bool _released;
        internal bool PauseVerification;
        internal bool FailVerification;
        internal TaskCompletionSource Verifying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueVerification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken)
        {
            if (PauseVerification)
            {
                Verifying.TrySetResult();
                await ContinueVerification.Task.WaitAsync(cancellationToken);
            }
            if (FailVerification) throw new IOException("The old ownership connection failed.");
            return !_released;
        }
        public ValueTask DisposeAsync()
        {
            _released = true;
            authority.Holds.TryRemove(new KeyValuePair<long, Hold>(userNum, this));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Mutations : IAccountMutationScopes
    {
        internal int Binds;
        internal bool Removed;
        public IDisposable Bind(ISession session, IAccountOwnership ownership)
        {
            Binds++;
            return new Removal(() => Removed = true);
        }
        public IDisposable Enter(ISession session) => new Removal(() => { });
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }

    private sealed class Session(long id) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new SessionFeatures(4);
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new("test", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
