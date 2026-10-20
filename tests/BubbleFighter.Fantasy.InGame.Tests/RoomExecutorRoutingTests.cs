using System.Buffers.Binary;
using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class RoomExecutorRoutingTests
{
    private sealed record SendRecord(long Recipient, ushort Opcode, byte[] Body, long[] Members);

    [Fact]
    public async Task ConcurrentJoinLeaveAndReadyEachFanOutOverOneMemberTable()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var options = new InGameOptions { SendPlazaSnapshot = false };
            var registry = new RoomRegistry(options);
            var rooms = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
            var combat = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
            var host = new RoomPushTests.RecordingSession(101);
            var first = new RoomPushTests.RecordingSession(102);
            var second = new RoomPushTests.RecordingSession(103);
            host.SetLobbyPlayer(11, "host");
            first.SetLobbyPlayer(22, "first");
            second.SetLobbyPlayer(33, "second");
            var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");

            var records = new ConcurrentQueue<SendRecord>();
            foreach (var session in new[] { host, first, second })
            {
                var recipient = session;
                recipient.OnSend = async (opcode, body) =>
                {
                    long[] members;
                    lock (room.Gate)
                        members = [.. room.Seats.Select(seat => seat.Game.Id).Order()];
                    records.Enqueue(new SendRecord(recipient.Id, opcode, body, members));
                    // Yield mid fan-out so an unserialized leave or join would change the table between sends.
                    await Task.Delay(1).ConfigureAwait(false);
                };
            }

            await Task.WhenAll(
                Task.Run(async () => await rooms.HandleJoinRoomAsync(first, Join(room.Id), CancellationToken.None)),
                Task.Run(async () => await rooms.HandleJoinRoomAsync(second, Join(room.Id), CancellationToken.None)));
            var secondSlot = room.Seats.Single(seat => seat.Game.Id == second.Id).Slot;
            await Task.WhenAll(
                Task.Run(async () => await rooms.HandleUnknown404Async(first,
                    new InboundPacket(InGameOpcodes.C2SUnknown404, ReadOnlyMemory<byte>.Empty), CancellationToken.None)),
                Task.Run(async () => await combat.Setup.HandleRoomStatusAsync(second, Status(1), CancellationToken.None)));

            // Every S2C 503 body is one operation's fan-out: seat announcements and the ready broadcast.
            var fanOuts = records.Where(record => record.Opcode == InGameOpcodes.S2CCreateRoomReply)
                .GroupBy(record => Convert.ToHexString(record.Body)).ToArray();
            Assert.NotEmpty(fanOuts);
            foreach (var fanOut in fanOuts)
            {
                var tables = fanOut.Select(record => string.Join(',', record.Members)).Distinct().ToArray();
                Assert.Single(tables);
                Assert.All(fanOut, record => Assert.Contains(record.Recipient, record.Members));
            }

            var ready = Assert.Single(fanOuts, group =>
            {
                var body = group.First().Body;
                return BinaryPrimitives.ReadInt32LittleEndian(body) == secondSlot
                    && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(42)) == 1;
            });
            Assert.Equal(ready.First().Members, ready.Select(record => record.Recipient).Order().ToArray());
            Assert.Null(registry.TryGet(first.Id));
            Assert.Equal([host.Id, second.Id], room.Seats.Select(seat => seat.Game.Id).Order().ToArray());
            await registry.StopAllAsync();
        }
    }

    [Fact]
    public async Task ClosedConnectionDetachesOnTheRoomExecutorAndTheLastLeaveStopsIt()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var combat = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        var host = new RoomPushTests.RecordingSession(101);
        var guest = new RoomPushTests.RecordingSession(102);
        var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");
        registry.Join(guest, room.Id, 22, "guest");

        await combat.Setup.PostDetach(guest.Connection.Id);
        Assert.Null(registry.TryGet(guest.Id));
        Assert.Same(room, registry.Find(room.Id));

        await combat.Setup.PostDetach(host.Connection.Id);
        Assert.Null(registry.Find(room.Id));
        await registry.StopAllAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task ConcurrentStartsAndALeaveStartOnceWithOneRoster()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var options = new InGameOptions { StartMatches = true };
            var registry = new RoomRegistry(options);
            var combat = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
            var host = new RoomPushTests.RecordingSession(101);
            var guest = new RoomPushTests.RecordingSession(102);
            var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");
            registry.Join(guest, room.Id, 22, "guest");
            var membersAtStageEnter = new ConcurrentQueue<int>();
            foreach (var session in new[] { host, guest })
            {
                session.OnSend = async (opcode, _) =>
                {
                    if (opcode == InGameOpcodes.S2CEnterGameStage)
                    {
                        lock (room.Gate)
                            membersAtStageEnter.Enqueue(room.Seats.Count);
                    }
                    await Task.Delay(1).ConfigureAwait(false);
                };
            }

            var starts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await combat.Start.StartMatchAsync(room, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The second start finds the room no longer waiting (MarkStarting) or empty.
                }
            }));
            var leave = combat.Setup.PostDetach(guest.Connection.Id);
            await Task.WhenAll([.. starts, leave]);

            Assert.Equal(1, room.StartGeneration);
            Assert.Single(membersAtStageEnter.Distinct());
        }
    }

    [Fact]
    public async Task UnknownConnectionCloseCompletesWithoutRoomWork()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var combat = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        combat.Setup.OnConnectionClosed(new ConnectionClosedEvent(new RoomPushTests.FakeConnection(7), "test"));
        await combat.Setup.PostDetach(7);
        Assert.Empty(registry.All());
    }

    [Fact]
    public async Task TwoJoinersRacingForTheLastSeatSeatExactlyOne()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var options = new InGameOptions { MaxPlayersPerMatch = 2, SendPlazaSnapshot = false };
            var registry = new RoomRegistry(options);
            var rooms = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
            var host = new RoomPushTests.RecordingSession(101);
            var first = new RoomPushTests.RecordingSession(102);
            var second = new RoomPushTests.RecordingSession(103);
            host.SetLobbyPlayer(11, "host");
            first.SetLobbyPlayer(22, "first");
            second.SetLobbyPlayer(33, "second");
            var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");

            await Task.WhenAll(
                Task.Run(async () => await rooms.HandleUnknown403Async(first, JoinFromList(room.Id), CancellationToken.None)),
                Task.Run(async () => await rooms.HandleUnknown403Async(second, JoinFromList(room.Id), CancellationToken.None)));

            Assert.Equal(2, room.Seats.Count);
            var winner = registry.TryGet(first.Id) is not null ? first : second;
            var loser = ReferenceEquals(winner, first) ? second : first;
            Assert.Null(registry.TryGet(loser.Id));
            Assert.Contains(InGameOpcodes.S2CGotoRoom, winner.Opcodes);
            Assert.DoesNotContain(InGameOpcodes.S2CGotoRoom, loser.Opcodes);
            Assert.Equal(Convert.ToHexString(GameToClient.JoinResult(GameToClient.JoinResultRoomFull)),
                Convert.ToHexString(loser.Body(InGameOpcodes.S2CJoinResult)));
            await registry.StopAllAsync();
        }
    }

    [Fact]
    public async Task HostLeavingWhileTheGuestReadiesLeavesTheGuestMasterAndNotReady()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var options = new InGameOptions { SendPlazaSnapshot = false };
            var registry = new RoomRegistry(options);
            var rooms = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
            var combat = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
            var host = new RoomPushTests.RecordingSession(101);
            var guest = new RoomPushTests.RecordingSession(102);
            host.SetLobbyPlayer(11, "host");
            guest.SetLobbyPlayer(22, "guest");
            var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");
            registry.Join(guest, room.Id, 22, "guest");

            await Task.WhenAll(
                Task.Run(async () => await rooms.HandleUnknown404Async(host,
                    new InboundPacket(InGameOpcodes.C2SUnknown404, ReadOnlyMemory<byte>.Empty), CancellationToken.None)),
                Task.Run(async () => await combat.Setup.HandleRoomStatusAsync(guest, Status(1), CancellationToken.None)));

            // Whichever ran first, the promoted master ends not ready (0x96DD20 exempts the master) and was told
            // of its promotion.
            var seat = Assert.Single(room.Seats);
            Assert.Equal(guest.Id, seat.Game.Id);
            Assert.Equal(seat.Slot, room.MasterSlot);
            Assert.False(seat.Ready);
            Assert.Contains(InGameOpcodes.S2CMasterChanged, guest.Opcodes);
            await registry.StopAllAsync();
        }
    }

    [Fact]
    public async Task PasswordRoomRefusesAWrongPasswordAndSeatsTheRightOne()
    {
        var options = new InGameOptions { SendPlazaSnapshot = false };
        var registry = new RoomRegistry(options);
        var rooms = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
        var host = new RoomPushTests.RecordingSession(101);
        var guest = new RoomPushTests.RecordingSession(102);
        host.SetLobbyPlayer(11, "host");
        guest.SetLobbyPlayer(22, "guest");
        var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");
        room.Password = "pw";

        await rooms.HandleUnknown403Async(guest, JoinFromList(room.Id, "nope"), CancellationToken.None);
        Assert.Null(registry.TryGet(guest.Id));
        Assert.DoesNotContain(InGameOpcodes.S2CGotoRoom, guest.Opcodes);
        Assert.Equal(Convert.ToHexString(GameToClient.JoinResult(GameToClient.JoinResultWrongPassword)),
            Convert.ToHexString(guest.Body(InGameOpcodes.S2CJoinResult)));

        // A refusal never opens the finding-room wall.
        Assert.DoesNotContain(InGameOpcodes.S2CWaitingDialog, guest.Opcodes);
        var before = guest.Opcodes.Count();

        await rooms.HandleUnknown403Async(guest, JoinFromList(room.Id, "pw"), CancellationToken.None);
        Assert.Same(room, registry.TryGet(guest.Id));
        // 128 {0,0,""} leads, then the 117 install.
        Assert.Equal(new[] { InGameOpcodes.S2CWaitingDialog, InGameOpcodes.S2CGotoRoom },
            guest.Opcodes.Skip(before).Take(2).ToArray());
        Assert.Equal("000000000000000000000000", Convert.ToHexString(guest.Body(InGameOpcodes.S2CWaitingDialog)));
        await registry.StopAllAsync();
    }

    [Fact]
    public async Task MasterClosesAnEmptySeatAndJoinsSkipItUntilItReopens()
    {
        var options = new InGameOptions { MaxPlayersPerMatch = 3, SendPlazaSnapshot = false };
        var registry = new RoomRegistry(options);
        var rooms = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
        var host = new RoomPushTests.RecordingSession(101);
        var guest = new RoomPushTests.RecordingSession(102);
        var late = new RoomPushTests.RecordingSession(103);
        host.SetLobbyPlayer(11, "host");
        guest.SetLobbyPlayer(22, "guest");
        late.SetLobbyPlayer(33, "late");
        var (room, _) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 11, "host");

        await rooms.HandleSeatCloseAsync(host, SeatClose(1, true), CancellationToken.None);
        Assert.Equal(Convert.ToHexString(GameToClient.SeatClosed(1, true)),
            Convert.ToHexString(host.Body(InGameOpcodes.S2CSeatRecord)));
        // RoomPlayerInfo +1 (the closed byte sub_980730 reads) follows the i32 seat index and the +0 dword.
        Assert.Equal(1, BitConverter.ToInt32(host.Body(InGameOpcodes.S2CSeatRecord), 8));

        // The joiner takes seat 2, past the closed seat, and learns seat 1 is closed.
        await rooms.HandleUnknown403Async(guest, JoinFromList(room.Id), CancellationToken.None);
        Assert.Equal(2, registry.RequireGame(guest).Seat.Slot);
        Assert.Equal(Convert.ToHexString(GameToClient.SeatClosed(1, true)),
            Convert.ToHexString(guest.Body(InGameOpcodes.S2CSeatRecord)));

        // Two players plus one closed seat fill a three-seat room.
        await rooms.HandleUnknown403Async(late, JoinFromList(room.Id), CancellationToken.None);
        Assert.Null(registry.TryGet(late.Id));
        Assert.Equal(Convert.ToHexString(GameToClient.JoinResult(GameToClient.JoinResultRoomFull)),
            Convert.ToHexString(late.Body(InGameOpcodes.S2CJoinResult)));

        // Only the master may change seats; a guest's click changes nothing.
        var guestBefore = guest.Opcodes.Count();
        await rooms.HandleSeatCloseAsync(guest, SeatClose(1, false), CancellationToken.None);
        Assert.Equal(guestBefore, guest.Opcodes.Count());

        await rooms.HandleSeatCloseAsync(host, SeatClose(1, false), CancellationToken.None);
        Assert.Equal(Convert.ToHexString(GameToClient.SeatClosed(1, false)),
            Convert.ToHexString(guest.Body(InGameOpcodes.S2CSeatRecord)));
        await rooms.HandleUnknown403Async(late, JoinFromList(room.Id), CancellationToken.None);
        Assert.Equal(1, registry.RequireGame(late).Seat.Slot);
        await registry.StopAllAsync();
    }

    private static InboundPacket SeatClose(int slot, bool closed) => new(InGameOpcodes.C2SUnknown509,
        WireLengthSupport.Written(w => { w.WriteInt32(slot); w.WriteInt32(closed ? 1 : 0); }));

    private static InboundPacket JoinFromList(int roomId, string password = "") => new(InGameOpcodes.C2SUnknown403,
        WireLengthSupport.Written(w => { w.WriteInt32(roomId); w.WriteWideStringPrefixed(password); }));

    private static InboundPacket Join(int roomId) => new(InGameOpcodes.C2SJoinRoom,
        WireLengthSupport.Written(w => w.WriteInt32(roomId)));

    private static InboundPacket Status(int ready) => new(InGameOpcodes.C2SUnknown505,
        WireLengthSupport.Written(w => { w.WriteInt32(ready); w.WriteInt32(0); w.WriteInt32(0); }));
}
