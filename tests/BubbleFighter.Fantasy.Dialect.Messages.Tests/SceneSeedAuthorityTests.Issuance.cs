using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class SceneSeedAuthorityTests
{
    private static readonly MatchSeedTablesRequested Opening = new(41, 7, 1_000_000, MatchPreviousRound.None);

    /// <summary>A repeated request cannot reinstall completed tables or advance the random source.</summary>
    [Fact]
    public async Task CompletedIssuanceIsNotDrawnOrSentAgain()
    {
        var random = new CountingRandom();
        var authority = Authority("cn-324", random);
        var session = new SeedSession(1);
        await authority.SendAsync(session, Opening, default);
        await authority.SendAsync(session, Opening with { }, default);
        Assert.Equal(2000, random.Draws);
        Assert.Equal(new ushort[] { 4, 5, 6 }, session.Sent.Select(frame => frame.Opcode));
        Assert.Equal(new[] { 268, 1008, 136 }, session.Sent.Select(frame => frame.Payload.Length));
        Assert.All(session.Sent, frame => Assert.Equal(Opening.EntityId, BitConverter.ToInt32(frame.Payload)));
        Assert.True(authority.Issued(session.Id, NativeSceneSeedKind.Critical, Opening.EntityId, out var generation));
        Assert.Equal(Opening.Generation, generation);
        Assert.False(authority.Issued(session.Id, NativeSceneSeedKind.Critical, Opening.EntityId + 1, out _));
    }

    /// <summary>Identical in-flight requests share one send; canceling a duplicate only cancels its own wait.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateRequestsCannotInterleaveInstallations(bool cancelDuplicate)
    {
        var random = new CountingRandom();
        var authority = Authority("cn-324", random);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SeedSession(1) { BeforeSend = (attempt, ct) => attempt == 1 ? release.Task.WaitAsync(ct) : Task.CompletedTask };
        using var cancelled = new CancellationTokenSource();
        var first = authority.SendAsync(session, Opening, default).AsTask();
        var duplicate = authority.SendAsync(session, Opening, cancelled.Token).AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(duplicate.IsCompleted);
            Assert.Equal(1, session.Attempts);
            Assert.Equal(2000, random.Draws);
            Assert.True(authority.Issued(session.Id, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
            if (cancelDuplicate)
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => duplicate);
                Assert.False(first.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (!cancelDuplicate) await duplicate;
        }
        Assert.Equal(new ushort[] { 4, 5, 6 }, session.Sent.Select(frame => frame.Opcode));
    }

    /// <summary>A failed queue attempt cannot be retried by redrawing or reinstalling the same round's tables.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailedIssuanceIsNotReplayed(int failedAttempt)
    {
        var random = new CountingRandom();
        var authority = Authority("cn-324", random);
        var failure = new IOException("Seed queue rejected the frame.");
        var session = new SeedSession(1) { BeforeSend = (attempt, _) => attempt == failedAttempt ? Task.FromException(failure) : Task.CompletedTask };
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, Opening, default).AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, Opening, default).AsTask()));
        Assert.Equal(failedAttempt, session.Attempts);
        Assert.Equal(failedAttempt - 1, session.Sent.Count);
        Assert.Equal(2000, random.Draws);
    }

    /// <summary>Duplicate waiters observe the original failure, while another relay can finish independently.</summary>
    [Fact]
    public async Task PendingFailureIsSharedWithoutBlockingOtherSessions()
    {
        var authority = Authority("cn-324", new CountingRandom());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Critical queue failed.");
        var session = new SeedSession(1) { BeforeSend = (_, ct) => release.Task.WaitAsync(ct) };
        var first = authority.SendAsync(session, Opening, default).AsTask();
        var duplicate = authority.SendAsync(session, Opening, default).AsTask();
        try
        {
            var other = new SeedSession(2);
            await authority.SendAsync(other, Opening, default);
            Assert.Equal(3, other.Sent.Count);
            Assert.False(first.IsCompleted);
            Assert.False(duplicate.IsCompleted);
        }
        finally
        {
            release.TrySetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => duplicate));
        }
        Assert.Equal(1, session.Attempts);
        Assert.Empty(session.Sent);
    }

    /// <summary>Cancellation after critical publication cannot restart that round's installation.</summary>
    [Fact]
    public async Task PartialCancellationCannotRestartAnIssuance()
    {
        var random = new CountingRandom();
        var authority = Authority("cn-324", random);
        using var cancelled = new CancellationTokenSource();
        var session = new SeedSession(1)
        {
            BeforeSend = (attempt, ct) =>
            {
                if (attempt == 2) cancelled.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.SendAsync(session, Opening, cancelled.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.SendAsync(session, Opening, default).AsTask());
        Assert.Equal(2, session.Attempts);
        Assert.Single(session.Sent);
        Assert.Equal(2000, random.Draws);
    }

    /// <summary>One room generation cannot silently change actor or history after installation.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConflictingSameRoundInputsAreRejected(bool changeActor)
    {
        var authority = Authority("cn-324", new CountingRandom());
        var session = new SeedSession(1);
        await authority.SendAsync(session, Opening, default);
        var conflict = changeActor ? Opening with { EntityId = Opening.EntityId + 1 }
            : Opening with { PreviousRound = MatchPreviousRound.Lost };
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, conflict, default).AsTask());
        Assert.Equal(3, session.Attempts);
        Assert.True(authority.Issued(session.Id, NativeSceneSeedKind.Critical, Opening.EntityId, out var generation));
        Assert.Equal(Opening.Generation, generation);
    }

    /// <summary>New rounds draw once, stale same-room requests fail, and room generations are independent.</summary>
    [Fact]
    public async Task NewRoundsReplaceCompletedIssuances()
    {
        var random = new CountingRandom();
        var authority = Authority("cn-324", random);
        var session = new SeedSession(1);
        await authority.SendAsync(session, Opening, default);
        await authority.SendAsync(session, Opening with { Generation = Opening.Generation + 1 }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, Opening, default).AsTask());
        var otherRoom = Opening with { RoomId = 99, Generation = 8, EntityId = 1_100_000 };
        await authority.SendAsync(session, otherRoom, default);
        var newRoom = otherRoom with { RoomId = 100, Generation = 1 };
        await authority.SendAsync(session, newRoom, default);
        Assert.Equal(8000, random.Draws);
        Assert.Equal(12, session.Attempts);
        Assert.False(authority.Issued(session.Id, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
        Assert.True(authority.Issued(session.Id, NativeSceneSeedKind.Critical, otherRoom.EntityId, out var generation));
        Assert.Equal(1, generation);
    }

    /// <summary>A next generation or room move cannot overtake an unfinished multi-packet installation.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentIssuanceCannotOvertakeAPendingSend(bool nextRoom)
    {
        var authority = Authority("cn-324", new CountingRandom());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SeedSession(1) { BeforeSend = (attempt, ct) => attempt == 1 ? release.Task.WaitAsync(ct) : Task.CompletedTask };
        var first = authority.SendAsync(session, Opening, default).AsTask();
        try
        {
            var next = nextRoom ? Opening with { RoomId = 99 } : Opening with { Generation = 8 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, next, default).AsTask());
            Assert.Equal(1, session.Attempts);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
        Assert.Equal(3, session.Sent.Count);
    }

    /// <summary>The session bound counts distinct leases; closing one releases exactly its retained record.</summary>
    [Fact]
    public async Task SessionBoundAndCloseCleanupArePreserved()
    {
        var authority = Authority("cn-324", new CountingRandom());
        for (var id = 1; id <= 4; id++)
            await authority.SendAsync(new SeedSession(id), Opening, default);
        var fifth = new SeedSession(5);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(fifth, Opening, default).AsTask());
        Assert.Empty(fifth.Sent);
        authority.Remove(1);
        Assert.False(authority.Issued(1, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
        Assert.True(authority.Issued(2, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
        await authority.SendAsync(fifth, Opening, default);
        Assert.Equal(3, fifth.Sent.Count);
    }

    /// <summary>A close during a send prevents further tables without recreating the removed record.</summary>
    [Fact]
    public async Task RemovedIssuanceCannotContinueSending()
    {
        var authority = Authority("cn-324", new CountingRandom());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SeedSession(1) { BeforeSend = (attempt, ct) => attempt == 1 ? release.Task.WaitAsync(ct) : Task.CompletedTask };
        var first = authority.SendAsync(session, Opening, default).AsTask();
        authority.Remove(session.Id);
        var replacement = new SeedSession(1);
        var next = Opening with { RoomId = 99, Generation = 1, EntityId = 1_100_000 };
        await authority.SendAsync(replacement, next, default);
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Equal(1, session.Attempts);
        Assert.False(authority.Issued(session.Id, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
        Assert.Equal(3, replacement.Sent.Count);
        Assert.True(authority.Issued(replacement.Id, NativeSceneSeedKind.Critical, next.EntityId, out var generation));
        Assert.Equal(next.Generation, generation);
    }

    /// <summary>Wrong-profile and already-canceled requests reserve neither state nor random draws.</summary>
    [Fact]
    public async Task RejectedRequestsDoNotReserveAnIssuance()
    {
        var random = new CountingRandom();
        var authority = Authority("cn-324", random);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(new SeedSession(1, "cn-406"), Opening, default).AsTask());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.SendAsync(new SeedSession(2), Opening, cancelled.Token).AsTask());
        Assert.False(authority.Issued(1, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
        Assert.False(authority.Issued(2, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
        Assert.Equal(0, random.Draws);
    }

    private sealed class CountingRandom : Random
    {
        internal int Draws { get; private set; }

        /// <summary>Counts Bernoulli inputs without relying on a probabilistic assertion.</summary>
        public override double NextDouble()
        {
            Draws++;
            return 0.5;
        }
    }

    private sealed class SeedSession(long id, string build = "cn-324") : ISession
    {
        /// <summary>Gets the test relay lease identity.</summary>
        public long Id => id;

        /// <summary>Refuses socket access because this fixture exercises queueing only.</summary>
        public IConnection Connection => throw new NotSupportedException();

        /// <summary>Gets the client build used to check authority ownership.</summary>
        public ClientProfile Profile { get; } = new(build, build.Split('-')[0], int.Parse(build.Split('-')[1]), 0, 0, build);

        /// <summary>Gets the session state required by the protocol interface.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal ConcurrentQueue<(ushort Opcode, byte[] Payload)> Sent { get; } = new();
        internal Func<int, CancellationToken, Task> BeforeSend { get; init; } = (_, _) => Task.CompletedTask;
        private int _attempts;
        internal int Attempts => Volatile.Read(ref _attempts);

        /// <summary>Records a copied frame only after the configured queue gate succeeds.</summary>
        public async ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            await BeforeSend(attempt, ct);
            ct.ThrowIfCancellationRequested();
            Sent.Enqueue((opcode, payload.ToArray()));
        }
    }
}
