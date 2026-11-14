using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Reads client_tick:u32 and writes unknown_4:u32 through packet data (kr-812 sub_844A30).
/// kr-812 sub_810C20 discards the reply word before updating its local delay tracker.
/// </summary>
public static class RelayLatencyWire
{
    /// <summary>Requires the complete one-word request and reply shapes before a dialect is installed.</summary>
    public static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireSignature(tables, build, "RelayLatencyRequest", "Relay latency", "client_tick:u32");
        PacketLayoutContract.RequireSignature(tables, build, "RelayLatencyReply", "Relay latency", "unknown_4:u32");
    }

    /// <summary>Consumes the entire native tick word and preserves its unsigned bits.</summary>
    public static RelayLatencyRequested ReadRequest(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RelayLatencyRequest", payload);
        return new(unchecked((uint)(int)fields["client_tick"]!));
    }

    /// <summary>Writes the configured opaque word without assigning it timestamp or status semantics.</summary>
    public static byte[] WriteReply(PacketTables tables, string build, RelayLatencyAcknowledged message) =>
        PacketCodec.Write(tables, build, "RelayLatencyReply",
            new Dictionary<string, object?> { ["unknown_4"] = message.Unknown4 });
}
