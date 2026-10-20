using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The horde (pirate) round on the main connection. The room host's client runs the waves; the
/// server answers each wave request and repeats the host's wave table, drops and pickups to every
/// seat, the host included (see <see cref="HordeWave1256"/> for the exchange).
/// </summary>
/// <param name="registry">The room registry.</param>
/// <param name="logger">The row logger.</param>
internal sealed class HordeWaveFlow(RoomRegistry registry, ILogger logger)
{
    /// <summary>C2S 903: answers the wave request with S2C 904 to every seat.</summary>
    public async ValueTask HandleWaveStartAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var wave = HordeWave1256.ReadWave(packet.Payload.Span, packet.Opcode);
        if (await BroadcastAsync(session, HordeWave1256.WaveStart, packet.Payload, ct).ConfigureAwait(false) is { } room)
            logger.LogInformation("Room {Room} horde wave {Wave} started.", room.Id, wave);
    }

    /// <summary>C2S 908: repeats the host's wave table as S2C 909 to every seat.</summary>
    public async ValueTask HandleWaveTableAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        HordeWave1256.CheckWaveTable(packet.Payload.Span);
        if (await BroadcastAsync(session, HordeWave1256.WaveTable, packet.Payload, ct).ConfigureAwait(false) is { } room)
            logger.LogInformation("Room {Room} horde wave table ({Length} bytes) sent to the room.", room.Id, packet.Payload.Length);
    }

    /// <summary>C2S 904: repeats the cleared wave as S2C 905 to every seat.</summary>
    public async ValueTask HandleWaveEndAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var wave = HordeWave1256.ReadWave(packet.Payload.Span, packet.Opcode);
        if (await BroadcastAsync(session, HordeWave1256.WaveEnd, packet.Payload, ct).ConfigureAwait(false) is { } room)
            logger.LogInformation("Room {Room} horde wave {Wave} cleared.", room.Id, wave);
    }

    /// <summary>C2S 905: answers the last cleared wave with an empty S2C 906 to every seat.</summary>
    public async ValueTask HandleAllWavesEndAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        HordeWave1256.CheckAllWavesEnd(packet.Payload.Span);
        if (await BroadcastAsync(session, HordeWave1256.AllWavesEnd, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false) is { } room)
            logger.LogInformation("Room {Room} horde waves all cleared.", room.Id);
    }

    /// <summary>C2S 2027: gives the drop the round's next serial and sends it to every seat as S2C 645.</summary>
    public async ValueTask HandleDropAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (HordeRoom(session, packet.Opcode) is not { } room)
            return;
        int serial;
        lock (room.Gate)
            serial = ++room.HordeDropSerial;
        var spawn = HordeWave1256.DropSpawnFor(packet.Payload.Span, serial);
        await SendToSeatsAsync(room, HordeWave1256.DropSpawn, spawn, ct).ConfigureAwait(false);
        logger.LogDebug("Room {Room} horde drop {Serial} sent to the room.", room.Id, serial);
    }

    /// <summary>C2S 635: repeats the pickup as S2C 646 to every seat.</summary>
    public async ValueTask HandlePickupAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var (entity, serial) = HordeWave1256.ReadPickup(packet.Payload.Span);
        if (await BroadcastAsync(session, HordeWave1256.Pickup, packet.Payload, ct).ConfigureAwait(false) is { } room)
            logger.LogDebug("Room {Room} horde drop {Serial} picked up by entity {Entity}.", room.Id, serial, entity);
    }

    private async ValueTask<PlayRoom?> BroadcastAsync(ISession session, ushort opcode, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (HordeRoom(session, opcode) is not { } room)
            return null;
        await SendToSeatsAsync(room, opcode, body, ct).ConfigureAwait(false);
        return room;
    }

    // The sender must be seated in a horde-family room; anything else is logged and dropped.
    private PlayRoom? HordeRoom(ISession session, ushort opcode)
    {
        if (registry.TryGet(session.Id) is not { } room)
        {
            logger.LogDebug("Session {Id} horde frame for S2C {Opcode} with no room; ignored.", session.Id, opcode);
            return null;
        }
        lock (room.Gate)
        {
            if (room.FindByGame(session.Id) is null)
            {
                logger.LogDebug("Session {Id} horde frame for S2C {Opcode} has no seat in room {Room}; ignored.", session.Id, opcode, room.Id);
                return null;
            }
            if (GameMode.Resolve(room.ModeId).Family != GameModeFamily.Horde)
            {
                logger.LogWarning("Session {Id} sent a horde frame for S2C {Opcode} in room {Room} of mode {Mode}; ignored.",
                    session.Id, opcode, room.Id, room.ModeId);
                return null;
            }
        }
        return room;
    }

    private static async ValueTask SendToSeatsAsync(PlayRoom room, ushort opcode, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        RoomSeat[] seats;
        lock (room.Gate)
            seats = [.. room.Seats];
        foreach (var seat in seats)
        {
            if (seat.Game.Connection.IsClosed)
                continue;
            await seat.Game.SendAsync(opcode, body, ct).ConfigureAwait(false);
        }
    }
}
