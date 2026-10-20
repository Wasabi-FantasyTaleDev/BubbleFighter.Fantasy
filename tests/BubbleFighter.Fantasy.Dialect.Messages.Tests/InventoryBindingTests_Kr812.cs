using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the legacy inventory-entry idiom: an empty S2C 801
/// re-establishes the connection's time origin, then the whole bag rides an S2C 802 update.
/// Login publishes the bag on S2C 801 instead.
/// </summary>
public sealed class InventoryBindingTests_Kr812
{
    private static InventoryBinding Binding(InventoryBaselineScope scope) =>
        new("kr-812/game", TestBuilds.Opcodes("kr-812"), TestBuilds.Packets, "kr-812", scope);

    private static InventorySlotRecord Slot(long key, int itemId, byte flags = 0) =>
        new(42, InventoryBag.CharacterBag, key, itemId, 99999, flags)
        { Lifetime = InventoryLifetime.Permanent };

    [Fact]
    public async Task InventoryEntryRestatesTheWholeBagOnTheFullOpcode()
    {
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var session = new RecordingSession(7);
        var bag = new[] { Slot(1, 300001, 1), Slot(2, 202001, 1), Slot(3, 201003) };

        await Binding(scope).SendAsync(session, new InventoryReplaced(bag, []), default);

        Assert.Equal(new ushort[] { 801 }, session.Sent.Select(f => f.Opcode).ToArray());
        Assert.Equal(4 + 44 * bag.Length, session.Sent[0].Payload.Length);
        Assert.Equal(bag.Length, BinaryPrimitives.ReadInt32LittleEndian(session.Sent[0].Payload));
        // Rows ascend by id (201003, 202001, 300001), and the equipped bool32 rides each 44-byte
        // record at offset 16 (i32 id, i32 unknown2, f64 days, then the flag): offsets 20, 64, 108.
        Assert.Equal(201003, BinaryPrimitives.ReadInt32LittleEndian(session.Sent[0].Payload[4..8]));
        Assert.Equal(300001, BinaryPrimitives.ReadInt32LittleEndian(session.Sent[0].Payload[92..96]));
        Assert.Equal(0, session.Sent[0].Payload[20]);
        Assert.Equal(1, session.Sent[0].Payload[64]);
        Assert.Equal(1, session.Sent[0].Payload[108]);
    }

    [Fact]
    public async Task FullReplacementRemovesExhaustedConsumablesButKeepsOwnedItems()
    {
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var session = new RecordingSession(27);
        var bag = new[] { Slot(1, 100104) with { Quantity = 0 }, Slot(2, 300001, 1) };
        await Binding(scope).SendAsync(session, new InventoryReplaced(bag, []), default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal(48, frame.Payload.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame.Payload));
        Assert.Equal(300001, BinaryPrimitives.ReadInt32LittleEndian(frame.Payload[4..]));
        session.Sent.Clear();
        await Binding(scope).SendLoginAsync(session, bag, default);
        Assert.Equal(frame.Payload, Assert.Single(session.Sent).Payload);
    }

    [Fact]
    public async Task LoginPublishesTheBagOnTheFullOpcodeAndAnchorsTheOrigin()
    {
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var session = new RecordingSession(9);
        var bag = new[] { Slot(1, 300001, 1), Slot(2, 202001) };

        await Binding(scope).SendLoginAsync(session, bag, default);

        var frame = Assert.Single(session.Sent);
        Assert.Equal((ushort)801, frame.Opcode);
        Assert.Equal(4 + 44 * bag.Length, frame.Payload.Length);
        // The origin now stands: an update against it must not throw.
        session.Sent.Clear();
        await Binding(scope).SendAsync(session, new InventorySlotsUpdated([Slot(2, 202001, 1)]), default);
        Assert.Equal((ushort)802, Assert.Single(session.Sent).Opcode);
    }

    [Fact]
    public async Task UpdatesWithoutAnOriginFailLoud()
    {
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var session = new RecordingSession(11);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Binding(scope)
            .SendAsync(session, new InventorySlotsUpdated([Slot(1, 300001)]), default).AsTask());
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void BaselineScopeBoundsOriginsAndReleasesThem()
    {
        using var scope = new InventoryBaselineScope(1, TimeProvider.System);
        var first = scope.Reset(1);
        Assert.Equal(first, scope.Require(1));
        Assert.Throws<InvalidOperationException>(() => scope.Reset(2));
        scope.Remove(1);
        Assert.Throws<InvalidOperationException>(() => scope.Require(1));
        var second = scope.Reset(2);
        Assert.Equal(second, scope.Require(2));
        scope.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scope.Reset(3));
    }

    [Fact]
    public async Task LocalViewPublicationSendsViewThenPresetBar()
    {
        // Equip and preset bursts end 805 -> 833; the entry
        // burst puts the bar first, but the client's readers (sub_8213A0 / sub_832D60) fill
        // independent models, so one composite order is used for every local publication.
        var view = new EquipmentViewBinding("kr-812/game", 805, 833, 832, TestBuilds.Packets, "kr-812");
        var session = new RecordingSession(13);
        await view.SendAsync(session,
            new LocalEquipmentViewUpdated(42, 300001, [300001, 202001, 201001, 205001, 207001], 1, 202001, 205001), default);
        Assert.Equal(new ushort[] { 805, 833 }, session.Sent.Select(f => f.Opcode).ToArray());
        Assert.Equal(28 + 5 * 4, session.Sent[0].Payload.Length);
        Assert.Equal(47, session.Sent[1].Payload.Length);
        Assert.Equal(1, session.Sent[1].Payload[0]);
    }

    [Fact]
    public async Task LocalViewWithoutANamedPresetBarSendsTheViewAlone()
    {
        var view = new EquipmentViewBinding("cn-324/game", 805, null, null, TestBuilds.Packets, "cn-324");
        var session = new RecordingSession(17);
        await view.SendAsync(session,
            new LocalEquipmentViewUpdated(42, 300001, [300001, 202001, 201001, 207001], 0, 202001, 0), default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal((ushort)805, frame.Opcode);
    }

    [Fact]
    public async Task BagRowsRideTheWireAscendingByItemId()
    {
        // The twenty slots ascend by id with the character last.
        using var scope = new InventoryBaselineScope(4, TimeProvider.System);
        var session = new RecordingSession(25);
        await Binding(scope).SendLoginAsync(session,
            [Slot(1, 300001, 1), Slot(2, 204001, 1), Slot(3, 201001, 1)], default);
        var frame = Assert.Single(session.Sent);
        // Each kr-812 slot is 44 bytes; the first id sits right after the u32 count.
        var first = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload[4..8]);
        var second = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload[48..52]);
        var third = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload[92..96]);
        Assert.Equal((201001, 204001, 300001), (first, second, third));
    }

    [Fact]
    public async Task RemoteViewPublicationNeverTouchesThePresetBar()
    {
        var view = new EquipmentViewBinding("kr-812/game", 805, 833, 832, TestBuilds.Packets, "kr-812");
        var session = new RecordingSession(19);
        await view.SendAsync(session, new RemoteEquipmentViewUpdated(43, 300001, [300001, 202001]), default);
        var frame = Assert.Single(session.Sent);
        Assert.Equal((ushort)805, frame.Opcode);
        Assert.Equal(43, BinaryPrimitives.ReadInt32LittleEndian(frame.Payload));
    }

    [Fact]
    public async Task Cn324EquipResultReplySelectsTheOpcodeByRequestAndMapsFailureToNoItem()
    {
        // cn-324 sub_70B280 switches on the result: case 0 is the success message, case 3 the no-item one.
        var binding = new EquipmentBinding("cn-324/game", null, TestBuilds.Packets, "cn-324", 808, 809);
        var session = new RecordingSession(21);
        await binding.SendAsync(session, new EquipmentChangeReplied(new EquipmentChangeRequested(803, 203001, true), true), default);
        await binding.SendAsync(session, new EquipmentChangeReplied(new EquipmentChangeRequested(804, 202001, false), true), default);
        await binding.SendAsync(session, new EquipmentChangeReplied(new EquipmentChangeRequested(803, 999999, true), false), default);

        Assert.Equal(new ushort[] { 808, 809, 808 }, session.Sent.Select(f => f.Opcode).ToArray());
        Assert.All(session.Sent.Take(2), f => Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(f.Payload)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(session.Sent[2].Payload));
    }

    [Fact]
    public async Task BuildsWithoutTheStatedReplySendNoResultFrame()
    {
        var binding = new EquipmentBinding("kr-812/game", null, TestBuilds.Packets, "kr-812");
        var session = new RecordingSession(22);
        await binding.SendAsync(session, new EquipmentChangeReplied(new EquipmentChangeRequested(803, 202001, true), true), default);
        Assert.Empty(session.Sent);
        Assert.True(TestBuilds.Packets.CapabilityOf("cn-324", "equip_result_reply"));
        Assert.False(TestBuilds.Packets.CapabilityOf("kr-812", "equip_result_reply"));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public async Task PresetBindingPreservesTypedFailureStatusAndItsResolvedOpcode(string build)
    {
        var binding = new EquipmentBinding(build + "/game", 4101, TestBuilds.Packets, build);
        var session = new RecordingSession(23);
        var request = new WeaponPresetChangeRequested(-1, 4);
        await binding.SendAsync(session, new WeaponPresetChangeReplied(request, 2) { Status = 1 }, default);
        await binding.SendAsync(session, new WeaponPresetChangeReplied(request, 2) { Status = -1 }, default);
        await binding.SendAsync(session, new WeaponPresetChangeReplied(request, 2), default);

        Assert.Equal(3, session.Sent.Count);
        Assert.All(session.Sent, frame => Assert.Equal((ushort)4101, frame.Opcode));
        Assert.Equal(Convert.FromHexString("0201000000"), session.Sent[0].Payload);
        Assert.Equal(Convert.FromHexString("02FFFFFFFF"), session.Sent[1].Payload);
        Assert.Equal(Convert.FromHexString("0200000000"), session.Sent[2].Payload);
    }

    /// <summary>A session stub recording outbound frames; the bindings only need the connection id.</summary>
    private sealed class RecordingSession(long connectionId) : ISession
    {
        public long Id => 1;
        public IConnection Connection { get; } = new StubConnection(connectionId);
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 458, 0, "kr-812");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubConnection(long id) : IConnection
    {
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
