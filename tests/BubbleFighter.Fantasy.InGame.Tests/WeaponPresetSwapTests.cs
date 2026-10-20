using BubbleFighter.Fantasy.Contracts.Inventory;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins for the C2S 832 weapon-preset swap and its S2C 836 apply reply: the request body is exactly
/// five bytes in <c>i32</c>-then-<c>u8</c> order with the slot capped at 4, the reply body is the <c>u8</c>
/// slot then a zero status <c>i32</c>, the reply goes only to the swapping session - the client code recovers no
/// peer surface for a remote actor's weapon swap - and a granted slot is persisted under the sender's lobby
/// identity behind the already-sent reply, never blocking it.
/// </summary>
public sealed class WeaponPresetSwapTests
{
    /// <summary>
    /// Request shape the builder writes: the leading <c>i32</c> is 0 on the UI
    /// paths (<c>sub_9753A0</c> passes flag 0) and the slot <c>u8</c> follows.
    ///
    /// </summary>
    private static readonly byte[] UiPathRequest = [0x00, 0x00, 0x00, 0x00, 0x03];

    [Fact]
    public void RequestBodyIsPinnedAtFiveBytesInI32ThenU8Order()
    {
        // The builder appends 4 bytes then 1 byte through the stream vtable
        // (sub_808F10): 4 + 1 = 5.
        Assert.Equal(5, WeaponPresetSwap.BodyBytes);
        var swap = WeaponPresetSwap.Parse(UiPathRequest);
        Assert.Equal(0, swap.Unknown1);
        Assert.Equal(3, swap.Slot);

        // Endianness and field-order pin: the first four bytes are one
        // little-endian i32, the fifth byte is the slot.
        var hotkey = WeaponPresetSwap.Parse([0x78, 0x56, 0x34, 0x12, 0x02]);
        Assert.Equal(0x12345678, hotkey.Unknown1);
        Assert.Equal(2, hotkey.Slot);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void ShortOrLongRequestBodiesAreRejected(int length)
    {
        var body = new byte[length];
        UiPathRequest.AsSpan(0, Math.Min(length, UiPathRequest.Length)).CopyTo(body);
        Assert.Throws<PacketFormatException>(() => WeaponPresetSwap.Parse(body));
    }

    [Fact]
    public void SlotAboveFourIsRejected()
    {
        // The client's own builder returns for a slot above 4 (sub_808F10)
        // and the hotkey table has five entries, so slot 5 on the wire cannot
        // come from this client.
        Assert.Throws<PacketFormatException>(
            () => WeaponPresetSwap.Parse([0x00, 0x00, 0x00, 0x00, 0x05]));
    }

    [Fact]
    public void ReplyBodyIsU8SlotThenZeroStatusI32()
    {
        // The dispatcher case reads one u8 (sub_429DD0) then one i32 status
        // through the same stream slot (sub_9D2FF0:9D5043-9D5067): 1 + 4 = 5.
        Assert.Equal(5, WeaponPresetSwapReply.BodyBytes);
        Assert.Equal(0, WeaponPresetSwapReply.Success);
        var body = WeaponPresetSwapReply.WriteSuccess(3);
        Assert.Equal(5, body.Length);
        Assert.Equal(Convert.ToHexString([0x03, 0x00, 0x00, 0x00, 0x00]), Convert.ToHexString(body));
    }

    [Fact]
    public async Task TheSwappingSessionReceivesTheApplyReplyAndPeersReceiveNothing()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        var (_, guestSeat) = registry.Join(guest, room.Id, 2, "guest");
        var hostRelay = new RecordingSession(20);
        var guestRelay = new RecordingSession(21);
        registry.BindRelay(hostRelay, hostSeat.Key);
        registry.BindRelay(guestRelay, guestSeat.Key);
                var relay = new TestRelay(registry);

        await relay.Flows.HandleWeaponSwapAsync(
            hostRelay, new InboundPacket(InGameOpcodes.C2SWeaponPresetSwap, UiPathRequest), CancellationToken.None);

        // The reply rides the session that delivered the request, under
        // S2C 836, with the requested slot and success status.
        Assert.Equal([InGameOpcodes.S2CWeaponPresetSwap], hostRelay.Opcodes);
        Assert.Equal(
            Convert.ToHexString(WeaponPresetSwapReply.WriteFailure(3)),
            Convert.ToHexString(hostRelay.Body(InGameOpcodes.S2CWeaponPresetSwap)));

        // No peer surface: the S2C 836 case swaps the receiving client's own
        // model, so echoing it to the guest would switch the guest's weapon.
        Assert.Empty(guestRelay.Opcodes);
        Assert.Empty(guest.Opcodes);
        Assert.Empty(host.Opcodes);
    }

    [Fact]
    public async Task ARequestOnTheGameSessionIsAnsweredOnThatSession()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var hostRelay = new RecordingSession(20);
        registry.BindRelay(hostRelay, hostSeat.Key);
                var relay = new TestRelay(registry);

        await relay.Flows.HandleWeaponSwapAsync(
            host, new InboundPacket(InGameOpcodes.C2SWeaponPresetSwap, UiPathRequest), CancellationToken.None);

        Assert.Equal([InGameOpcodes.S2CWeaponPresetSwap], host.Opcodes);
        Assert.Equal(
            Convert.ToHexString(WeaponPresetSwapReply.WriteFailure(3)),
            Convert.ToHexString(host.Body(InGameOpcodes.S2CWeaponPresetSwap)));
        Assert.Empty(hostRelay.Opcodes);
    }

    [Fact]
    public async Task AMalformedRequestBodyFailsLoudAndSendsNothing()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var hostRelay = new RecordingSession(20);
        registry.BindRelay(hostRelay, hostSeat.Key);
                var relay = new TestRelay(registry);

        byte[] badSlot = [0, 0, 0, 0, 5];
        await Assert.ThrowsAsync<PacketFormatException>(async () => await relay.Flows.HandleWeaponSwapAsync(
            hostRelay, new InboundPacket(InGameOpcodes.C2SWeaponPresetSwap, badSlot), CancellationToken.None));

        Assert.Empty(hostRelay.Opcodes);
        Assert.Empty(host.Opcodes);
    }

    [Fact]
    public async Task AGrantedSwapPersistsTheSlotUnderTheSendersIdentity()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var hostRelay = new RecordingSession(20);
        registry.BindRelay(hostRelay, hostSeat.Key);
        var state = new RecordingPlayerState();
        hostRelay.SetLobbyPlayer(42, "host", pastLoginBurst: false);
                var relay = new TestRelay(registry, playerState: state);

        await relay.Flows.HandleWeaponSwapAsync(
            hostRelay, new InboundPacket(InGameOpcodes.C2SWeaponPresetSwap, UiPathRequest), CancellationToken.None);

        // The reply is exactly as before: the granted slot with status 0 on the
        // delivering session only.
        Assert.Equal([InGameOpcodes.S2CWeaponPresetSwap], hostRelay.Opcodes);
        Assert.Equal(
            Convert.ToHexString(WeaponPresetSwapReply.WriteSuccess(3)),
            Convert.ToHexString(hostRelay.Body(InGameOpcodes.S2CWeaponPresetSwap)));

        // The grant is persisted behind the reply, keyed by the lobby identity of the session that sent the
        // request (grant time is the persist point).
        var grant = Assert.Single(state.Sets);
        Assert.Equal(42, grant.UserNum);
        Assert.Equal(3, grant.Slot);
    }

    [Fact]
    public async Task AFailingStoreIsLoggedAndTheReplyStillGoesOut()
    {
        var order = new List<string>();
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var hostRelay = new RecordingSession(20, order);
        registry.BindRelay(hostRelay, hostSeat.Key);
        var state = new RecordingPlayerState(order) { ThrowOnSet = new InvalidOperationException("store down") };
        hostRelay.SetLobbyPlayer(42, "host", pastLoginBurst: false);
                var relay = new TestRelay(registry, playerState: state);

        await relay.Flows.HandleWeaponSwapAsync(
            hostRelay, new InboundPacket(InGameOpcodes.C2SWeaponPresetSwap, UiPathRequest), CancellationToken.None);

        // A failed save must never be acknowledged as a successful swap.
        Assert.Equal([InGameOpcodes.S2CWeaponPresetSwap], hostRelay.Opcodes);
        Assert.Equal(Convert.ToHexString(WeaponPresetSwapReply.WriteFailure(3)),
            Convert.ToHexString(hostRelay.Body(InGameOpcodes.S2CWeaponPresetSwap)));
        Assert.Equal(["save", "reply"], order);
    }

    [Fact]
    public async Task ASessionWithoutLobbyIdentityIsAnsweredButPersistsNothing()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var state = new RecordingPlayerState();
                var relay = new TestRelay(registry, playerState: state);

        await relay.Flows.HandleWeaponSwapAsync(
            host, new InboundPacket(InGameOpcodes.C2SWeaponPresetSwap, UiPathRequest), CancellationToken.None);

        // Without an identity the server cannot persist the grant, so it refuses it.
        Assert.Equal([InGameOpcodes.S2CWeaponPresetSwap], host.Opcodes);
        Assert.Equal(Convert.ToHexString(WeaponPresetSwapReply.WriteFailure(3)),
            Convert.ToHexString(host.Body(InGameOpcodes.S2CWeaponPresetSwap)));
        Assert.Empty(state.Sets);
    }

    /// <summary>Lobby-owned record shape the identity resolver reads by property name.</summary>
    private sealed record LobbyPlayer(int UserNum, string Nickname);

    /// <summary>
    /// Store double that records the grants a handler persists, optionally
    /// failing the set the way a down database would.
    ///
    /// </summary>
    private sealed class RecordingPlayerState : IPlayerInventoryState
    {
        private readonly Dictionary<long, byte> _slots = [];
        private readonly List<string>? _order;

        public RecordingPlayerState(List<string>? order = null) => _order = order;

        /// <summary>Gets every persisted grant in arrival order.</summary>
        public List<(long UserNum, byte Slot)> Sets { get; } = [];

        /// <summary>Gets or sets the exception the next set throws, if any.</summary>
        public Exception? ThrowOnSet { get; init; }

        public ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(_slots.TryGetValue(userNum, out var slot) ? slot : (byte)0);

        public ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct)
        {
            Sets.Add((userNum, slot));
            _order?.Add("save");
            if (ThrowOnSet is not null)
                throw ThrowOnSet;
            _slots[userNum] = slot;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSession(long id, List<string>? order = null) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        private readonly List<(ushort Opcode, byte[] Body)> _sent = [];

        public long Id { get; } = id;
        public IConnection Connection { get; } = new FakeConnection();
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();

        public IEnumerable<ushort> Opcodes
        {
            get
            {
                lock (_sent)
                    return _sent.Select(s => s.Opcode).ToArray();
            }
        }

        public byte[] Body(ushort opcode)
        {
            lock (_sent)
                return _sent.Last(s => s.Opcode == opcode).Body;
        }

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            lock (_sent)
                _sent.Add((opcode, payload.ToArray()));
            order?.Add("reply");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnection : IConnection
    {
        public long Id => 0;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
