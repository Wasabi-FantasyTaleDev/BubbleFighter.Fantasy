using System.Buffers.Binary;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Exercises room membership, ready state and host transfer over loopback against the
/// in-process kr-1256 plugin rows. These are automated regression checks, not client acceptance.
///
/// </summary>
public sealed class RoomScenarioTests
{
    // S2C 503 seat record (kr-1256 sub_9DA720): join/ready announce, not a scene reinstall.
    private const ushort S2CRoomSeat = 503;
    // S2C 511 seat upsert (kr-1256 sub_9DB1E0): occupancy=0 clears a seat; do not type by length.
    private const ushort S2CSeatRecord = 511;
    // S2C 502 (kr-1256 sub_9DA3C0): i32 = new master seat; clears RoomPlayerInfo+96 (ready). Host leave only
    // (after 511:204@0, the remaining member gets 502=1).
    private const ushort S2CHostChanged = 502;
    private const ushort S2CSceneInstall = 117;
    // S2C 103 join failure (kr-1256 sub_857990 / LobbyStage case 0x67): result 5 = room missing (R9).
    private const ushort S2CJoinFailure = 103;
    private const int JoinRoomMissingResult = 5;
    private const int MenuReturnKind = 1;
    private static readonly TimeSpan Observe = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task TwoClientsLogInAndReachTheMainMenu()
    {
        await using var server = await ScenarioServer.StartAsync();
        var a = await server.ConnectAsync("scenA");
        var b = await server.ConnectAsync("scenB");
        await a.Login();
        await b.Login();
        Assert.Contains(a.Since(0), f => f.Opcode == S2CSceneInstall);
        Assert.Contains(b.Since(0), f => f.Opcode == S2CSceneInstall);
    }

    [Fact]
    public async Task GuestJoinsAndReadyReachesTheHost()
    {
        await using var server = await ScenarioServer.StartAsync();
        var (a, b, room) = await HostAndGuestInPlazaAsync(server);
        await b.JoinRoom(room);
        await Task.Delay(TimeSpan.FromMilliseconds(500), server.Ct);

        var hostCursor = a.Cursor;
        await b.SetReady(true);
        await Expect(a, S2CRoomSeat, hostCursor, "after the guest set ready");
    }

    [Fact]
    public async Task JoinedMemberIsAnnouncedWithASeatRecordNotAReinstall()
    {
        await using var server = await ScenarioServer.StartAsync();
        var (a, b, room) = await HostAndGuestInPlazaAsync(server);

        var hostCursor = a.Cursor;
        await b.JoinRoom(room);
        var seat = await Expect(a, S2CRoomSeat, hostCursor, "after the guest joined");
        // Guest is seated after the host (slot 0); 503 carries that slot, not a 117 reinstall.
        Assert.Equal(1, SlotIndex(seat.Payload));
        Assert.DoesNotContain(a.Since(hostCursor), f => f.Opcode == S2CSceneInstall);
    }

    [Fact]
    public async Task LeavingMemberIsAnnouncedToTheRoom()
    {
        await using var server = await ScenarioServer.StartAsync();
        var (a, b, room) = await HostAndGuestInPlazaAsync(server);
        var guestSlot = await JoinAndCaptureGuestSlotAsync(a, b, room);

        var hostCursor = a.Cursor;
        await b.LeaveRoom();
        // Non-master leave: 511 clear only (no S2C 502); V3 §7.1 / cap leave steps.
        var cleared = await a.WaitFor(S2CSeatRecord, hostCursor, Observe.TotalSeconds);
        Assert.Equal(guestSlot, SlotIndex(cleared.Payload));
        Assert.Equal(0, Occupancy(cleared.Payload));
        Assert.DoesNotContain(a.Since(hostCursor), f => f.Opcode == S2CSceneInstall);
    }

    [Fact]
    public async Task HostLeaveHandsTheRoomToTheRemainingMember()
    {
        await using var server = await ScenarioServer.StartAsync();
        var (a, b, room) = await HostAndGuestInPlazaAsync(server);
        var newHostSlot = await JoinAndCaptureGuestSlotAsync(a, b, room);
        const int leavingHostSlot = 0;

        var guestCursor = b.Cursor;
        await a.LeaveRoom();
        var cleared = await b.WaitFor(S2CSeatRecord, guestCursor, Observe.TotalSeconds);
        Assert.Equal(leavingHostSlot, SlotIndex(cleared.Payload));
        Assert.Equal(0, Occupancy(cleared.Payload));
        // 502 = new master seat (kr-1256 sub_9DA3C0): 511:204@0 then 502=1 for remaining B.
        var master = await b.WaitFor(S2CHostChanged, guestCursor, Observe.TotalSeconds);
        Assert.Equal(newHostSlot, HostSeat(master.Payload));
        Assert.DoesNotContain(b.Since(guestCursor), f => f.Opcode == S2CSceneInstall);
    }

    [Fact]
    public async Task JoiningAVanishedRoomKeepsTheConnection()
    {
        await using var server = await ScenarioServer.StartAsync();
        var (_, b, _) = await HostAndGuestInPlazaAsync(server);

        var from = b.Cursor;
        await b.Send(403, SimActor.RoomJoin(60000));
        // Missing room → S2C 103 result 5 (kr-1256 sub_857990); not S2C 128.
        var failure = await b.WaitFor(S2CJoinFailure, from, Observe.TotalSeconds);
        Assert.Equal(JoinRoomMissingResult, BinaryPrimitives.ReadInt32LittleEndian(failure.Payload));
        Assert.DoesNotContain(b.Since(from), f => f.Opcode == S2CSceneInstall);
        await b.ListRooms();
    }

    [Fact]
    public async Task PlazaMemberIsNotReinstalledWhenAnotherCreatesARoom()
    {
        await using var server = await ScenarioServer.StartAsync();
        var a = await server.ConnectAsync("scenA");
        var b = await server.ConnectAsync("scenB");
        await a.Login();
        await b.Login();
        await a.EnterPlaza();
        await b.EnterPlaza();

        var plazaCursor = b.Cursor;
        await a.CreateRoom();
        // Creator leaving the plaza clears that seat for observers (511 occupancy=0); not 806+511:232.
        var cleared = await b.WaitFor(S2CSeatRecord, plazaCursor, Observe.TotalSeconds);
        Assert.Equal(0, Occupancy(cleared.Payload));
        Assert.DoesNotContain(b.Since(plazaCursor), f => f.Opcode == S2CSceneInstall);
    }

    [Fact]
    public async Task PlazaMemberCanReturnToTheMainMenu()
    {
        await using var server = await ScenarioServer.StartAsync();
        var a = await server.ConnectAsync("scenA");
        await a.Login();
        await a.EnterPlaza();

        var from = a.Cursor;
        await a.ReturnToMenu();
        var install = a.Since(from).First(f => f.Opcode == S2CSceneInstall);
        // C2S 105 reply: kind=1 only (0x9AC9E4); do not latch on 102/501/967.
        Assert.Equal(MenuReturnKind, SimRoomList.ReadSessionKind(install.Payload));
    }

    // Waits for an opcode and, on timeout, names what did arrive so a failure explains itself.
    private static async Task<SimFrame> Expect(SimActor client, ushort opcode, int from, string when)
    {
        try
        {
            return await client.WaitFor(opcode, from, Observe.TotalSeconds);
        }
        catch (TimeoutException)
        {
            var seen = string.Join(" ", client.Since(from).Select(f => $"S{f.Opcode}({f.Payload.Length})"));
            Assert.Fail($"{client.Name} got no S2C {opcode} {when}; received: {(seen.Length == 0 ? "nothing" : seen)}");
            return null!;
        }
    }

    private static async Task<int> JoinAndCaptureGuestSlotAsync(SimActor host, SimActor guest, ushort room)
    {
        var hostCursor = host.Cursor;
        await guest.JoinRoom(room);
        var seat = await Expect(host, S2CRoomSeat, hostCursor, "after the guest joined");
        return SlotIndex(seat.Payload);
    }

    private static async Task<(SimActor Host, SimActor Guest, ushort Room)> HostAndGuestInPlazaAsync(ScenarioServer server)
    {
        var a = await server.ConnectAsync("scenA");
        var b = await server.ConnectAsync("scenB");
        await a.Login();
        await b.Login();
        await a.EnterPlaza();
        await b.EnterPlaza();
        var before = await b.ListRooms();
        var installId = await a.CreateRoom();
        var room = SimRoomList.FindNewRoom(before, await b.ListRooms(), installId);
        return (a, b, room);
    }

    private static int SlotIndex(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadInt32LittleEndian(payload);

    // RoomPlayerInfo.occupancy at +0 after the leading slotIndex (kr-1256 sub_1143710 / kr-1256 sub_9DB1E0).
    private static int Occupancy(ReadOnlySpan<byte> seatRecordPayload) =>
        BinaryPrimitives.ReadInt32LittleEndian(seatRecordPayload[4..]);

    private static int HostSeat(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadInt32LittleEndian(payload);
}
