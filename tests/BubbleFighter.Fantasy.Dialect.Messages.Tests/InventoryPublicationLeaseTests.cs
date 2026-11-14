using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises publication ordering, bounded admission and retirement with explicitly paused producers.</summary>
public sealed class InventoryPublicationLeaseTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A delta admitted first finishes against its original origin before a full list replaces that origin.</summary>
    [Fact]
    public async Task DeltaAndFullCannotPublishAgainstReorderedOrigins()
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session();
        var binding = Inventory(scope);
        var row = Row();
        await Seed(scope, session, Origin);
        var gate = new Pause();
        session.BeforeSend = (attempt, _) => attempt == 1 ? gate.Wait() : Task.CompletedTask;
        var delta = binding.SendAsync(session, new InventorySlotsUpdated([row]), default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Now = Origin.AddDays(1);
        var full = binding.SendLoginAsync(session, [row], default).AsTask();
        Assert.False(full.IsCompleted);
        Assert.Empty(session.Sent);
        gate.Resume();
        await Task.WhenAll(delta, full).WaitAsync(TimeSpan.FromSeconds(10));
        var frames = session.Sent.ToArray();
        Assert.Equal(10, Days(frames[0], "kr-1256"));
        Assert.Equal(9, Days(frames[1], "kr-1256"));
        Assert.Equal(clock.Now, scope.Require(1));
        await binding.SendAsync(session, new InventorySlotsUpdated([row]), default);
        Assert.Equal(9, Days(session.Sent.Last(), "kr-1256"));
    }

    /// <summary>The last full list and the stored origin remain paired even when the first send is paused.</summary>
    [Fact]
    public async Task ConcurrentFullListsKeepTheLastPublishedOrigin()
    {
        var clock = new Clock(Origin.AddDays(1));
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session();
        var binding = Inventory(scope);
        var gate = new Pause();
        session.BeforeSend = (attempt, _) => attempt == 1 ? gate.Wait() : Task.CompletedTask;
        var first = binding.SendLoginAsync(session, [Row()], default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Now = Origin.AddDays(2);
        var second = binding.SendLoginAsync(session, [Row()], default).AsTask();
        Assert.False(second.IsCompleted);
        gate.Resume();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new double[] { 9, 8 }, session.Sent.Select(frame => Days(frame, "kr-1256")));
        Assert.Equal(clock.Now, scope.Require(1));
        await binding.SendAsync(session, new InventorySlotsUpdated([Row()]), default);
        Assert.Equal(8, Days(session.Sent.Last(), "kr-1256"));
    }

    /// <summary>A purchase's full bag and delta finish together before another full publication enters.</summary>
    [Fact]
    public async Task PurchaseBagAndSlotsCannotStraddleAFullList()
    {
        var clock = new Clock(Origin.AddDays(1));
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session();
        var binding = Inventory(scope);
        var gate = new Pause();
        session.BeforeSend = (attempt, _) => attempt == 2 ? gate.Wait() : Task.CompletedTask;
        var purchase = binding.SendAsync(session, new InventoryPurchaseApplied([Row()]), default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(session.Sent);
        clock.Now = Origin.AddDays(2);
        var full = binding.SendLoginAsync(session, [Row()], default).AsTask();
        Assert.False(full.IsCompleted);
        gate.Resume();
        await Task.WhenAll(purchase, full).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new double[] { 9, 9, 8 }, session.Sent.Select(frame => Days(frame, "kr-1256")));
        Assert.Equal(clock.Now, scope.Require(1));
    }

    /// <summary>Legacy shop state and inventory share one lease in both possible producer orders.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShopAndInventorySharePublicationAdmission(bool shopFirst)
    {
        const string build = "kr-812";
        var clock = new Clock(Origin.AddDays(1));
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session(build: build);
        var inventory = Inventory(scope, build);
        var shop = new ShopBinding(build + "/game", TestBuilds.Opcodes(build), scope, TestBuilds.Packets, build);
        var gate = new Pause();
        session.BeforeSend = (attempt, _) => attempt == 1 ? gate.Wait() : Task.CompletedTask;
        Task Shop() => shop.SendAsync(session, new ShopStateUpdated(new(42, "owner", "Owner", 1),
            new(42, 100, 0), [Row()], true), default).AsTask();
        var first = shopFirst ? Shop() : inventory.SendLoginAsync(session, [Row()], default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Now = Origin.AddDays(2);
        var second = shopFirst ? inventory.SendLoginAsync(session, [Row()], default).AsTask() : Shop();
        Assert.False(second.IsCompleted);
        gate.Resume();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        var frames = session.Sent.ToArray();
        var fullOpcode = TestBuilds.Opcodes(build).S2C("gspInventoryUpdateAllInventorySlot");
        Assert.Equal(new double[] { 9, 8 }, frames.Where(frame => frame.Opcode == fullOpcode).Select(frame => Days(frame, build)));
        Assert.Equal(TestBuilds.Opcodes(build).S2C("gspUserStat"), frames[shopFirst ? 1 : 2].Opcode);
        Assert.Equal(clock.Now, scope.Require(1));
        await inventory.SendAsync(session, new InventorySlotsUpdated([Row()]), default);
        Assert.Equal(8, Days(session.Sent.Last(), build));
    }

    /// <summary>A finite legacy completion reads and publishes against the origin ordered with its own lease.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaboratoryAndFullInventoryShareTheOriginLease(bool laboratoryFirst)
    {
        const string build = "kr-812";
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session(build: build);
        await Seed(scope, session, Origin);
        var inventory = Inventory(scope, build);
        var laboratory = new LaboratoryBinding(build + "/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build, scope);
        var gate = new Pause();
        session.BeforeSend = (attempt, _) => attempt == 1 ? gate.Wait() : Task.CompletedTask;
        Task Complete() => laboratory.SendAsync(session,
            new LaboratoryCompleted(LaboratoryOperation.Synthesis, 202001, Row().Lifetime), default).AsTask();
        if (!laboratoryFirst) clock.Now = Origin.AddDays(1);
        var first = laboratoryFirst ? Complete() : inventory.SendLoginAsync(session, [Row()], default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Now = Origin.AddDays(1);
        var second = laboratoryFirst ? inventory.SendLoginAsync(session, [Row()], default).AsTask() : Complete();
        Assert.False(second.IsCompleted);
        gate.Resume();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        var reply = Assert.Single(session.Sent, frame => frame.Opcode == TestBuilds.Opcodes(build).S2C("gspSynthesisProcess"));
        Assert.Equal(laboratoryFirst ? 10 : 9, BinaryPrimitives.ReadDoubleLittleEndian(reply.Payload.AsSpan(20)));
        Assert.Equal(clock.Now, scope.Require(1));
        await Complete();
        Assert.Equal(9, BinaryPrimitives.ReadDoubleLittleEndian(session.Sent.Last().Payload.AsSpan(20)));
    }

    /// <summary>A blocked publisher does not hold the scope lock or the lease of another connection.</summary>
    [Fact]
    public async Task DifferentConnectionsPublishIndependently()
    {
        using var scope = new InventoryBaselineScope(2, new Clock(Origin));
        var first = new Session();
        var second = new Session(2);
        var binding = Inventory(scope);
        var gate = new Pause();
        first.BeforeSend = (_, _) => gate.Wait();
        var held = binding.SendLoginAsync(first, [Row()], default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await binding.SendLoginAsync(second, [Row()], default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(second.Sent);
        Assert.Empty(first.Sent);
        gate.Resume();
        await held;
    }

    /// <summary>Canceling a queued caller releases its budget without releasing the active caller's semaphore.</summary>
    [Fact]
    public async Task PendingCapacityAndCancellationReleaseExactlyOneAdmission()
    {
        using var scope = new InventoryBaselineScope(1, new Clock(Origin), 2);
        var connection = new Connection(1);
        var active = await scope.EnterAsync(connection, default);
        using var stop = new CancellationTokenSource();
        var canceled = scope.EnterAsync(connection, stop.Token).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.EnterAsync(connection, default).AsTask());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var next = scope.EnterAsync(connection, default).AsTask();
        Assert.False(next.IsCompleted);
        active.Dispose();
        active.Dispose();
        using var acquired = await next;
        var stillWaiting = scope.EnterAsync(connection, stop.Token).AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stillWaiting);
        using var freshStop = new CancellationTokenSource();
        var fresh = scope.EnterAsync(connection, freshStop.Token).AsTask();
        Assert.False(fresh.IsCompleted);
        freshStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fresh);
    }

    /// <summary>Retired active work consumes connection capacity until it drains and cannot recreate its old epoch.</summary>
    [Fact]
    public async Task RetirementRetainsTheConnectionBudgetUntilAdmissionsDrain()
    {
        using var scope = new InventoryBaselineScope(1, new Clock(Origin), 2);
        var connection = new Connection(1);
        var active = await scope.EnterAsync(connection, default);
        active.Reset(Origin);
        var waiting = scope.EnterAsync(connection, default).AsTask();
        scope.Remove(1);
        Assert.Null(scope.Find(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.ThrowsAny<OperationCanceledException>(() => active.Reset(Origin.AddDays(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.EnterAsync(new Connection(2), default).AsTask());
        active.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.EnterAsync(connection, default).AsTask());
        using var replacement = await scope.EnterAsync(new Connection(2), default);
        replacement.Reset(Origin);
        Assert.Equal(Origin, scope.Require(2));
    }

    /// <summary>Closing during either preparation stage cancels queued work and prevents the resumed producer from publishing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseDuringPreparationCannotResurrectTheOrigin(bool presentation)
    {
        using var scope = new InventoryBaselineScope(1, new Clock(Origin), 2);
        var events = new EventBus(NullLogger.Instance);
        var gate = new Pause();
        using var hook = PausePreparation(events, gate, presentation);
        var session = new Session();
        var binding = Inventory(scope, events: events);
        var preparing = binding.SendLoginAsync(session, [Row()], default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = binding.SendLoginAsync(session, [Row()], default).AsTask();
        await session.Connection.CloseAsync("Test closure.");
        scope.Remove(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(preparing.IsCompleted);
        Assert.Null(scope.Find(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.EnterAsync(new Connection(2), default).AsTask());
        gate.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Empty(session.Sent);
        Assert.Null(scope.Find(1));
        using var replacement = await scope.EnterAsync(new Connection(2), default);
    }

    /// <summary>Row unload closes admission and cancels waiters even when an interceptor ignores its cancellation token.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeDuringPreparationCancelsWaitersAndRejectsLateContinuations(bool presentation)
    {
        using var scope = new InventoryBaselineScope(1, new Clock(Origin), 2);
        var events = new EventBus(NullLogger.Instance);
        var gate = new Pause();
        using var hook = PausePreparation(events, gate, presentation);
        var session = new Session();
        var binding = Inventory(scope, events: events);
        var preparing = binding.SendLoginAsync(session, [Row()], default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = binding.SendLoginAsync(session, [Row()], default).AsTask();
        scope.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.EnterAsync(new Connection(2), default).AsTask());
        gate.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Empty(session.Sent);
        scope.Dispose();
    }

    /// <summary>A failed frame retires the burst before later work, preserving its original error even when close also fails.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task SendFailureRetiresTheEpochAndPreservesTheOriginalException(int publishedPrefix, bool closeFails)
    {
        using var scope = new InventoryBaselineScope(1, new Clock(Origin), 2);
        var session = new Session();
        session.Transport.CloseFailure = closeFails ? new IOException("Close failed.") : null;
        var binding = Inventory(scope);
        var failure = new IOException("Frame admission failed.");
        var gate = new Pause();
        session.BeforeSend = async (attempt, _) =>
        {
            if (attempt != publishedPrefix + 1) return;
            await gate.Wait();
            throw failure;
        };
        var failed = binding.SendAsync(session, new InventoryPurchaseApplied([Row()]), default).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = binding.SendLoginAsync(session, [Row()], default).AsTask();
        gate.Resume();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failed));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(publishedPrefix, session.Sent.Count);
        Assert.Equal(1, session.Transport.CloseCalls);
        Assert.Equal(closeFails, failure.Data.Contains("InventoryPublicationCleanupFailure"));
        Assert.Null(scope.Find(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binding.SendLoginAsync(session, [Row()], default).AsTask());
        using var other = await scope.EnterAsync(new Connection(2), default);
    }

    /// <summary>Cancellation after a published prefix closes the connection and prevents a queued full list from following it.</summary>
    [Fact]
    public async Task CancellationAfterAPrefixRetiresTheConnection()
    {
        using var scope = new InventoryBaselineScope(1, new Clock(Origin), 2);
        var session = new Session();
        var binding = Inventory(scope);
        var gate = new Pause();
        using var stop = new CancellationTokenSource();
        session.BeforeSend = (attempt, ct) => attempt == 2 ? gate.Wait(ct) : Task.CompletedTask;
        var active = binding.SendAsync(session, new InventoryPurchaseApplied([Row()]), stop.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = binding.SendLoginAsync(session, [Row()], default).AsTask();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(session.Sent);
        Assert.True(session.Connection.IsClosed);
        Assert.Null(scope.Find(1));
    }

    /// <summary>Deterministic encoding failure releases admission without closing the connection or replacing its standing origin.</summary>
    [Fact]
    public async Task EncodingFailureKeepsTheConnectionAndItsOriginUsable()
    {
        var clock = new Clock(DateTimeOffset.MinValue.AddDays(1));
        using var scope = new InventoryBaselineScope(1, clock, 1);
        var session = new Session();
        await Seed(scope, session, DateTimeOffset.MinValue);
        var row = Row() with { Lifetime = InventoryLifetime.Until(clock.Now.AddDays(2)) };
        var invalid = row with { ItemId = 202002, Bag = InventoryBag.EquippedBag, Lifetime = InventoryLifetime.Until(DateTimeOffset.MaxValue) };
        var binding = Inventory(scope);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => binding.SendAsync(session, new InventoryReplaced([row], [invalid]), default).AsTask());
        Assert.Empty(session.Sent);
        Assert.Equal(0, session.Transport.CloseCalls);
        Assert.Equal(DateTimeOffset.MinValue, scope.Require(1));
        await binding.SendLoginAsync(session, [row], default);
        Assert.Single(session.Sent);
        Assert.Equal(clock.Now, scope.Require(1));
    }

    /// <summary>A closed connection is refused before reserving any entry capacity.</summary>
    [Fact]
    public async Task ClosedConnectionCannotConsumeAnEntry()
    {
        using var scope = new InventoryBaselineScope(1, new Clock(Origin));
        var closed = new Connection(1);
        await closed.CloseAsync("Already closed.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.EnterAsync(closed, default).AsTask());
        using var next = await scope.EnterAsync(new Connection(2), default);
    }

    /// <summary>The plugin row validates the complete running-plus-pending limit, including both supported endpoints.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void PublicationLimitIsAValidatedRowOption(int limit, bool valid)
    {
        var options = new MessagesOptions { MaxInventoryPublicationsPerConnection = limit };
        var results = new List<ValidationResult>();
        Assert.Equal(valid, Validator.TryValidateProperty(limit,
            new ValidationContext(options) { MemberName = nameof(MessagesOptions.MaxInventoryPublicationsPerConnection) }, results));
        Assert.Equal(8, new MessagesOptions().MaxInventoryPublicationsPerConnection);
    }

    private static IDisposable PausePreparation(EventBus events, Pause gate, bool presentation) => presentation
        ? events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(async (_, _, _) =>
        {
            await gate.Wait();
            return InventoryItemPresentation.Empty;
        })
        : events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>(async (_, _, _) =>
        {
            await gate.Wait();
            return null;
        });

    private static async Task Seed(InventoryBaselineScope scope, Session session, DateTimeOffset origin)
    {
        using var publication = await scope.EnterAsync(session.Connection, default);
        publication.Reset(origin);
    }

    private static InventoryBinding Inventory(InventoryBaselineScope scope, string build = "kr-1256", EventBus? events = null) =>
        new(build + "/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build, scope, events);

    private static InventorySlotRecord Row() => new(42, InventoryBag.CharacterBag, 7, 202001, 1, 0)
    { Lifetime = InventoryLifetime.Until(Origin.AddDays(10)) };

    private static double Days((ushort Opcode, byte[] Payload) frame, string build)
    {
        var body = frame.Payload;
        if (build == "kr-1256")
        {
            using var input = new MemoryStream(body, 4, body.Length - 4);
            using var compressed = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            compressed.CopyTo(output);
            body = output.ToArray();
        }
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body));
        return BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(12));
    }

    private sealed class Pause
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task Wait(CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Released.Task.WaitAsync(ct);
        }
        internal void Resume() => Released.TrySetResult();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        /// <summary>Returns the instant selected by the explicit publication schedule.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Session(long id = 1, string build = "kr-1256") : ISession
    {
        private int _attempts;
        internal Connection Transport { get; } = new(id);
        internal ConcurrentQueue<(ushort Opcode, byte[] Payload)> Sent { get; } = new();
        internal Func<int, CancellationToken, Task>? BeforeSend { get; set; }
        /// <summary>Gets the recording session's identity.</summary>
        public long Id => id;
        /// <summary>Gets the connection whose lease guards this recorder.</summary>
        public IConnection Connection => Transport;
        /// <summary>Gets the native profile selected for these bindings.</summary>
        public ClientProfile Profile { get; } = TestBuilds.Opcodes(build).Profile;
        /// <summary>Gets session-local state used by publication owner checks.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Runs the explicit pause or failure before recording one cancellable frame admission.</summary>
        public async ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (BeforeSend is { } before) await before(Interlocked.Increment(ref _attempts), ct);
            ct.ThrowIfCancellationRequested();
            if (Connection.IsClosed) throw new IOException("Recording connection is closed.");
            Sent.Enqueue((opcode, payload.ToArray()));
        }
    }

    private sealed class Connection(long id) : IConnection
    {
        internal Exception? CloseFailure { get; set; }
        internal int CloseCalls { get; private set; }
        /// <summary>Gets the unique connection key for this fixture.</summary>
        public long Id => id;
        /// <summary>Gets a loopback endpoint without opening a socket.</summary>
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects reads because this fixture only records publications.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects raw writes because the session owns frame admission.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Retains the bounded connection-local retirement marker.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Gets whether a successful fixture close has occurred.</summary>
        public bool IsClosed { get; private set; }
        /// <summary>Records closure or throws the explicitly selected cleanup failure.</summary>
        public ValueTask CloseAsync(string reason)
        {
            CloseCalls++;
            if (CloseFailure is not null) throw CloseFailure;
            IsClosed = true;
            return ValueTask.CompletedTask;
        }
    }
}
