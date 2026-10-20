using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Dialects;

/// <summary>
/// The horde (pirate) round's main-connection family. The room host's client runs the waves: its
/// wave manager (<c>sub_7B9820</c>) asks for each wave, builds the wave table, reports drops and
/// pickups, and the server answers or repeats each report to every seat
/// (the host and the non-host players see the same counts).
/// </summary>
internal static class HordeWave1256
{
    /// <summary>C2S 903: <c>{i32 wave}</c>, the next wave the host asks to start; <c>sub_7B9820</c> latches until S2C 904 (part03#202).</summary>
    internal const ushort WaveStartRequest = 903;

    /// <summary>S2C 904: the requested wave number, sent to every seat (part03#199).</summary>
    internal const ushort WaveStart = 904;

    /// <summary>C2S 908: the host's wave table, a 2584-byte body.</summary>
    internal const ushort WaveTableReport = 908;

    /// <summary>S2C 909: the host's wave table, byte-identical, sent to every seat (part03#200).</summary>
    internal const ushort WaveTable = 909;

    /// <summary>C2S 904: <c>{i32 wave}</c>, the host cleared that wave; built by <c>sub_7BAB70</c> (part03#19779).</summary>
    internal const ushort WaveEndRequest = 904;

    /// <summary>S2C 905: the cleared wave number, sent to every seat (part03#17124).</summary>
    internal const ushort WaveEnd = 905;

    /// <summary>C2S 905: empty, the host cleared every wave; built by <c>sub_7BAC80</c> (part03#19780).</summary>
    internal const ushort AllWavesEndRequest = 905;

    /// <summary>S2C 906: empty, sent to every seat after C2S 905 (part03#17125).</summary>
    internal const ushort AllWavesEnd = 906;

    /// <summary>C2S 2027: a drop the host's round spawned, 32 bytes (part03#437).</summary>
    internal const ushort DropReport = 2027;

    /// <summary>S2C 645: the drop with its server serial, 36 bytes, read by <c>sub_6EFD00</c> (part03#434).</summary>
    internal const ushort DropSpawn = 645;

    /// <summary>C2S 635: <c>{i32 entity, i32 serial}</c>, a player picked a drop up (part03#2327).</summary>
    internal const ushort PickupReport = 635;

    /// <summary>S2C 646: the pickup, repeated to every seat, read by <c>sub_6EAD50</c> (part03#1170).</summary>
    internal const ushort Pickup = 646;

    /// <summary>The largest wave table relayed; the recorded table is 2584 bytes.</summary>
    internal const int MaxWaveTableBytes = 16384;

    /// <summary>Reads the <c>{i32 wave}</c> body of C2S 903 and C2S 904.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="opcode">The opcode, for the error text.</param>
    internal static int ReadWave(ReadOnlySpan<byte> body, ushort opcode)
    {
        if (body.Length != 4)
            throw new PacketFormatException($"C2S {opcode} must be one i32 wave number.", 0);
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }

    /// <summary>Checks the C2S 908 wave table: forwarded without parsing, so only its size is bounded.</summary>
    /// <param name="body">The request body.</param>
    internal static void CheckWaveTable(ReadOnlySpan<byte> body)
    {
        if (body.Length is 0 or > MaxWaveTableBytes)
            throw new PacketFormatException($"C2S 908 wave table must be 1..{MaxWaveTableBytes} bytes.", 0);
    }

    /// <summary>Checks that C2S 905 is empty.</summary>
    /// <param name="body">The request body.</param>
    internal static void CheckAllWavesEnd(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty)
            throw new PacketFormatException("C2S 905 carries no body.", 0);
    }

    /// <summary>
    /// Builds S2C 645 from a C2S 2027 drop report. <c>sub_6EFD00</c> reads nine 4-byte fields:
    /// the serial, the drop kind, six floats and the kind again; the reply carries the
    /// report's six floats and its kind (report +24) in both places, and drops the report's last
    /// i32 (0 in every recorded report).
    /// </summary>
    /// <param name="report">The 32-byte C2S 2027 body.</param>
    /// <param name="serial">The round's next drop serial, starting at 1.</param>
    internal static byte[] DropSpawnFor(ReadOnlySpan<byte> report, int serial)
    {
        if (report.Length != 32)
            throw new PacketFormatException("C2S 2027 must be six f32, i32 kind and i32 unknown0 (32 bytes).", 0);
        if (serial <= 0)
            throw new ArgumentOutOfRangeException(nameof(serial), serial, "Drop serials start at 1.");
        var body = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(body, serial);
        report.Slice(24, 4).CopyTo(body.AsSpan(4));
        report[..24].CopyTo(body.AsSpan(8));
        report.Slice(24, 4).CopyTo(body.AsSpan(32));
        return body;
    }

    /// <summary>Checks the <c>{i32 entity, i32 serial}</c> body of C2S 635, which S2C 646 repeats.</summary>
    /// <param name="body">The request body.</param>
    internal static (int Entity, int Serial) ReadPickup(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8)
            throw new PacketFormatException("C2S 635 must be i32 entity and i32 serial (8 bytes).", 0);
        return (BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]));
    }
}
