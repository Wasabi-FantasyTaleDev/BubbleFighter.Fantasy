using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>Represents a datagram whose packet-table header is followed by an opaque body.</summary>
public readonly record struct ProtocolDatagram(ushort Opcode, ReadOnlyMemory<byte> Body);

/// <summary>Reads and writes the shared packet-table datagram header used by protocol bindings.</summary>
public static class ProtocolDatagramCodec
{
    private const string Structure = "UdpRelayDatagramPrefix";

    /// <summary>Requires an opcode word first and a header made only of fixed-width unsigned scalars.</summary>
    public static void ValidateHeader(PacketTables tables, string build)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrEmpty(build);
        PacketLayoutContract.RequireFields(tables, build, Structure, nameof(ProtocolDatagramCodec), ("opcode", "u16"));
        var fields = tables.Resolve(build, Structure);
        if (fields[0].Name != "opcode" || fields.Any(field => field.Type is not ("u8" or "u16" or "u32" or "u64")))
            throw new ConfigException($"{Structure} for '{build}' requires opcode first and fixed unsigned scalar fields.");
    }

    /// <summary>Reads the opcode at the start of a datagram and returns the untouched body.</summary>
    public static ProtocolDatagram Read(PacketTables tables, string build, ReadOnlyMemory<byte> datagram)
    {
        ValidateHeader(tables, build);
        var headerLength = PacketCodec.Write(tables, build, Structure, new Dictionary<string, object?>()).Length;
        if (datagram.Length < headerLength)
            throw new PacketFormatException($"Datagram for '{build}' is {datagram.Length} bytes; the {Structure} header needs {headerLength}.", 0);
        var header = PacketCodec.ReadScalars(tables, build, Structure, datagram.Span[..headerLength]);
        return new(Convert.ToUInt16(header["opcode"]), datagram[headerLength..]);
    }

    /// <summary>Writes a packet-table header followed by the supplied opaque body.</summary>
    public static byte[] Write(PacketTables tables, string build, ushort opcode, ReadOnlySpan<byte> body)
    {
        ValidateHeader(tables, build);
        var header = PacketCodec.Write(tables, build, Structure, new Dictionary<string, object?> { ["opcode"] = opcode });
        var datagram = new byte[checked(header.Length + body.Length)];
        header.CopyTo(datagram, 0);
        body.CopyTo(datagram.AsSpan(header.Length));
        return datagram;
    }
}
