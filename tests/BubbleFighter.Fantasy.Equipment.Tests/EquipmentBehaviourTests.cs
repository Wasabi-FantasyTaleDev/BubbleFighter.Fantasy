using BubbleFighter.Fantasy.Lobby;
using BubbleFighter.Fantasy.Contracts.Inventory;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Equipment;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Equipment.Sessions;

using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Exercises the wear / remove path end to end against a recording session. With
/// <c>persist_worn_state</c> on and the real memory persistence row plus the
/// lobby push service mounted (the same wiring the runtime uses), C2S 804 sets
/// the stored slot's worn bit, replies S2C 811 and re-pushes one S2C 803 whose
/// container 1 carries the updated slot; C2S 805 clears the bit the same way. A
/// request the store cannot apply - unknown item id, out-of-band key, session
/// without a lobby identity - is answered with the failure discriminator and
/// mutates nothing. With the services absent (persist_worn_state: false) the
/// row keeps the historical ack-only behaviour for any key.
/// </summary>
public sealed class EquipmentBehaviourTests
{
    private sealed class WireEquipmentHandlers(EquipmentHandlers domain)
    {
        internal ValueTask HandleWearAsync(ISession session, InboundPacket packet, CancellationToken ct) =>
            domain.HandleAsync(session, EquipmentCodec.Decode(packet.Payload.Span, true), ct);
        internal ValueTask HandleRemoveAsync(ISession session, InboundPacket packet, CancellationToken ct) =>
            domain.HandleAsync(session, EquipmentCodec.Decode(packet.Payload.Span, false), ct);
    }

    private static readonly IOpcodeTable Opcodes = EquipmentOpcodeTests.LoadTable();

    private const long SampleUserNum = 42;

    /// <summary>An item id the seeded bag carries, in the 100xxx..301xxx band.</summary>
    private const int OwnedItemId = 200001;

    private const long SampleSlotKey = 0x1122334455667788L;

    private static InventorySlotRecord SeededSlot(byte flags) => new(
        SampleUserNum, InventoryBag.CharacterBag, SampleSlotKey, OwnedItemId, 99999, flags);

    private static (WireEquipmentHandlers Handlers, MemoryInventoryRepository Inventory, EquippedItemScope Scope) NewPersistingHandlers(
        EquipmentOptions? options = null)
    {
        options ??= new EquipmentOptions();
        var table = new MemoryPlayerTable(16);
        var inventory = new MemoryInventoryRepository(table);
        var binding = new BubbleFighter.Fantasy.Dialect.Messages.InventoryBinding(
            "kr-1256/game", Opcodes, BubbleFighter.Fantasy.Packets.PacketTables.Load(
                BubbleFighter.Fantasy.Tests.SharedDialectRows.PacketsDirectory()), "kr-1256");
        var push = new InventoryPush(new LoginInventoryResolver(inventory, seedStarterInventory: false), binding, binding, binding);
        var scope = new EquippedItemScope(options.MaxTrackedConnections, options.MaxEquippedPerSession);
        return (
            new WireEquipmentHandlers(new EquipmentHandlers(scope, inventory, push, NullLogger.Instance, new EquipmentCodec("kr-1256/game", Opcodes))),
            inventory,
            scope);
    }

    private static (WireEquipmentHandlers Handlers, EquippedItemScope Scope) NewAckOnlyHandlers(EquipmentOptions? options = null)
    {
        options ??= new EquipmentOptions();
        var scope = new EquippedItemScope(options.MaxTrackedConnections, options.MaxEquippedPerSession);
        return (new WireEquipmentHandlers(new EquipmentHandlers(scope, null, null, NullLogger.Instance, new EquipmentCodec("kr-1256/game", Opcodes))), scope);
    }

    private static async Task SeedAsync(MemoryInventoryRepository inventory, params InventorySlotRecord[] slots) =>
        await inventory.ReplaceSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, slots, CancellationToken.None);

    private static InboundPacket Wear(long key) =>
        new(Opcodes.C2S("gcpReqInventoryEquip"), WireLengthSupport.Written(w => w.WriteInt64(key)));

    private static InboundPacket Remove(long key) =>
        new(Opcodes.C2S("gcpReqInventoryUnequip"), WireLengthSupport.Written(w => w.WriteInt64(key)));

    private static EquippedItemSet WornSet(EquippedItemScope scope, RecordingSession session) =>
        scope.TryGet(session.Connection.Id, out var set)
            ? set
            : throw new InvalidOperationException($"No equipped set for connection {session.Connection.Id}.");

    [Fact]
    public async Task WearSetsTheStoredWornFlagAndRepushesContainer1()
    {
        var (handlers, inventory, scope) = NewPersistingHandlers();
        await SeedAsync(inventory, SeededSlot(0));
        var session = new RecordingSession(1, SampleUserNum);

        await handlers.HandleWearAsync(session, Wear(OwnedItemId), CancellationToken.None);

        // Ack first, re-push second: the ack is toast and telemetry only, the
        // 803 is the state change.
        Assert.Equal(
            [Opcodes.S2C("gspRepInventoryEquip"), (ushort)803],
            session.Sent.Select(f => f.Opcode).ToArray());

        var (ackOpcode, ackPayload) = session.Sent[0];
        Assert.Equal(EquipmentWire.RepInventoryEquipBytes, ackPayload.Length);
        var ack = new PacketReader(ackPayload);
        Assert.Equal(EquipmentWire.SuccessResult, ack.ReadInt32());
        Assert.Equal(OwnedItemId, ack.ReadInt64());
        Assert.Equal(0, ack.Remaining);

        // The re-push carries exactly one container-1 slot, and its decoded
        // wire fields are the seeded row with flags bit 0x01 set.
        var decoded = DecodeContainer1(session.Sent[1].Payload);
        var slot = Assert.Single(decoded);
        Assert.Equal(OwnedItemId, slot.Unknown1);
        Assert.Equal(99999, slot.Unknown2);
        Assert.Equal(SampleSlotKey, slot.SlotKey);
        Assert.Equal(string.Empty, slot.Unknown4);
        Assert.Equal(InventorySlotRecord.WornFlagBit, slot.Flags);

        // The store holds the flag too, so the next login burst carries it.
        var stored = await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(InventorySlotRecord.WornFlagBit, Assert.Single(stored).Flags);
        Assert.True(WornSet(scope, session).Contains(OwnedItemId));
    }

    [Fact]
    public async Task RemoveClearsTheStoredWornFlagAndRepushesContainer1()
    {
        var (handlers, inventory, scope) = NewPersistingHandlers();
        await SeedAsync(inventory, SeededSlot(InventorySlotRecord.WornFlagBit));
        var session = new RecordingSession(2, SampleUserNum);

        await handlers.HandleWearAsync(session, Wear(OwnedItemId), CancellationToken.None);
        await handlers.HandleRemoveAsync(session, Remove(OwnedItemId), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                Opcodes.S2C("gspRepInventoryEquip"),
                (ushort)803,
                Opcodes.S2C("gspRepInventoryUnequip"),
                (ushort)803,
            },
            session.Sent.Select(f => f.Opcode).ToArray());

        var (ackOpcode, ackPayload) = session.Sent[2];
        Assert.Equal(EquipmentWire.RepInventoryUnequipBytes, ackPayload.Length);
        var ack = new PacketReader(ackPayload);
        Assert.Equal(EquipmentWire.SuccessResult, ack.ReadInt32());
        Assert.Equal(OwnedItemId, ack.ReadInt64());
        Assert.Equal(0, ack.Remaining);

        // The second re-push restates the slot with the worn bit cleared.
        var decoded = DecodeContainer1(session.Sent[3].Payload);
        var slot = Assert.Single(decoded);
        Assert.Equal(OwnedItemId, slot.Unknown1);
        Assert.Equal(0, slot.Flags);

        var stored = await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(0, Assert.Single(stored).Flags);
        Assert.False(WornSet(scope, session).Contains(OwnedItemId));
    }

    [Fact]
    public async Task WearOfUnknownItemIdAcksFailureAndMutatesNothing()
    {
        var (handlers, inventory, scope) = NewPersistingHandlers();
        await SeedAsync(inventory, SeededSlot(0));
        var session = new RecordingSession(3, SampleUserNum);

        await handlers.HandleWearAsync(session, Wear(999999), CancellationToken.None);

        // One failure ack, no re-push, the stored flags byte untouched and the
        // connection worn set empty.
        var (opcode, payload) = Assert.Single(session.Sent);
        Assert.Equal(Opcodes.S2C("gspRepInventoryEquip"), opcode);
        Assert.Equal(EquipmentWire.RepInventoryEquipBytes, payload.Length);
        var r = new PacketReader(payload);
        Assert.Equal(EquipmentWire.FailureResult, r.ReadInt32());
        Assert.Equal(999999, r.ReadInt64());
        Assert.Equal(0, r.Remaining);

        var stored = await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(0, Assert.Single(stored).Flags);
        Assert.False(scope.TryGet(session.Connection.Id, out _));
    }

    [Fact]
    public async Task RemoveOfUnknownItemIdAcksFailureAndMutatesNothing()
    {
        var (handlers, inventory, _) = NewPersistingHandlers();
        await SeedAsync(inventory, SeededSlot(0));
        var session = new RecordingSession(4, SampleUserNum);

        await handlers.HandleRemoveAsync(session, Remove(999999), CancellationToken.None);

        var (opcode, payload) = Assert.Single(session.Sent);
        Assert.Equal(Opcodes.S2C("gspRepInventoryUnequip"), opcode);
        var r = new PacketReader(payload);
        Assert.Equal(EquipmentWire.FailureResult, r.ReadInt32());
        Assert.Equal(999999, r.ReadInt64());
        Assert.Equal(0, r.Remaining);

        var stored = await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(0, Assert.Single(stored).Flags);
    }

    [Fact]
    public async Task KeyOutsideTheBagBandIsRefusedWithoutAStoreCall()
    {
        // The purchase path hands out slot keys far above the client's bag
        // band; a client echoing one of those instead of the item id must not
        // reach the store.
        var (handlers, inventory, _) = NewPersistingHandlers();
        await SeedAsync(inventory, SeededSlot(0));
        var session = new RecordingSession(5, SampleUserNum);

        await handlers.HandleWearAsync(session, Wear(SampleSlotKey), CancellationToken.None);

        var (opcode, payload) = Assert.Single(session.Sent);
        Assert.Equal(Opcodes.S2C("gspRepInventoryEquip"), opcode);
        var r = new PacketReader(payload);
        Assert.Equal(EquipmentWire.FailureResult, r.ReadInt32());
        Assert.Equal(SampleSlotKey, r.ReadInt64());

        var stored = await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(0, Assert.Single(stored).Flags);
    }

    [Fact]
    public async Task SessionWithoutLobbyIdentityIsRefusedAndMutatesNothing()
    {
        var (handlers, inventory, scope) = NewPersistingHandlers();
        await SeedAsync(inventory, SeededSlot(0));
        var session = new RecordingSession(6);

        await handlers.HandleWearAsync(session, Wear(OwnedItemId), CancellationToken.None);

        var (opcode, payload) = Assert.Single(session.Sent);
        Assert.Equal(Opcodes.S2C("gspRepInventoryEquip"), opcode);
        var r = new PacketReader(payload);
        Assert.Equal(EquipmentWire.FailureResult, r.ReadInt32());
        Assert.Equal(OwnedItemId, r.ReadInt64());

        var stored = await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(0, Assert.Single(stored).Flags);
        Assert.False(scope.TryGet(session.Connection.Id, out _));
    }

    [Fact]
    public async Task RepeatedWearStaysSuccessfulAndStoresTheFlagOnce()
    {
        var (handlers, inventory, scope) = NewPersistingHandlers();
        await SeedAsync(inventory, SeededSlot(0));
        var session = new RecordingSession(7, SampleUserNum);

        await handlers.HandleWearAsync(session, Wear(OwnedItemId), CancellationToken.None);
        await handlers.HandleWearAsync(session, Wear(OwnedItemId), CancellationToken.None);

        // Both acks are successes and both re-pushes went out.
        Assert.Equal(4, session.Sent.Count);
        foreach (var ack in session.Sent.Where(f => f.Opcode == Opcodes.S2C("gspRepInventoryEquip")))
        {
            var r = new PacketReader(ack.Payload);
            Assert.Equal(EquipmentWire.SuccessResult, r.ReadInt32());
            Assert.Equal(OwnedItemId, r.ReadInt64());
        }

        Assert.Equal(2, session.Sent.Count(f => f.Opcode == (ushort)803));

        var stored = await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        var slot = Assert.Single(stored);
        Assert.Equal(InventorySlotRecord.WornFlagBit, slot.Flags);
        Assert.Equal(1, WornSet(scope, session).Count);
    }

    [Fact]
    public async Task WearRejectsTrailingBytesWithoutReplying()
    {
        var session = new RecordingSession(8, SampleUserNum);
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt64(OwnedItemId);
            w.WriteByte(0);
        });

        await Assert.ThrowsAsync<PacketFormatException>(async () =>
            await NewPersistingHandlers().Handlers.HandleWearAsync(
                session,
                new InboundPacket(Opcodes.C2S("gcpReqInventoryEquip"), payload),
                CancellationToken.None));
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task RemoveRejectsTruncatedBodyWithoutReplying()
    {
        var session = new RecordingSession(9, SampleUserNum);
        var payload = WireLengthSupport.Written(w => w.WriteInt32(7));

        await Assert.ThrowsAsync<PacketFormatException>(async () =>
            await NewPersistingHandlers().Handlers.HandleRemoveAsync(
                session,
                new InboundPacket(Opcodes.C2S("gcpReqInventoryUnequip"), payload),
                CancellationToken.None));
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task AckOnlyRowAnswersAnyKeyWithoutARePush()
    {
        // persist_worn_state: false mounts the handlers without the persistence
        // services: every key is acked as before the wear flow existed, and no
        // 803 ever leaves the row.
        var (handlers, _) = NewAckOnlyHandlers();
        var session = new RecordingSession(10, SampleUserNum);

        await handlers.HandleWearAsync(session, Wear(SampleSlotKey), CancellationToken.None);
        await handlers.HandleRemoveAsync(session, Remove(SampleSlotKey), CancellationToken.None);

        Assert.Equal(
            [Opcodes.S2C("gspRepInventoryEquip"), Opcodes.S2C("gspRepInventoryUnequip")],
            session.Sent.Select(f => f.Opcode).ToArray());
        foreach (var (_, payload) in session.Sent)
        {
            var r = new PacketReader(payload);
            Assert.Equal(EquipmentWire.SuccessResult, r.ReadInt32());
            Assert.Equal(SampleSlotKey, r.ReadInt64());
            Assert.Equal(0, r.Remaining);
        }
    }

    [Fact]
    public void EquippedSetIsBounded()
    {
        var set = new EquippedItemSet(2);
        Assert.Equal(2, set.Capacity);
        Assert.True(set.Equip(1));
        Assert.True(set.Equip(2));
        Assert.False(set.Equip(1));
        Assert.Throws<InvalidOperationException>(() => set.Equip(3));
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void EquippedSetUnequipOfUnknownKeyIsANoOp()
    {
        var set = new EquippedItemSet(4);
        Assert.False(set.Unequip(42));
        Assert.True(set.Equip(42));
        Assert.True(set.Unequip(42));
        Assert.False(set.Unequip(42));
        Assert.Equal(0, set.Count);
    }

    /// <summary>
    /// Decodes one S2C 803 envelope: u32 compressed length, zlib stream, then the
    /// plain body's container 1 (u32 count + count x 25-byte slot records).
    /// Containers 2 and 3 are skipped by the reader running out of records
    /// only after container 1, so the returned list pins container 1 alone.
    /// </summary>
    private static List<(int Unknown1, int Unknown2, long SlotKey, string Unknown4, byte Flags)> DecodeContainer1(
        byte[] payload)
    {
        var compressedLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Assert.Equal(payload.Length - 4, compressedLength);
        using var source = new MemoryStream(payload, 4, payload.Length - 4);
        using var zlib = new System.IO.Compression.ZLibStream(
            source, System.IO.Compression.CompressionMode.Decompress);
        using var target = new MemoryStream();
        zlib.CopyTo(target);
        Assert.Equal(37, (int)target.Length);

        var slots = new List<(int, int, long, string, byte)>();
        var r = new PacketReader(target.ToArray());
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var unknown1 = r.ReadInt32();
            var unknown2 = r.ReadInt32();
            var slotKey = r.ReadInt64();
            var unknown4 = r.ReadWideStringPrefixed();
            Assert.Equal(0u, r.ReadUInt32());
            var flags = r.ReadByte();
            slots.Add((unknown1, unknown2, slotKey, unknown4, flags));
        }

        return slots;
    }
}
