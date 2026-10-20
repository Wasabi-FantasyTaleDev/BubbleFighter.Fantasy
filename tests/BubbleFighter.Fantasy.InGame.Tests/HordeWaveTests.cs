using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RecordingSession = BubbleFighter.Fantasy.InGame.Tests.RoomPushTests.RecordingSession;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The horde round's main-connection family, pinned byte for byte.</summary>
public sealed class HordeWaveTests
{
    // part03#437 (C2S 2027) and its answer part03#434 (S2C 645, serial 1).
    private static readonly byte[] DropReport = Convert.FromHexString(
        "6912204558B694432F46384538D003BFE6AB3240AAAF68BF0500000000000000");
    private static readonly byte[] DropSpawn = Convert.FromHexString(
        "01000000050000006912204558B694432F46384538D003BFE6AB3240AAAF68BF05000000");

    [Fact]
    public void BodiesKeepTheirRecordedLengths()
    {
        Assert.Equal(1, HordeWave1256.ReadWave(BitConverter.GetBytes(1), 903));
        Assert.Throws<PacketFormatException>(() => HordeWave1256.ReadWave(new byte[5], 903));
        Assert.Throws<PacketFormatException>(() => HordeWave1256.ReadWave([], 904));
        HordeWave1256.CheckAllWavesEnd([]);
        Assert.Throws<PacketFormatException>(() => HordeWave1256.CheckAllWavesEnd([0]));
        HordeWave1256.CheckWaveTable(new byte[2584]);
        Assert.Throws<PacketFormatException>(() => HordeWave1256.CheckWaveTable([]));
        Assert.Throws<PacketFormatException>(() => HordeWave1256.CheckWaveTable(new byte[HordeWave1256.MaxWaveTableBytes + 1]));
        Assert.Equal((1000000, 6), HordeWave1256.ReadPickup(Convert.FromHexString("40420F0006000000")));
        Assert.Throws<PacketFormatException>(() => HordeWave1256.ReadPickup(new byte[9]));
    }

    [Fact]
    public void DropSpawnMatchesTheRecordedReply()
    {
        var spawn = HordeWave1256.DropSpawnFor(DropReport, 1);
        Assert.Equal(36, spawn.Length);
        Assert.Equal(DropSpawn, spawn);
        Assert.Throws<PacketFormatException>(() => HordeWave1256.DropSpawnFor(DropReport[..31], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HordeWave1256.DropSpawnFor(DropReport, 0));
    }

    [Fact]
    public async Task WaveRequestsAndReportsReachEverySeatIncludingTheHost()
    {
        var (flow, host, guest, _) = HordeRoom(101);
        await flow.HandleWaveStartAsync(host, new(903, BitConverter.GetBytes(1)), CancellationToken.None);
        var table = Enumerable.Range(0, 2584).Select(i => (byte)i).ToArray();
        await flow.HandleWaveTableAsync(host, new(908, table), CancellationToken.None);
        await flow.HandlePickupAsync(guest, new(635, Convert.FromHexString("E0C8100002000000")), CancellationToken.None);
        await flow.HandleWaveEndAsync(host, new(904, BitConverter.GetBytes(1)), CancellationToken.None);
        await flow.HandleAllWavesEndAsync(host, new(905, Array.Empty<byte>()), CancellationToken.None);

        foreach (var seat in new[] { host, guest })
        {
            Assert.Equal(new ushort[] { 904, 909, 646, 905, 906 }, seat.Opcodes);
            Assert.Equal(BitConverter.GetBytes(1), seat.Body(904));
            Assert.Equal(table, seat.Body(909));
            Assert.Equal(Convert.FromHexString("E0C8100002000000"), seat.Body(646));
            Assert.Equal(BitConverter.GetBytes(1), seat.Body(905));
            Assert.Empty(seat.Body(906));
        }
    }

    [Fact]
    public async Task DropSerialsCountPerRoundAndRestartWithTheNextRound()
    {
        var (flow, host, guest, room) = HordeRoom(106);
        await flow.HandleDropAsync(host, new(2027, DropReport), CancellationToken.None);
        await flow.HandleDropAsync(host, new(2027, DropReport), CancellationToken.None);
        Assert.Equal(2, BitConverter.ToInt32(guest.Body(645)));
        Assert.Equal(DropSpawn.AsSpan(4).ToArray(), guest.Body(645).AsSpan(4).ToArray());

        lock (room.Gate)
        {
            room.MarkResult();
            room.ResetToWaiting();
            room.MarkStarting();
            room.MarkPlaying();
        }
        await flow.HandleDropAsync(host, new(2027, DropReport), CancellationToken.None);
        Assert.Equal(DropSpawn, host.Body(645));
    }

    [Fact]
    public async Task OtherModesAndUnseatedSessionsSendNothing()
    {
        var (flow, host, guest, _) = HordeRoom(1);
        await flow.HandleWaveStartAsync(host, new(903, BitConverter.GetBytes(1)), CancellationToken.None);
        await flow.HandleDropAsync(host, new(2027, DropReport), CancellationToken.None);
        Assert.Empty(host.Opcodes);
        Assert.Empty(guest.Opcodes);

        var (hordeFlow, hordeHost, _, _) = HordeRoom(101);
        var outsider = new RecordingSession(99);
        await hordeFlow.HandleWaveStartAsync(outsider, new(903, BitConverter.GetBytes(1)), CancellationToken.None);
        Assert.Empty(outsider.Opcodes);
        Assert.Empty(hordeHost.Opcodes);
    }

    private static (HordeWaveFlow Flow, RecordingSession Host, RecordingSession Guest, PlayRoom Room) HordeRoom(ushort mode)
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        registry.Join(guest, room.Id, 2, "guest");
        lock (room.Gate)
        {
            room.ModeId = mode;
            room.MarkStarting();
            room.MarkPlaying();
        }
        return (new HordeWaveFlow(registry, NullLogger.Instance), host, guest, room);
    }
}
