using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete native Article ownership, preparation boundaries and ordinary-row exclusion.</summary>
public sealed class InventoryArticlePublicationTests
{
    /// <summary>Mirrored ownership installs one Article with all four signed attribute words at both native key endpoints.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task CompleteOwnershipPublishesOneArticlePerEntitlement(string build)
    {
        var events = new EventBus(NullLogger.Instance);
        using var presentation = Present(events);
        var first = Pet(301001, 1, PetArticleKeys.First, worn: true);
        var last = Pet(301002, 2, PetArticleKeys.Last, worn: false);
        var ordinary = Ordinary(202001);
        var session = new Session();
        await Binding(build, events).SendAsync(session, new InventoryReplaced([first, ordinary, last],
            [Mirror(first) with { Flags = 0x81 }, Mirror(last), Mirror(ordinary)]) { IsCompleteSnapshot = true }, default);

        AssertReplacement(build, session);
        Assert.Equal(new[] { ordinary.ItemId }, OrdinaryIds(Body(build, session.Sent[0])));
        Assert.Equal(new[] { ordinary.ItemId }, OrdinaryIds(Body(build, session.Sent[1]), build == "kr-1256"));
        AssertArticles(build, session.Sent[2], [first, last]);
    }

    /// <summary>Shared ordinary identities use the Article table only on the exact admitted build, while pets remain Articles.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    public async Task StoredOrdinaryArticlesRequireExactBuildAdmission(string build, bool admitted)
    {
        var events = new EventBus(NullLogger.Instance);
        using var presentation = Present(events);
        var equipment = EquipmentArticle();
        var pet = Pet(301001, 1, 100000001, true);
        var plain = Ordinary(202002);
        var session = new Session();
        await Binding(build, events).SendAsync(session, new InventoryReplaced([equipment, pet, plain],
            [Mirror(equipment), Mirror(pet), Mirror(plain)]) { IsCompleteSnapshot = true }, default);

        AssertReplacement(build, session);
        var ordinaryIds = admitted ? new[] { plain.ItemId } : new[] { equipment.ItemId, plain.ItemId };
        Assert.Equal(ordinaryIds, OrdinaryIds(Body(build, session.Sent[0])));
        Assert.Equal(ordinaryIds, OrdinaryIds(Body(build, session.Sent[1]), build == "kr-1256"));
        AssertArticles(build, session.Sent[2], admitted ? [equipment, pet] : [pet]);
    }

    /// <summary>A pet keeps its Article representation when matching ordinary compatibility metadata is also stored.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task PetCompatibilityIdentityRemainsAnArticleWithoutOrdinaryAdmission(string build)
    {
        var events = new EventBus(NullLogger.Instance);
        using var presentation = Present(events);
        var pet = Pet(301001, 1, 100000001, true) with { ArticleIdentity = new(1, 100000001) };
        var session = new Session();
        await Binding(build, events).SendAsync(session,
            new InventoryReplaced([pet], [Mirror(pet)]) { IsCompleteSnapshot = true }, default);
        AssertReplacement(build, session);
        Assert.Empty(OrdinaryIds(Body(build, session.Sent[0])));
        Assert.Empty(OrdinaryIds(Body(build, session.Sent[1])));
        AssertArticles(build, session.Sent[2], [pet]);
    }

    /// <summary>Non-admitted builds preserve ordinary bytes for saved identities through every inventory publication entry point.</summary>
    [Theory]
    [InlineData("cn-774", "login")]
    [InlineData("tw-454", "login")]
    [InlineData("cn-774", "complete")]
    [InlineData("tw-454", "complete")]
    [InlineData("cn-774", "replacement")]
    [InlineData("tw-454", "replacement")]
    [InlineData("cn-774", "purchase")]
    [InlineData("tw-454", "purchase")]
    [InlineData("cn-774", "delta")]
    [InlineData("tw-454", "delta")]
    public async Task UnadmittedOrdinaryIdentityPreservesItemPublication(string build, string publication)
    {
        var events = new EventBus(NullLogger.Instance);
        var identified = new Session();
        var ordinary = new Session();
        var row = EquipmentArticle() with { Quantity = 9, SlotKey = 0x1122334455667788L };
        await Publish(publication, Binding(build, events), identified, row);
        await Publish(publication, Binding(build, events), ordinary, row with { ArticleIdentity = null });
        Assert.NotEmpty(identified.Sent);
        Assert.Equal(ordinary.Sent.Select(frame => frame.Opcode), identified.Sent.Select(frame => frame.Opcode));
        Assert.Equal(ordinary.Sent.Count, identified.Sent.Count);
        for (var index = 0; index < ordinary.Sent.Count; index++)
            Assert.Equal(ordinary.Sent[index].Payload, identified.Sent[index].Payload);
    }

    /// <summary>The admitted build leaves ordinary Articles out of its initial bag until the existing full-table installation point.</summary>
    [Fact]
    public async Task AdmittedOrdinaryLoginPreservesTheArticleInstallationBoundary()
    {
        var equipment = EquipmentArticle();
        var pet = Pet(301001, 1, 100000001, true);
        var ordinary = Ordinary(202002);
        var session = new Session();
        await Binding("kr-1256").SendLoginAsync(session, [equipment, pet, ordinary], default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal(TestBuilds.Opcodes("kr-1256").S2C("gspInventoryUpdateAllInventorySlot"), frame.Opcode);
        Assert.Equal(new[] { ordinary.ItemId }, OrdinaryIds(Body("kr-1256", frame)));
    }

    /// <summary>Actual Article publications still require complete preparation for replacements, purchases and deltas.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task UnpreparedAdmittedArticlesFailBeforeAnyPacket(string build, bool pet)
    {
        var row = pet ? Pet(301001, 1, 100000001, true) : EquipmentArticle();
        foreach (var publication in new[] { "replacement", "purchase", "delta" })
        {
            var session = new Session();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(publication, Binding(build), session, row).AsTask());
            Assert.Empty(session.Sent);
        }
    }

    /// <summary>Choosing ordinary Item representation never conceals invalid durable identities or inconsistent equipped mirrors.</summary>
    [Theory]
    [InlineData("cn-774", "invalid-key")]
    [InlineData("tw-454", "invalid-key")]
    [InlineData("cn-774", "conflicting-pet")]
    [InlineData("tw-454", "conflicting-pet")]
    [InlineData("cn-774", "duplicate-key")]
    [InlineData("tw-454", "duplicate-key")]
    [InlineData("cn-774", "orphan")]
    [InlineData("tw-454", "orphan")]
    [InlineData("cn-774", "missing-identity")]
    [InlineData("tw-454", "missing-identity")]
    [InlineData("cn-774", "wrong-wear")]
    [InlineData("tw-454", "wrong-wear")]
    [InlineData("cn-774", "pet-mirror-domain")]
    [InlineData("tw-454", "pet-mirror-domain")]
    [InlineData("cn-774", "ordinary-mirror-domain")]
    [InlineData("tw-454", "ordinary-mirror-domain")]
    public async Task UnadmittedOrdinaryMetadataStillRequiresValidOwnership(string build, string conflict)
    {
        var equipment = EquipmentArticle();
        var identity = Assert.IsType<InventoryArticleIdentity>(equipment.ArticleIdentity);
        InventorySlotRecord[] owned = [equipment];
        InventorySlotRecord[] mirrors = [Mirror(equipment)];
        switch (conflict)
        {
            case "invalid-key": owned = [equipment with { ArticleIdentity = new(7, ulong.MaxValue) }]; break;
            case "conflicting-pet": owned = [equipment with { PetIdentity = new(8, 100000008) }]; break;
            case "duplicate-key": owned = [equipment, Ordinary(202002) with { ArticleIdentity = new(8, equipment.ArticleIdentity!.ArticleKey) }]; break;
            case "orphan": owned = []; break;
            case "missing-identity": mirrors = [Mirror(equipment) with { ArticleIdentity = null }]; break;
            case "wrong-wear": mirrors = [Mirror(equipment) with { Flags = 0 }]; break;
            case "pet-mirror-domain":
                mirrors = [Mirror(equipment) with { PetIdentity = new(identity.EntitlementId, identity.ArticleKey) }];
                break;
            case "ordinary-mirror-domain":
                owned = [equipment with { PetIdentity = new(identity.EntitlementId, identity.ArticleKey) }];
                break;
        }
        var session = new Session();
        await Assert.ThrowsAsync<InvalidDataException>(() => Binding(build).SendAsync(session,
            new InventoryReplaced(owned, mirrors) { IsCompleteSnapshot = true }, default).AsTask());
        Assert.Empty(session.Sent);
    }

    /// <summary>A prepared partial mutation restates the complete Article table and removes obsolete ownership.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task PreparedChangesReplaceAllArticlesAndRemoveEmptyRows(string build, bool purchase)
    {
        var events = new EventBus(NullLogger.Instance);
        using var presentation = Present(events);
        var changed = Pet(301001, 1, 100000001, true);
        var retained = Pet(301002, 2, 100000002, false);
        var ordinary = Ordinary(202001);
        var exhausted = Ordinary(100644) with { Quantity = 0 };
        var session = new Session();
        var calls = new List<InventoryPublicationPreparing>();
        InventoryReplaced authoritative = new([changed, retained, ordinary, exhausted], [Mirror(changed)]);
        using var preparing = events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>((request, _, _) =>
        {
            calls.Add(request);
            return ValueTask.FromResult<InventoryReplaced?>(authoritative);
        });
        var binding = Binding(build, events);
        if (purchase) await binding.SendAsync(session, new InventoryPurchaseApplied([changed]), default);
        else await binding.SendAsync(session, new InventorySlotsUpdated([changed]), default);

        Assert.False(Assert.Single(calls).IsComplete);
        Assert.Equal(42, calls[0].UserNum);
        AssertReplacement(build, session);
        AssertArticles(build, session.Sent[2], [changed, retained]);
        Assert.Equal(new[] { ordinary.ItemId }, OrdinaryIds(Body(build, session.Sent[0])));

        session.Sent.Clear();
        authoritative = new([retained, ordinary], []);
        await binding.SendAsync(session, new InventorySlotsUpdated([changed with { Quantity = 0 }]), default);
        AssertReplacement(build, session);
        AssertArticles(build, session.Sent[2], [retained]);
        Assert.Empty(OrdinaryIds(Body(build, session.Sent[1]), build == "kr-1256"));

        session.Sent.Clear();
        authoritative = new([ordinary], []);
        await binding.SendAsync(session, new InventorySlotsUpdated([]), default);
        AssertArticles(build, session.Sent[2], []);
    }

    /// <summary>Bag-only login preparation does not move the full Article installation earlier in the burst.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task LoginBagPreparationPreservesTheInstallationBoundary(string build)
    {
        var events = new EventBus(NullLogger.Instance);
        var pet = Pet(301001, 1, 100000001, true);
        var ordinary = Ordinary(202001);
        using var preparation = events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>((request, _, _) =>
        {
            Assert.False(request.IsComplete);
            Assert.Null(request.Changes);
            return ValueTask.FromResult<InventoryReplaced?>(new([pet, ordinary], [Mirror(pet)]));
        });
        using var presentation = Present(events);
        var session = new Session();
        await Binding(build, events).SendLoginAsync(session, [pet with { PetIdentity = null }], default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal(TestBuilds.Opcodes(build).S2C("gspInventoryUpdateAllInventorySlot"), frame.Opcode);
        Assert.Equal(new[] { ordinary.ItemId }, OrdinaryIds(Body(build, frame)));
    }

    /// <summary>Only an explicitly complete replacement carries the complete-snapshot promise to the feature.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementForwardsItsSnapshotCompleteness(bool complete)
    {
        var events = new EventBus(NullLogger.Instance);
        using var preparation = events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>((request, _, _) =>
        {
            Assert.Equal(complete, request.IsComplete);
            Assert.Equal(42, request.UserNum);
            return ValueTask.FromResult<InventoryReplaced?>(new([], []));
        });
        await Binding("kr-1256", events).SendAsync(new Session(),
            new InventoryReplaced([], []) { IsCompleteSnapshot = complete }, default);
    }

    /// <summary>Full unsigned storage is not silently narrowed into an ordinary item or published as a usable Article.</summary>
    [Theory]
    [InlineData("kr-1256", 0x8000000000000000UL)]
    [InlineData("cn-774", 0x80000000000497D9UL)]
    [InlineData("tw-454", ulong.MaxValue)]
    [InlineData("kr-1256", 99999999UL)]
    public async Task UnaddressableArticleKeysFailBeforeAnyPacket(string build, ulong key)
    {
        var pet = Pet(301001, 1, key, false);
        var session = new Session();
        await Assert.ThrowsAsync<InvalidDataException>(() => Binding(build).SendAsync(session,
            new InventoryReplaced([pet], []) { IsCompleteSnapshot = true }, default).AsTask());
        Assert.Empty(session.Sent);
        Assert.Equal(key, pet.PetIdentity!.ArticleKey);
    }

    /// <summary>Conflicting ownership cannot be hidden by deduplicating the full Article list.</summary>
    [Theory]
    [InlineData("duplicate-owner")]
    [InlineData("duplicate-key")]
    [InlineData("orphan")]
    [InlineData("missing-identity")]
    [InlineData("wrong-wear")]
    public async Task AmbiguousArticleMirrorsFailBeforeAnyPacket(string conflict)
    {
        var pet = Pet(301001, 1, 100000001, true);
        InventorySlotRecord[] owned = [pet];
        InventorySlotRecord[] mirrors = [Mirror(pet)];
        switch (conflict)
        {
            case "duplicate-owner": owned = [pet, Pet(301002, 1, 100000002, false)]; break;
            case "duplicate-key": owned = [pet, Pet(301002, 2, 100000001, false)]; break;
            case "orphan": owned = []; break;
            case "missing-identity": mirrors = [Mirror(pet) with { PetIdentity = null }]; break;
            case "wrong-wear": mirrors = [Mirror(pet) with { Flags = 0 }]; break;
        }
        var session = new Session();
        await Assert.ThrowsAsync<InvalidDataException>(() => Binding("kr-1256").SendAsync(session,
            new InventoryReplaced(owned, mirrors) { IsCompleteSnapshot = true }, default).AsTask());
        Assert.Empty(session.Sent);
    }

    /// <summary>An authenticated account cannot publish another account's supplied or prepared rows.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossOwnerPublicationFailsBeforeAnyPacket(bool preparedConflict)
    {
        var events = new EventBus(NullLogger.Instance);
        var wrong = Ordinary(202001) with { UserNum = 43 };
        using var preparation = events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>((_, _, _) =>
            ValueTask.FromResult<InventoryReplaced?>(new([wrong], [])));
        var session = new Session();
        await Assert.ThrowsAsync<InvalidDataException>(() => Binding("kr-1256", events).SendAsync(session,
            new InventoryReplaced(preparedConflict ? [] : [wrong], []), default).AsTask());
        Assert.Empty(session.Sent);
    }

    /// <summary>An unhandled preparation event leaves the existing complete, purchase and delta byte sequences unchanged.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task MissingPreparationFeaturePreservesExistingPublication(string build)
    {
        var events = new EventBus(NullLogger.Instance);
        var plain = new Session(authenticated: false);
        var observed = new Session(authenticated: false);
        var first = Binding(build);
        var second = Binding(build, events);
        var rows = new[] { Ordinary(202001), Ordinary(100644) with { Quantity = 0 } };
        await first.SendAsync(plain, new InventoryReplaced([], []), default);
        await second.SendAsync(observed, new InventoryReplaced([], []), default);
        await first.SendAsync(plain, new InventoryPurchaseApplied(rows), default);
        await second.SendAsync(observed, new InventoryPurchaseApplied(rows), default);
        await first.SendAsync(plain, new InventorySlotsUpdated(rows), default);
        await second.SendAsync(observed, new InventorySlotsUpdated(rows), default);
        Assert.Equal(plain.Sent.Select(frame => frame.Opcode), observed.Sent.Select(frame => frame.Opcode));
        Assert.Equal(plain.Sent.Count, observed.Sent.Count);
        for (var index = 0; index < plain.Sent.Count; index++)
            Assert.Equal(plain.Sent[index].Payload, observed.Sent[index].Payload);
    }

    /// <summary>Legacy inventories retain ordinary pet rows even when durable Article identity is stored.</summary>
    [Fact]
    public async Task LegacyPublicationKeepsOrdinaryPetOwnership()
    {
        using var baseline = new InventoryBaselineScope(4, TimeProvider.System);
        var session = new Session();
        var binding = new InventoryBinding("kr-812/game", TestBuilds.Opcodes("kr-812"), TestBuilds.Packets,
            "kr-812", baseline);
        await binding.SendAsync(session, new InventoryReplaced([Pet(301001, 1, 100000001, true)], []), default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame.Payload));
        Assert.Equal(301001, BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(4)));
    }

    private static InventoryBinding Binding(string build, IEventBus? events = null) =>
        new(build + "/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build, null, events);

    private static InventorySlotRecord Ordinary(int item) =>
        new(42, InventoryBag.CharacterBag, item, item, 1, 0) { Lifetime = InventoryLifetime.Permanent };

    private static InventorySlotRecord EquipmentArticle() =>
        Ordinary(202001) with { ArticleIdentity = new(7, 100000007), Flags = 0x81 };

    private static ValueTask Publish(string publication, InventoryBinding binding, Session session, InventorySlotRecord row) =>
        publication switch
        {
            "login" => binding.SendLoginAsync(session, [row], default),
            "complete" => binding.SendAsync(session, new InventoryReplaced([row], [Mirror(row)]) { IsCompleteSnapshot = true }, default),
            "replacement" => binding.SendAsync(session, new InventoryReplaced([row], [Mirror(row)]), default),
            "purchase" => binding.SendAsync(session, new InventoryPurchaseApplied([row]), default),
            "delta" => binding.SendAsync(session, new InventorySlotsUpdated([row]), default),
            _ => throw new ArgumentOutOfRangeException(nameof(publication)),
        };

    private static InventorySlotRecord Pet(int item, ulong entitlement, ulong article, bool worn) =>
        Ordinary(item) with { PetIdentity = new(entitlement, article), Flags = worn ? (byte)1 : (byte)0 };

    private static InventorySlotRecord Mirror(InventorySlotRecord row) => row with { Bag = InventoryBag.EquippedBag };

    private static IDisposable Present(EventBus events) => events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(
        (request, _, _) => ValueTask.FromResult(request.Slot.PetIdentity is null ? InventoryItemPresentation.Empty :
            new InventoryItemPresentation(Attributes(request.Slot.ItemId), null)));

    private static RoomItemAttribute[] Attributes(int item) =>
    [
        new(item, 7, new uint[] { 2, 9, uint.MaxValue, 1250 }, 0, ""),
        new(item, 1, new uint[] { unchecked((uint)-5), 0, 17, uint.MaxValue }, 2, ""),
    ];

    private static void AssertReplacement(string build, Session session)
    {
        var opcodes = TestBuilds.Opcodes(build);
        Assert.Equal(new[] { opcodes.S2C("gspInventoryUpdateAllInventorySlot"), opcodes.S2C("gspInventoryUpdateInventorySlots"),
            opcodes.S2C("gspUpdateAllArticleSlot"), opcodes.S2C("gspCashInvenUpdateAllInventorySlot") },
            session.Sent.Select(frame => frame.Opcode));
    }

    private static void AssertArticles(string build, (ushort Opcode, byte[] Payload) frame, InventorySlotRecord[] expected)
    {
        var body = Body(build, frame);
        Assert.Equal(4 + expected.Sum(row => row.PetIdentity is null ? 24 : 96), body.Length);
        var reader = new PacketReader(body);
        Assert.Equal((uint)expected.Length, reader.ReadUInt32());
        foreach (var row in expected)
        {
            Assert.Equal(row.ArticleIdentity?.ArticleKey ?? row.PetIdentity!.ArticleKey, unchecked((ulong)reader.ReadInt64()));
            Assert.Equal(row.ItemId, reader.ReadInt32());
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal((uint)(row.Flags & InventorySlotRecord.WornFlagBit), reader.ReadUInt32());
            RoomItemAttribute[] attributes = row.PetIdentity is null ? [] : Attributes(row.ItemId);
            Assert.Equal((uint)attributes.Length, reader.ReadUInt32());
            foreach (var attribute in attributes)
            {
                Assert.Equal(attribute.ItemId, reader.ReadInt32());
                Assert.Equal(attribute.Kind, reader.ReadUInt32());
                Assert.Equal(4u, reader.ReadUInt32());
                foreach (var value in attribute.Values) Assert.Equal(value, reader.ReadUInt32());
                Assert.Equal(attribute.Unknown28, reader.ReadUInt32());
                Assert.Equal(attribute.Unknown32, reader.ReadWideStringPrefixed());
            }
        }
        Assert.Equal(0, reader.Remaining);
    }

    private static int[] OrdinaryIds(byte[] body, bool krSlots = false)
    {
        var reader = new PacketReader(body);
        var count = reader.ReadUInt32();
        var ids = new List<int>();
        for (var index = 0; index < count; index++)
        {
            ids.Add(reader.ReadInt32());
            reader.ReadInt32();
            reader.ReadInt64();
            Assert.Equal("", reader.ReadWideStringPrefixed());
            Assert.Equal(0u, reader.ReadUInt32());
            reader.ReadByte();
        }
        if (krSlots)
        {
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
        }
        Assert.Equal(0, reader.Remaining);
        return ids.ToArray();
    }

    private static byte[] Body(string build, (ushort Opcode, byte[] Payload) frame)
    {
        var article = frame.Opcode == TestBuilds.Opcodes(build).S2C("gspUpdateAllArticleSlot");
        if (build != "kr-1256" && !(article && build == "cn-774")) return frame.Payload;
        var count = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload);
        Assert.Equal(frame.Payload.Length - 4, count);
        using var input = new MemoryStream(frame.Payload, 4, count);
        using var compressed = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        compressed.CopyTo(output);
        return output.ToArray();
    }

    private sealed class Session(bool authenticated = true) : ISessionWithFeatures
    {
        /// <summary>Gets the fixed session identity used by the Article publication fixture.</summary>
        public long Id => 1;
        /// <summary>Gets the open fixture connection used while recording inventory frames.</summary>
        public IConnection Connection { get; } = new Connection();
        /// <summary>Gets the Korean modern client profile attached to the recording session.</summary>
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "kr-1256");
        /// <summary>Gets session-local storage for the inventory publication's connection state.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Gets the fixture feature set with or without an authenticated player, as selected by the test.</summary>
        public ISessionFeatures Features { get; } = new Features(authenticated);
        internal List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        /// <summary>Records the opcode and an owned copy of each emitted inventory payload.</summary>
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Features(bool authenticated) : ISessionFeatures
    {
        private readonly IPlayerSession? _player = authenticated ? new PlayerSession(new(42, "test", "Test", 1)) : null;
        /// <summary>Gets the single player-feature slot supported by this fixture.</summary>
        public int Capacity => 1;
        /// <summary>Gets the number of player features installed by the fixture's authentication choice.</summary>
        public int Count => _player is null ? 0 : 1;
        /// <summary>Reports the fixture feature set as open throughout the publication test.</summary>
        public bool IsClosed => false;
        /// <summary>Rejects feature registration because the fixture's authentication state is fixed at construction.</summary>
        public IDisposable Register<T>(T feature) where T : class => throw new NotSupportedException();
        /// <summary>Returns the requested authenticated player feature or fails when it is unavailable.</summary>
        public T Get<T>() where T : class => TryGet<T>(out var value) ? value : throw new InvalidOperationException();
        /// <summary>Exposes only the optional authenticated player feature through its interface type.</summary>
        public bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class
        {
            feature = typeof(T) == typeof(IPlayerSession) ? (T?)_player : null;
            return feature is not null;
        }
    }

    private sealed class Connection : IConnection
    {
        /// <summary>Gets the fixed connection identity paired with the recording session.</summary>
        public long Id => 1;
        /// <summary>Gets the loopback endpoint used as the fixture connection's remote address.</summary>
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects transport reads because the fixture invokes publication directly.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects transport writes because outgoing frames are recorded by the session.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Gets connection-local storage available to the publication path.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Reports the fixture connection as open for every inventory send.</summary>
        public bool IsClosed => false;
        /// <summary>Completes fixture connection closure without allocating transport resources.</summary>
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
