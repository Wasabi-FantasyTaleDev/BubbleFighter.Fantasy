using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class RoomReadyTests
{
    [Fact]
    public async Task EachRecipientGetsItsOwnSlotAndTheSameMaster()
    {
        var (registry, room, host, guest, _) = Create();
        await RoomPush.PushRoomStaticAsync(room, CancellationToken.None);
        AssertInstall(host, 0, 0);
        AssertInstall(guest, 0, 1);

        registry.Detach(host.Id);
        await RoomPush.PushRoomStaticAsync(room, CancellationToken.None);
        AssertInstall(guest, 1, 1);
        var r = new PacketReader(guest.Body(InGameOpcodes.S2CGotoRoom));
        SessionSettingBlob.Read(ref r);
        r.ReadInt32(); r.ReadInt32();
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal(0, r.ReadInt32()); // A departed slot remains empty, never shifts the guest to zero.
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, 0)]
    public async Task StatusIsBroadcastWithinItsRoomAndSurvivesInstall(int ready, int shop, int inventory)
    {
        var (registry, room, host, guest, loop) = Create();
        var other = new RoomPushTests.RecordingSession(103);
        registry.CreateFromSetting(other, RoomWireSupport.OrdinarySettingWithMap(), 33, "other");
        await loop.Setup.HandleRoomStatusAsync(guest, Status(ready, shop, inventory), CancellationToken.None);
        Assert.Equal(new ushort[] { InGameOpcodes.S2CCreateRoomReply }, host.Opcodes);
        Assert.Equal(host.Body(InGameOpcodes.S2CCreateRoomReply), guest.Body(InGameOpcodes.S2CCreateRoomReply));
        Assert.Empty(other.Opcodes);
        var r = new PacketReader(host.Body(InGameOpcodes.S2CCreateRoomReply));
        Assert.Equal(1, r.ReadInt32());
        var seat = room.FindByGame(guest.Id)!;
        var expected = WireLengthSupport.Written(w => seat.ToPlayerInfo().Write(w));
        Assert.Equal(expected, r.ReadBytes(expected.Length).ToArray());
        Assert.Equal(ready != 0, seat.Ready);
        Assert.Equal(shop != 0, seat.InShop);
        Assert.Equal(inventory != 0, seat.InInventory);
        Assert.Equal(ready == 1, room.CanStart);

        await RoomPush.PushRoomStaticAsync(room, CancellationToken.None);
        var install = new PacketReader(guest.Body(InGameOpcodes.S2CGotoRoom));
        SessionSettingBlob.Read(ref install);
        install.ReadInt32(); install.ReadInt32(); install.ReadUInt32();
        install.ReadBytes(WireLengthSupport.Written(w => room.Seats[0].ToPlayerInfo().Write(w)).Length);
        Assert.Equal(expected, install.ReadBytes(expected.Length).ToArray());
    }

    [Fact]
    public async Task ReadyCanBeCancelledAndIsClearedAfterTheRound()
    {
        var (_, room, _, guest, loop) = Create();
        await loop.Setup.HandleRoomStatusAsync(guest, Status(1, 0, 0), CancellationToken.None);
        Assert.True(room.CanStart);
        await loop.Setup.HandleRoomStatusAsync(guest, Status(0, 0, 0), CancellationToken.None);
        Assert.False(room.CanStart);
        await loop.Setup.HandleRoomStatusAsync(guest, Status(1, 0, 0), CancellationToken.None);
        room.MarkStarting(); room.MarkPlaying(); room.MarkResult(); room.ResetToWaiting();
        Assert.False(room.FindByGame(guest.Id)!.Ready);
        Assert.False(room.CanStart);
    }

    [Fact]
    public async Task MasterNeedsNoReadyToggleAndCannotOverrideGuests()
    {
        var (registry, room, host, guest, loop) = Create();
        await loop.Setup.HandleRoomStatusAsync(host, Status(1, 0, 0), CancellationToken.None);
        Assert.False(room.FindByGame(host.Id)!.Ready);
        Assert.False(room.CanStart);
        registry.Detach(guest.Id);
        Assert.True(room.CanStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRequiresMasterAndReadyGuests(bool guestRequestsStart)
    {
        var (_, room, host, guest, loop) = Create();
        var body = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteWideStringPrefixed("rel_Village00_boomhill");
            w.WriteWideStringPrefixed("Boomhill");
            w.WriteWideStringPrefixed("rel_Village00_boomhill");
            w.WriteInt32(0);
            for (var i = 0; i < 13; i++) w.WriteInt32(0);
            w.WriteByte(0);
            w.WriteInt32(0);
        });
        if (guestRequestsStart)
            await loop.Setup.HandleRoomStatusAsync(guest, Status(1, 0, 0), CancellationToken.None);
        await loop.Setup.HandleCreateRoomAsync(guestRequestsStart ? guest : host,
            new InboundPacket(InGameOpcodes.C2SCreateRoom, body), CancellationToken.None);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
        Assert.DoesNotContain(InGameOpcodes.S2CEnterGameStage, host.Opcodes);
    }

    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(0, 1, 1)]
    public void InvalidStatusIsRejected(int ready, int shop, int inventory) =>
        Assert.Throws<PacketFormatException>(() => RoomStatusRequest.Parse(Status(ready, shop, inventory).Payload.Span));

    [Fact]
    public void ReferenceStatusTriplesPinTheSeatFieldOffsets()
    {
        foreach (var (slot, ready, shop, inventory) in new[]
        {
            (2, 0, 0, 1), (2, 0, 0, 0), (9, 1, 0, 0),
            (9, 0, 0, 0), (0, 0, 1, 0), (0, 0, 0, 0),
        })
        {
            var identity = new RoomPlayerIdentity(42, "sample", (byte)slot, Ready: ready != 0,
                InShop: shop != 0, InInventory: inventory != 0);
            var body = GameToClient.CreateRoomReply(slot, identity.ToPlayerInfo());
            Assert.Equal(224, body.Length);
            Assert.Equal(slot, BinaryPrimitives.ReadInt32LittleEndian(body));
            Assert.Equal(ready, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(42)));
            Assert.Equal(shop, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(46)));
            Assert.Equal(inventory, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(50)));
        }
    }

    private static void AssertInstall(RoomPushTests.RecordingSession recipient, int master, int local)
    {
        var body = recipient.Body(InGameOpcodes.S2CGotoRoom);
        var r = new PacketReader(body);
        SessionSettingBlob.Read(ref r);
        Assert.Equal(master, r.ReadInt32());
        Assert.Equal(local, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(body.Length - 8)));
    }

    private static InboundPacket Status(int ready, int shop, int inventory) => new(InGameOpcodes.C2SUnknown505,
        WireLengthSupport.Written(w => { w.WriteInt32(ready); w.WriteInt32(shop); w.WriteInt32(inventory); }));

    private static (RoomRegistry, PlayRoom, RoomPushTests.RecordingSession, RoomPushTests.RecordingSession, InGameFlows) Create()
    {
        var options = new InGameOptions { StartMatches = true };
        var registry = new RoomRegistry(options);
        var host = new RoomPushTests.RecordingSession(101);
        var guest = new RoomPushTests.RecordingSession(102);
        host.SetLobbyPlayer(11, "host", pastLoginBurst: false);
        guest.SetLobbyPlayer(22, "guest", pastLoginBurst: false);
        var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");
        registry.Join(guest, room.Id, 22, "guest");
        return (registry, room, host, guest, new InGameFlows(options, registry, NullLogger.Instance,
            signatureSender: new RecordingSignatureSender()));
    }
}
