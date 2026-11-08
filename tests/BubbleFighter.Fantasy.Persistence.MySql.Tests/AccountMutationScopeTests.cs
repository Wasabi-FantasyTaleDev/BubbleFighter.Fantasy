using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Persistence.MySql;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class AccountMutationScopeTests
{
    private static readonly AccountWriteProof First = new(7, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "11111111111111111111111111111111");
    private static readonly AccountWriteProof Second = First with { Token = "22222222222222222222222222222222" };

    [Fact]
    public async Task DelayedOldSessionCannotBorrowTheReplacementSessionsAuthority()
    {
        var old = new Hold(First);
        var next = new Hold(Second);
        var scopes = NewScopes();
        var first = new MutationSession(1);
        var second = new MutationSession(2);
        var firstBinding = scopes.Bind(first, old);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task delayed;
        using (scopes.Enter(first))
        {
            delayed = Task.Run(async () =>
            {
                await resume.Task;
                Assert.Null(scopes.Read(7));
            });
        }
        firstBinding.Dispose();
        using var secondBinding = scopes.Bind(second, next);
        using (scopes.Enter(second))
        {
            Assert.Same(Second, scopes.Read(7));
            resume.SetResult();
            await delayed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(Second, scopes.Read(7));
        }
        Assert.Null(scopes.Read(7));
    }

    [Fact]
    public async Task NestedRecipientScopesRestoreTheOriginalCallerAcrossAwaits()
    {
        var scopes = NewScopes();
        var first = new MutationSession(1);
        var second = new MutationSession(2);
        using var one = scopes.Bind(first, new Hold(First));
        using var two = scopes.Bind(second, new Hold(Second with { UserNum = 8 }));
        using (scopes.Enter(first))
        {
            Assert.Same(First, scopes.Read(7));
            Assert.Null(scopes.Read(8));
            using (scopes.Enter(second))
            {
                await Task.Yield();
                Assert.Equal(Second.Token, scopes.Read(8)!.Token);
                Assert.Null(scopes.Read(7));
            }
            Assert.Same(First, scopes.Read(7));
        }
        Assert.Null(scopes.Read(7));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedOldFlowCannotUseReboundAuthorityOnTheSameSession(bool enterBeforeAdmission)
    {
        var scopes = NewScopes();
        var session = new MutationSession(1);
        IDisposable? originalBinding = enterBeforeAdmission ? null : scopes.Bind(session, new Hold(First));
        using var originalFlow = scopes.Enter(session);
        originalBinding ??= scopes.Bind(session, new Hold(First));
        Assert.Same(First, scopes.Read(7));
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = Task.Run(async () =>
        {
            await resume.Task;
            Assert.Null(scopes.Read(7));
        });
        try
        {
            originalBinding.Dispose();
            using var replacementBinding = scopes.Bind(session, new Hold(Second));
            using (scopes.Enter(session))
            {
                Assert.Same(Second, scopes.Read(7));
                resume.TrySetResult();
                await delayed.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Null(scopes.Read(7));
        }
        finally
        {
            originalBinding.Dispose();
            resume.TrySetResult();
            await delayed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ClosedOrUnboundSessionsAndForeignHoldsCannotAuthorizeMutations()
    {
        var scopes = NewScopes();
        var session = new MutationSession(1);
        Assert.Null(scopes.Read(7));
        using var scope = scopes.Enter(session);
        Assert.Null(scopes.Read(7));
        using var binding = scopes.Bind(session, new Hold(First));
        Assert.Same(First, scopes.Read(7));
        await session.Connection.CloseAsync("test");
        Assert.Null(scopes.Read(7));
        Assert.Throws<InvalidOperationException>(() => scopes.Bind(session, new Hold(Second)));
        Assert.Throws<InvalidOperationException>(() => new MySqlAccountMutationScopes(_ => null).Bind(new MutationSession(2), new Hold(First)));
    }

    [Fact]
    public void AProviderCannotBorrowAnotherProvidersAmbientSession()
    {
        var first = NewScopes();
        var second = NewScopes();
        var session = new MutationSession(1);
        using var binding = first.Bind(session, new Hold(First));
        using var scope = first.Enter(session);
        Assert.Null(second.Read(7));
        using var otherScope = second.Enter(session);
        Assert.Null(second.Read(7));
    }

    private static MySqlAccountMutationScopes NewScopes() => new(owner => owner is Hold hold ? hold.Proof : null);

    private sealed class Hold(AccountWriteProof proof) : IAccountOwnership
    {
        internal AccountWriteProof Proof => proof;
        public ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class MutationSession(long id) : ISessionWithFeatures
{
    public ISessionFeatures Features { get; } = new TestSessionFeatures();
    public long Id => id;
    public IConnection Connection { get; } = new MutationConnection(id);
    public ClientProfile Profile { get; } = new("test", "kr", 812, 0, 0, "test");
    public ConcurrentDictionary<string, object> Items { get; } = new();
    public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();

    private sealed class MutationConnection(long id) : IConnection
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
