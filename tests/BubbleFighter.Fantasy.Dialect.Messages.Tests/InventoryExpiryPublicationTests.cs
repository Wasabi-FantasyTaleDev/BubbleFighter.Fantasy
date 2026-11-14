using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins ordinary ownership deadlines against the origin installed by each native full inventory list.</summary>
public sealed class InventoryExpiryPublicationTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Admission = "ordinary_expiry_builds: [kr-1256, cn-774, tw-454]";

    /// <summary>Fractional, expired and long ownership terms retain their exact double bits and stored ordinary fields.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task OrdinaryExpiryUsesSignedDayBitsWithoutChangingOwnership(string build)
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session();
        double[] days = [1.25, 0, -0.5, 10001.25];
        var rows = days.Select((value, index) => Row(index + 202001) with
        {
            Lifetime = InventoryLifetime.Until(Origin.AddDays(value)), Quantity = index + 2, Flags = 0x81,
        }).ToArray();
        var original = rows.ToArray();

        await Binding(build, scope).SendLoginAsync(session, rows, default);

        var body = OrdinaryBody(build, Assert.Single(session.Sent).Payload);
        Assert.Equal(4 + rows.Length * 25, body.Length);
        var reader = new PacketReader(body);
        Assert.Equal((uint)rows.Length, reader.ReadUInt32());
        for (var index = 0; index < rows.Length; index++)
            AssertSlot(ref reader, rows[index], BitConverter.DoubleToInt64Bits(days[index]));
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(Convert.FromHexString("000000000000E0BF"), body[(4 + 2 * 25 + 8)..(4 + 2 * 25 + 16)]);
        Assert.Equal(original, rows);
        Assert.Equal(Origin, scope.Require(session.Connection.Id));
    }

    /// <summary>Full bags re-anchor both purchase frames, while later pure updates retain that published origin.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task FullRestatementsAndPurchasePairsResetButDeltasRetainTheOrigin(string build)
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var binding = Binding(build, scope);
        var session = new Session();
        var row = Row() with { Lifetime = InventoryLifetime.Until(Origin.AddDays(7)) };
        await binding.SendLoginAsync(session, [row], default);
        AssertOneSlot(build, Assert.Single(session.Sent), row, 7, slots: false);

        clock.Now = Origin.AddDays(0.5);
        session.Sent.Clear();
        await binding.SendAsync(session, new InventorySlotsUpdated([row]), default);
        AssertOneSlot(build, Assert.Single(session.Sent), row, 7, slots: true);
        Assert.Equal(Origin, scope.Require(session.Connection.Id));

        clock.Now = Origin.AddDays(1);
        session.Sent.Clear();
        await binding.SendAsync(session, new InventoryPurchaseApplied([row]), default);
        Assert.Equal(2, session.Sent.Count);
        AssertOneSlot(build, session.Sent[0], row, 6, slots: false);
        AssertOneSlot(build, session.Sent[1], row, 6, slots: true);
        Assert.Equal(clock.Now, scope.Require(session.Connection.Id));

        clock.Now = Origin.AddDays(2);
        session.Sent.Clear();
        await binding.SendAsync(session, new InventorySlotsUpdated([row]), default);
        AssertOneSlot(build, Assert.Single(session.Sent), row, 6, slots: true);
        Assert.Equal(Origin.AddDays(1), scope.Require(session.Connection.Id));

        clock.Now = Origin.AddDays(3);
        session.Sent.Clear();
        await binding.SendAsync(session, new InventoryReplaced([row], [row with { Bag = InventoryBag.EquippedBag }]), default);
        Assert.Equal(4, session.Sent.Count);
        AssertOneSlot(build, session.Sent[0], row, 4, slots: false);
        AssertOneSlot(build, session.Sent[1], row, 4, slots: true);
        Assert.Equal(clock.Now, scope.Require(session.Connection.Id));
    }

    /// <summary>Feature deadlines and attributes take precedence over ownership expiry across a purchase rebase.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task ExplicitFeatureDeadlineAndAttributesSurvivePurchaseRebasing(string build)
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var events = new EventBus(NullLogger.Instance);
        var row = Row() with { Lifetime = InventoryLifetime.Until(Origin.AddDays(7)), Quantity = 17, Flags = 0x81 };
        RoomItemAttribute[] attributes = [new(row.ItemId, 7, new uint[] { 2, uint.MaxValue, 17 }, 3, "")];
        using var presentation = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(
            (_, _, _) => ValueTask.FromResult(new InventoryItemPresentation(attributes, Origin.AddDays(0.75))));
        var binding = Binding(build, scope, events);
        var session = new Session();
        await binding.SendLoginAsync(session, [row], default);
        clock.Now = Origin.AddDays(0.25);
        session.Sent.Clear();

        await binding.SendAsync(session, new InventoryPurchaseApplied([row]), default);

        Assert.Equal(2, session.Sent.Count);
        AssertOneSlot(build, session.Sent[0], row, 0.5, slots: false, attributes);
        AssertOneSlot(build, session.Sent[1], row, 0.5, slots: true, attributes);
        Assert.Equal(InventoryLifetime.Until(Origin.AddDays(7)), row.Lifetime);
        Assert.Equal(Origin.AddDays(0.25), scope.Require(session.Connection.Id));
    }

    /// <summary>A complete-snapshot interceptor turns a delta into the full sequence and installs its fresh origin.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task PreparedDeltaUsesItsEffectiveFullPublicationSequence(string build)
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var events = new EventBus(NullLogger.Instance);
        var binding = Binding(build, scope, events);
        var session = new Session();
        var row = Row() with { Lifetime = InventoryLifetime.Until(Origin.AddDays(3)) };
        await binding.SendLoginAsync(session, [row], default);
        clock.Now = Origin.AddDays(0.5);
        session.Sent.Clear();
        using var preparing = events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>(
            (_, _, _) => ValueTask.FromResult<InventoryReplaced?>(new([row], [])));

        await binding.SendAsync(session, new InventorySlotsUpdated([row]), default);

        Assert.Equal(4, session.Sent.Count);
        AssertOneSlot(build, session.Sent[0], row, 2.5, slots: false);
        Assert.Equal(clock.Now, scope.Require(session.Connection.Id));
    }

    /// <summary>A replacement sequence without a full bag retains its old origin and cannot establish a new one.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task ReplacementWithoutBagRequiresAndRetainsTheStandingOrigin(string build, bool prior)
    {
        var tables = WithBuildValues("replaced: [bag, equipped, articles, cash]", "replaced: [equipped, articles, cash]");
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var binding = new InventoryBinding(build + "/game", TestBuilds.Opcodes(build), tables, build, scope);
        var session = new Session();
        var row = Row() with { Lifetime = InventoryLifetime.Until(Origin.AddDays(3)) };
        if (prior) await binding.SendLoginAsync(session, [row], default);
        session.Sent.Clear();
        clock.Now = Origin.AddDays(1);
        var message = new InventoryReplaced([], [row with { Bag = InventoryBag.EquippedBag }]);

        if (prior)
        {
            await binding.SendAsync(session, message, default);
            Assert.Equal(3, session.Sent.Count);
            AssertOneSlot(build, session.Sent[0], row, 3, slots: true);
            Assert.Equal(Origin, scope.Require(session.Connection.Id));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => binding.SendAsync(session, message, default).AsTask());
            Assert.Empty(session.Sent);
            Assert.Null(scope.Find(session.Connection.Id));
        }
    }

    /// <summary>The full-list clock is sampled after awaited presentation work, without sending while it is blocked.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task DelayedPresentationFinishesBeforeTheOriginIsSampled(string build)
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var events = new EventBus(NullLogger.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var presentation = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return InventoryItemPresentation.Empty;
        });
        var session = new Session();
        var row = Row() with { Lifetime = InventoryLifetime.Until(Origin.AddDays(2)) };
        var publication = Binding(build, scope, events).SendLoginAsync(session, [row], default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(publication.IsCompleted);
        Assert.Empty(session.Sent);
        Assert.Null(scope.Find(session.Connection.Id));
        clock.Now = Origin.AddDays(0.25);
        release.TrySetResult();
        await publication.WaitAsync(TimeSpan.FromSeconds(10));

        AssertOneSlot(build, Assert.Single(session.Sent), row, 1.75, slots: false);
        Assert.Equal(clock.Now, scope.Require(session.Connection.Id));
    }

    /// <summary>Timed pure updates cannot invent an origin before the first full list, with or without a clock scope.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task TimedDeltaBeforeFullInventoryFailsBeforeSending(string build)
    {
        using var scope = new InventoryBaselineScope(2, new Clock(Origin));
        var row = Row() with { Lifetime = InventoryLifetime.Until(Origin.AddDays(1)) };
        foreach (var binding in new[] { Binding(build, scope), Binding(build, null) })
        {
            var session = new Session();
            await Assert.ThrowsAsync<InvalidOperationException>(() => binding
                .SendAsync(session, new InventorySlotsUpdated([row]), default).AsTask());
            Assert.Empty(session.Sent);
        }
        Assert.Null(scope.Find(1));
    }

    /// <summary>Permanent, unspecified and life-counted ordinary rows preserve their literal fields without a shared clock.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task TimelessRowsDoNotAcquireSyntheticDurationValues(string build)
    {
        InventoryLifetime[] lifetimes = [default, InventoryLifetime.Permanent, InventoryLifetime.Life];
        var rows = lifetimes.Select((term, index) => Row(202001 + index) with { Lifetime = term }).ToArray();
        var session = new Session();
        await Binding(build, null).SendLoginAsync(session, rows, default);
        var reader = new PacketReader(OrdinaryBody(build, Assert.Single(session.Sent).Payload));
        Assert.Equal((uint)rows.Length, reader.ReadUInt32());
        foreach (var row in rows) AssertSlot(ref reader, row, row.SlotKey);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>An invalid later equipped timer cannot send the earlier bag or replace its previous standing origin.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task LaterFrameEncodingFailureDoesNotPublishOrCommitThePreparedOrigin(string build, bool prior)
    {
        var clock = new Clock(DateTimeOffset.MinValue);
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session();
        if (prior)
            using (var publication = await scope.EnterAsync(session.Connection, default))
                publication.Reset(publication.PrepareReset());
        clock.Now = DateTimeOffset.MinValue.AddDays(1);
        var row = Row() with { Lifetime = InventoryLifetime.Until(clock.Now.AddDays(2)) };
        var invalid = Row(202002) with
        {
            Bag = InventoryBag.EquippedBag, Lifetime = InventoryLifetime.Until(DateTimeOffset.MaxValue),
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Binding(build, scope)
            .SendAsync(session, new InventoryReplaced([row], [invalid]), default).AsTask());

        Assert.Empty(session.Sent);
        Assert.Equal(prior ? DateTimeOffset.MinValue : (DateTimeOffset?)null, scope.Find(session.Connection.Id));
    }

    /// <summary>A prepared snapshot over the bag bound is rejected before per-row presentation or origin changes.</summary>
    [Fact]
    public async Task OversizedPreparedSnapshotCannotAdvanceTheOrigin()
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var events = new EventBus(NullLogger.Instance);
        var rows = Enumerable.Repeat(Row(), InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray();
        using var prepare = events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>(
            (_, _, _) => ValueTask.FromResult<InventoryReplaced?>(new(rows, [])));
        var presented = 0;
        using var presentation = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((_, _, _) =>
        {
            presented++;
            return ValueTask.FromResult(InventoryItemPresentation.Empty);
        });
        var session = new Session();

        await Assert.ThrowsAsync<InvalidDataException>(() => Binding("kr-1256", scope, events)
            .SendLoginAsync(session, [], default).AsTask());

        Assert.Empty(session.Sent);
        Assert.Equal(0, presented);
        Assert.Null(scope.Find(session.Connection.Id));
    }

    /// <summary>Cancellation during preparation does not install an unsent origin or publish an inventory prefix.</summary>
    [Fact]
    public async Task CancelledPresentationLeavesTheExistingOriginUntouched()
    {
        var clock = new Clock(Origin);
        using var scope = new InventoryBaselineScope(2, clock);
        var session = new Session();
        using (var publication = await scope.EnterAsync(session.Connection, default))
            publication.Reset(publication.PrepareReset());
        clock.Now = Origin.AddDays(1);
        using var stop = new CancellationTokenSource();
        var events = new EventBus(NullLogger.Instance);
        using var presentation = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((_, _, _) =>
        {
            stop.Cancel();
            return ValueTask.FromResult(InventoryItemPresentation.Empty);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Binding("kr-1256", scope, events)
            .SendLoginAsync(session, [Row()], stop.Token).AsTask());
        Assert.Empty(session.Sent);
        Assert.Equal(Origin, scope.Require(1));
    }

    /// <summary>The ordinary fallback leaves admitted timed Articles on their existing timer-free Article wire representation.</summary>
    [Fact]
    public async Task TimedArticleOwnershipDoesNotCreateAnOrdinaryDuplicateOrExpiryAttribute()
    {
        using var scope = new InventoryBaselineScope(2, new Clock(Origin));
        var row = Row() with { ArticleIdentity = new(7, 100000007), Lifetime = InventoryLifetime.Until(Origin.AddDays(1)) };
        var timed = new Session();
        var permanent = new Session(2);
        var binding = Binding("kr-1256", scope);
        await binding.SendAsync(timed, new InventoryReplaced([row], []) { IsCompleteSnapshot = true }, default);
        await binding.SendAsync(permanent,
            new InventoryReplaced([row with { Lifetime = InventoryLifetime.Permanent }], []) { IsCompleteSnapshot = true }, default);

        Assert.Equal(4, timed.Sent.Count);
        Assert.Equal(Convert.FromHexString("00000000"), OrdinaryBody("kr-1256", timed.Sent[0].Payload));
        for (var index = 0; index < timed.Sent.Count; index++)
            Assert.Equal(permanent.Sent[index].Payload, timed.Sent[index].Payload);
        var article = OrdinaryBody("kr-1256", timed.Sent[2].Payload);
        Assert.Equal(28, article.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(article));
        Assert.Equal(100000007L, BinaryPrimitives.ReadInt64LittleEndian(article.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(article.AsSpan(24)));
    }

    /// <summary>Only the individually named modern builds project ordinary durable deadlines.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("kr-812", false)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    public void OrdinaryExpiryAdmissionIsBuildQualified(string build, bool admitted) =>
        Assert.Equal(admitted, InventoryValues.Of(TestBuilds.Packets, build).OrdinaryExpiryAdmitted);

    /// <summary>Table inheritance alone cannot add the expiry projection to an unlisted descendant.</summary>
    [Fact]
    public async Task UnlistedDescendantKeepsItsPriorLiteralOrdinaryFields()
    {
        var tables = WithBuildValues(Admission, "ordinary_expiry_builds: [kr-1256, cn-774]");
        Assert.True(InventoryValues.Of(tables, "cn-774").OrdinaryExpiryAdmitted);
        Assert.False(InventoryValues.Of(tables, "tw-454").OrdinaryExpiryAdmitted);
        var session = new Session();
        var row = Row() with { Lifetime = InventoryLifetime.Until(Origin.AddDays(1)) };
        await new InventoryBinding("tw-454/game", TestBuilds.Opcodes("tw-454"), tables, "tw-454")
            .SendLoginAsync(session, [row], default);
        var reader = new PacketReader(Assert.Single(session.Sent).Payload);
        Assert.Equal(1u, reader.ReadUInt32());
        AssertSlot(ref reader, row, row.SlotKey);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Missing and empty admission lists conservatively preserve ordinary projection for all modern descendants.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ordinary_expiry_builds: []")]
    public void MissingOrEmptyExpiryAdmissionDoesNotEnableDescendants(string replacement)
    {
        var tables = WithBuildValues(Admission, replacement);
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
            Assert.False(InventoryValues.Of(tables, build).OrdinaryExpiryAdmitted);
    }

    /// <summary>Malformed membership lists fail at activation rather than silently admitting another build.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("kr-1256")]
    [InlineData("[kr-1256, kr-1256]")]
    [InlineData("[unknown-build]")]
    [InlineData("[KR-1256]")]
    [InlineData("null")]
    [InlineData("{}")]
    public void InvalidExpiryAdmissionFailsBeforePublication(string value)
    {
        var tables = WithBuildValues(Admission, "ordinary_expiry_builds: " + value);
        var error = Assert.Throws<ConfigException>(() => InventoryValues.Of(tables, "kr-1256"));
        Assert.Contains("ordinary_expiry_builds", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The modern expiry field cannot be enabled on a ranked row projection.</summary>
    [Fact]
    public void ExpiryAdmissionRequiresMirroredRows()
    {
        var tables = WithBuildValues("rows: mirror", "rows: ranked");
        var error = Assert.Throws<ConfigException>(() => InventoryValues.Of(tables, "kr-1256"));
        Assert.Contains("requires mirrored inventory rows", error.Message, StringComparison.Ordinal);
    }

    private static InventorySlotRecord Row(int item = 202001) =>
        new(42, InventoryBag.CharacterBag, 0x1122334455667788L, item, 5, 0)
        { Lifetime = InventoryLifetime.Permanent };

    private static InventoryBinding Binding(string build, InventoryBaselineScope? scope, EventBus? events = null) =>
        new(build + "/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build, scope, events);

    private static void AssertOneSlot(string build, (ushort Opcode, byte[] Payload) frame,
        InventorySlotRecord row, double days, bool slots, IReadOnlyList<RoomItemAttribute>? attributes = null)
    {
        Assert.Equal(TestBuilds.Opcodes(build).S2C(slots ? "gspInventoryUpdateInventorySlots" : "gspInventoryUpdateAllInventorySlot"), frame.Opcode);
        var body = OrdinaryBody(build, frame.Payload);
        var expectedTail = slots && build == "kr-1256" ? 8 : 0;
        var attributeBytes = attributes?.Sum(attribute => 20 + attribute.Values.Count * 4 + attribute.Unknown32.Length * 2) ?? 0;
        Assert.Equal(29 + expectedTail + attributeBytes, body.Length);
        var reader = new PacketReader(body);
        Assert.Equal(1u, reader.ReadUInt32());
        AssertSlot(ref reader, row, BitConverter.DoubleToInt64Bits(days), attributes);
        if (expectedTail != 0)
        {
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
        }
        Assert.Equal(0, reader.Remaining);
    }

    private static void AssertSlot(ref PacketReader reader, InventorySlotRecord row, long durationBits,
        IReadOnlyList<RoomItemAttribute>? attributes = null)
    {
        Assert.Equal(row.ItemId, reader.ReadInt32());
        Assert.Equal(row.Quantity, reader.ReadInt32());
        Assert.Equal(durationBits, reader.ReadInt64());
        Assert.Equal("", reader.ReadWideStringPrefixed());
        Assert.Equal((uint)(attributes?.Count ?? 0), reader.ReadUInt32());
        foreach (var attribute in attributes ?? [])
        {
            Assert.Equal(attribute.ItemId, reader.ReadInt32());
            Assert.Equal(attribute.Kind, reader.ReadUInt32());
            Assert.Equal((uint)attribute.Values.Count, reader.ReadUInt32());
            foreach (var value in attribute.Values) Assert.Equal(value, reader.ReadUInt32());
            Assert.Equal(attribute.Unknown28, reader.ReadUInt32());
            Assert.Equal(attribute.Unknown32, reader.ReadWideStringPrefixed());
        }
        Assert.Equal(row.Flags, reader.ReadByte());
    }

    private static byte[] OrdinaryBody(string build, byte[] payload)
    {
        if (build != "kr-1256") return payload;
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload);
        Assert.Equal(payload.Length - 4, length);
        using var source = new MemoryStream(payload, 4, length);
        using var compressed = new ZLibStream(source, CompressionMode.Decompress);
        using var result = new MemoryStream();
        compressed.CopyTo(result);
        return result.ToArray();
    }

    private static PacketTables WithBuildValues(string before, string after)
    {
        var directory = TestTempDirectories.ForTest("bff-inventory-expiry");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var path = Path.Combine(directory, "builds.yaml");
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(before, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(before, after, StringComparison.Ordinal));
        return PacketTables.Load(directory);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        /// <summary>Returns the instant selected by the publication-order fixture.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Session(long connectionId = 1) : ISession
    {
        /// <summary>Gets the fixed identity of the recording session.</summary>
        public long Id => 1;
        /// <summary>Gets the connection whose inventory origin the fixture tracks.</summary>
        public IConnection Connection { get; } = new Connection(connectionId);
        /// <summary>Gets the fixture profile; each binding supplies its independently selected build tables.</summary>
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "kr-1256");
        /// <summary>Gets session-local storage available to publication interceptors.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        /// <summary>Records an owned payload after checking publication cancellation.</summary>
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connection(long id) : IConnection
    {
        /// <summary>Gets the single connection key used by the fixture's bounded origin scope.</summary>
        public long Id => id;
        /// <summary>Gets a loopback endpoint without opening a socket.</summary>
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects input access because these tests only publish outbound frames.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects raw output access because the recording session owns publication.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Gets isolated connection metadata for the recording fixture.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Gets the open state used throughout ordinary publication tests.</summary>
        public bool IsClosed => false;
        /// <summary>Completes fixture cleanup without opening or closing a real transport.</summary>
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
