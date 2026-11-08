using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// The player menu's "view info": the lobby handler answers a request for another logged-in player with that
/// player's record and an unknown or departed player with the build's failure result; the shared binding writes
/// both bodies table-driven per build (kr-1256 sub_99A430, cn-774 sub_BBA7A0, legacy kr-812 sub_83DD00).
/// </summary>
public sealed partial class FighterInfoTests
{
    /// <summary>A request for another logged-in player is answered with that player's identity.</summary>
    [Fact]
    public async Task AnotherLoggedInPlayerIsAnsweredWithTheirIdentity()
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var target = new FeatureSession(2);
        online.Attach(asker, new PlayerIdentity(10, "asker", "Asker", 5));
        online.Attach(target, new PlayerIdentity(20, "target", "Target", 9));
        var replies = new Replies();

        await FighterInfoHandler.HandleAsync(online, new Inventory(), replies, asker, new FighterInfoRequested(20), default);

        var (to, reply) = Assert.Single(replies.Sent);
        Assert.Same(asker, to);
        Assert.Equal(new PlayerIdentity(20, "target", "Target", 9), reply.Fighter);
        Assert.Equal(300007, reply.Appearance!.CharacterItemId);
        Assert.Equal([300007, 202031, 400001], reply.Appearance.EquippedItemIds);
    }

    /// <summary>An account that is not logged in is answered with no record at all, never the asker's own.</summary>
    [Fact]
    public async Task UnknownAndDepartedPlayersGetNoRecord()
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var leaver = new FeatureSession(2);
        online.Attach(asker, new PlayerIdentity(10, "asker", "Asker", 5));
        online.Attach(leaver, new PlayerIdentity(20, "leaver", "Leaver", 9));
        online.Remove(leaver.Connection.Id);
        var replies = new Replies();
        var store = new Inventory { Read = _ => throw new InvalidOperationException("A missing target has no readable inventory.") };

        await FighterInfoHandler.HandleAsync(online, store, replies, asker, new FighterInfoRequested(404), default);
        await FighterInfoHandler.HandleAsync(online, store, replies, asker, new FighterInfoRequested(20), default);

        Assert.Equal(2, replies.Sent.Count);
        Assert.All(replies.Sent, sent => Assert.Null(sent.Reply.Fighter));
    }

    /// <summary>A player may look at their own record, as the menu offers on the player's own row.</summary>
    [Fact]
    public async Task APlayerMayRequestTheirOwnRecord()
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        online.Attach(asker, new PlayerIdentity(10, "asker", "Asker", 5));
        var replies = new Replies();

        await FighterInfoHandler.HandleAsync(online, new Inventory(), replies, asker, new FighterInfoRequested(10), default);

        Assert.Equal("Asker", Assert.Single(replies.Sent).Reply.Fighter!.Nickname);
    }

    /// <summary>A connection that has not logged in cannot ask.</summary>
    [Fact]
    public async Task ARequestBeforeLoginIsRefused()
    {
        using var online = new PlayerSessionScope();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await FighterInfoHandler.HandleAsync(online, new Inventory(), new Replies(), new FeatureSession(1), new FighterInfoRequested(1), default));
    }

    /// <summary>Only the requested player's current, unexpired worn items are copied, without modifying the bag.</summary>
    [Fact]
    public async Task CurrentEquipmentIsReadWithoutRepairingOrSeedingInventory()
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var target = new FeatureSession(2);
        online.Attach(asker, new(10, "asker", "Asker", 5));
        online.Attach(target, new(20, "target", "Target", 9));
        var rows = new List<InventorySlotRecord>
        {
            Row(20, 202031), Row(20, 400001), Row(20, 300007), Row(20, 202031),
            Row(20, 300001, 0), Row(20, 202001, 0),
            Row(20, 400005), Row(20, 400005, 0),
            Row(20, 400002) with { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(-1)) },
            Row(20, 400003) with { Quantity = 0, Lifetime = InventoryLifetime.Life },
        };
        var store = new Inventory { Read = user =>
        {
            Assert.Equal(20, user);
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(rows);
        } };
        var replies = new Replies();
        await FighterInfoHandler.HandleAsync(online, store, replies, asker, new(20), default);
        var first = Assert.Single(replies.Sent).Reply.Appearance!;
        Assert.Equal([300007, 202031, 400001], first.EquippedItemIds);
        Assert.Equal(10, rows.Count);

        rows.Clear();
        rows.AddRange([Row(20, 300002), Row(20, 204005), Row(20, 400004)]);
        await FighterInfoHandler.HandleAsync(online, store, replies, asker, new(20), default);
        Assert.Equal([300002, 204005, 400004], replies.Sent[1].Reply.Appearance!.EquippedItemIds);
        Assert.Equal([300007, 202031, 400001], first.EquippedItemIds);
    }

    /// <summary>Ambiguous or missing characters get the native failure response, with no invented default.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteCharacterSelectionCannotOpenTheDialog(bool duplicate)
    {
        using var online = new PlayerSessionScope();
        var session = new FeatureSession(1);
        online.Attach(session, new(10, "asker", "Asker", 5));
        var store = new Inventory { Read = _ => ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(duplicate
            ? [Row(10, 300001), Row(10, 300002)] : [Row(10, 202001)]) };
        var replies = new Replies();
        await FighterInfoHandler.HandleAsync(online, store, replies, session, new(10), default);
        Assert.Null(Assert.Single(replies.Sent).Reply.Fighter);
        Assert.Null(replies.Sent[0].Reply.Appearance);
    }

    /// <summary>An expired character is unavailable even when its persisted worn flag is still set.</summary>
    [Fact]
    public async Task ExpiredSelectedCharacterGetsNoDefaultReplacement()
    {
        using var online = new PlayerSessionScope();
        var session = new FeatureSession(1);
        online.Attach(session, new(10, "asker", "Asker", 5));
        var store = new Inventory { Read = _ => ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(
            [Row(10, 300007) with { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(-1)) }, Row(10, 300001, 0)]) };
        var replies = new Replies();
        await FighterInfoHandler.HandleAsync(online, store, replies, session, new(10), default);
        Assert.Null(Assert.Single(replies.Sent).Reply.Fighter);
    }

    /// <summary>A store that leaks another account or bag, or ignores its bound, fails before any reply is published.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvalidInventorySnapshotsAreRejected(int invalid)
    {
        using var online = new PlayerSessionScope();
        var session = new FeatureSession(1);
        online.Attach(session, new(10, "asker", "Asker", 5));
        IReadOnlyList<InventorySlotRecord> rows = invalid switch
        {
            0 => [Row(999, 300001)],
            1 => [Row(10, 300001) with { Bag = InventoryBag.EquippedBag }],
            _ => Enumerable.Repeat(Row(10, 300001), InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(),
        };
        var store = new Inventory { Read = _ => ValueTask.FromResult(rows) };
        var replies = new Replies();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await FighterInfoHandler.HandleAsync(online, store, replies, session, new(10), default));
        Assert.Empty(replies.Sent);
    }

    /// <summary>Store reads cannot outlive the target's or requester's login ownership.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DepartedOrReplacedSessionsAreRecheckedAfterTheRead(bool requesterChanged, bool replace)
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var target = new FeatureSession(2);
        online.Attach(asker, new(10, "asker", "Asker", 5));
        online.Attach(target, new(20, "target", "Target", 9));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<InventorySlotRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Inventory { Read = _ =>
        {
            entered.SetResult();
            return new(release.Task);
        } };
        var replies = new Replies();
        var pending = FighterInfoHandler.HandleAsync(online, store, replies, asker, new(20), default).AsTask();
        await entered.Task;
        online.Remove(requesterChanged ? asker.Connection.Id : target.Connection.Id);
        if (replace)
            online.Attach(new FeatureSession(3), new(requesterChanged ? 10 : 20, "replacement", "Replacement", 30));
        release.SetResult([Row(20, 300007), Row(20, 202031)]);
        await pending;
        if (requesterChanged)
            Assert.Empty(replies.Sent);
        else
            Assert.Null(Assert.Single(replies.Sent).Reply.Fighter);
    }

    /// <summary>
    /// The modern request is two words (kr-1256 sub_9319E0, cn-774 sub_B277C0); the first is the clicked account id.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 1256, 118, 133)]
    [InlineData("cn-774", 774, 117, 133)]
    [InlineData("tw-454", 454, 117, 131)]
    public void ModernRequestIsTwoWordsOnTheBuildsNumber(string build, int major, int request, int reply)
    {
        var (binding, table) = Binding(build, major);
        Assert.Equal((ushort)request, table.C2S("gcpReqFighterInfo"));
        Assert.Equal((ushort)reply, table.S2C("gspRepFighterInfo"));
        var body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body, 555);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), unchecked((int)0xDEADBEEF));
        Assert.Equal(new FighterInfoRequested(555), binding.Read(body));
        Assert.Throws<PacketFormatException>(() => binding.Read(body[..4]));
        Assert.Throws<PacketFormatException>(() => binding.Read([.. body, 0]));
    }

    /// <summary>
    /// Found: result 1, the player's FighterInfo, a constructor-default partner, two empty strings and four words
    /// (kr-1256 sub_99A430, cn-774 sub_BBA7A0). The lengths follow from the FighterInfo sizes (440 and 463 with an
    /// empty nickname, plus the nickname twice).
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 1256, 440)]
    [InlineData("cn-774", 774, 463)]
    [InlineData("tw-454", 454, 455)]
    public async Task ModernFoundReplyIsTheTwoFighterShape(string build, int major, int fighter)
    {
        var (binding, table) = Binding(build, major);
        var session = new RecordingSession();
        await binding.SendAsync(session, new FighterInfoReplied(new PlayerIdentity(20, "target", "Ab", 9), new FighterAppearance(300007, [300007, 202031, 400001])), default);

        Assert.Equal(2, session.Sent.Count);
        Assert.Equal(table.S2C("gspInventoryViewUpdateView"), session.Sent[0].Opcode);
        var (opcode, body) = session.Sent[1];
        Assert.Equal(table.S2C("gspRepFighterInfo"), opcode);
        Assert.Equal(4 + (fighter + 2 * 4) + fighter + 4 + 4 + 16, body.Length); // "Ab" is written at +8 and +504
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4))); // identity
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8)));  // nickname length
    }

    /// <summary>
    /// Not found: result 0 and the same shape as a record with an empty nickname, because the client's reader takes
    /// the whole body whatever the result; the blocks are constructor defaults and name nobody.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 1256, 440)]
    [InlineData("cn-774", 774, 463)]
    [InlineData("tw-454", 454, 455)]
    public async Task ModernUnknownPlayerGetsResultZeroAndDefaultBlocks(string build, int major, int fighter)
    {
        var (binding, _) = Binding(build, major);
        var session = new RecordingSession();
        await binding.SendAsync(session, new FighterInfoReplied(null), default);

        var (_, body) = Assert.Single(session.Sent);
        Assert.Equal(4 + fighter + fighter + 4 + 4 + 16, body.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)));  // identity
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8)));  // nickname length
    }

    /// <summary>TW has an audited full reply; deployment remains an explicit opt-in.</summary>
    [Fact]
    public void Tw454HasAuditedFighterInfoCapability()
    {
        Assert.True(PacketTables.Load(PacketsDirectory()).CapabilityOf("tw-454", "fighter_info"));
    }

    private static (FighterInfoBinding Binding, IOpcodeTable Table) Binding(string build, int major)
    {
        var profile = new ClientProfile(build, build[..2], major, 0, 0, build);
        var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), profile);
        return (new FighterInfoBinding(build + "/game", table, PacketTables.Load(PacketsDirectory()), build), table);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    private static string PacketsDirectory() => Path.Combine(RepositoryRoot(), "packets");

    private sealed class Inventory : IInventoryRepository
    {
        internal Func<long, ValueTask<IReadOnlyList<InventorySlotRecord>>>? Read { get; init; }
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
        {
            Assert.Equal(InventoryBag.CharacterBag, bag);
            Assert.Equal(InventoryLimits.MaxInventorySlotsPerBag, maxCount);
            return Read is null ? ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([Row(userNum, 300007), Row(userNum, 202031), Row(userNum, 400001)]) : Read(userNum);
        }
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) => throw new InvalidOperationException("Inspection must not replace inventory.");
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) => throw new InvalidOperationException("Inspection must not change worn state.");
    }

    private static InventorySlotRecord Row(long userNum, int item, byte flags = InventorySlotRecord.WornFlagBit) =>
        new(userNum, InventoryBag.CharacterBag, item, item, 1, flags);

    private sealed class Replies : IMessageSender<FighterInfoReplied>
    {
        internal List<(ISession Session, FighterInfoReplied Reply)> Sent { get; } = [];

        public string Protocol => "test/game";

        public ValueTask SendAsync(ISession session, FighterInfoReplied message, CancellationToken ct)
        {
            Sent.Add((session, message));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSession : ISession
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];

        public long Id => 1;

        public ClientProfile Profile => new("test", "kr", 1256, 0, 0, "test");

        public IConnection Connection => throw new NotSupportedException();

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FeatureSession(long id) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        public long Id { get; } = id;

        public IConnection Connection { get; } = new Conn(id);

        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Conn(long id) : IConnection
    {
        public long Id { get; } = id;

        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);

        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();

        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public bool IsClosed => false;

        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
