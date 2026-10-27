using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Dialect.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The modern typed round's movement: the full and the partial movement reports become one typed report, and the
/// merged record goes out on the other players' relay connections. The layouts are the build's
/// <c>MovementReport</c> and <c>MovementSample</c> in <c>packets/round-movement.yaml</c>: kr-1256 sub_95E920 writes
/// the full record and kr-1256 sub_10F4930 the mask-gated delta, as kr-812 sub_80DBB0 writes gppWanderLean and
/// gppWanderLeanOptimize, in wider fields (four-byte floats for kr-812's two-byte coordinates).
/// </summary>
internal sealed class RoundMovementBinding(string protocol, string relayProtocol, IOpcodeTable game, IOpcodeTable relay, PacketTables tables,
    string build, ILogger logger) : IInboundMessageBinding<RoundMovementReported>, IMessageSender<RoundMovementRelayed>
{
    private ushort _relayed;

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        // The fields the reports are built from must be the build's, with the widths the typed report carries.
        PacketLayoutContract.RequireFields(tables, build, "MovementReport", "Typed movement", ("object_id", "u32"), ("x", "f32"), ("y", "f32"), ("z", "f32"), ("yaw", "u8"),
            ("pitch", "u8"), ("state", "u8"), ("tick", "u16"));
        PacketLayoutContract.RequireFields(tables, build, "MovementSample", "Typed movement", ("actor", "u32"), ("tick", "u16"), ("mask", "u8"), ("x", "when<mask&0x01,f32>"),
            ("y", "when<mask&0x02,f32>"), ("z", "when<mask&0x04,f32>"), ("yaw", "when<mask&0x08,u8>"),
            ("pitch", "when<mask&0x10,u8>"), ("state", "when<mask&0x20,u8>"));
        // The record goes out on the relay connection (kr-1256 sub_95E920 writes the same 21 bytes it reads back).
        _relayed = relay.Find(PacketDirection.ServerToClient, "gppWanderLean")
            ?? throw new ConfigException("The modern typed movement requires the relay S2C gppWanderLean.");
        foreach (var name in new[] { "gppWanderLean", "gppWanderLeanOptimize" })
        {
            _ = game.Find(PacketDirection.ClientToServer, name)
                ?? throw new ConfigException($"The modern typed movement requires game C2S {name}.");
            _ = relay.Find(PacketDirection.ClientToServer, name)
                ?? throw new ConfigException($"The modern typed movement requires relay C2S {name}.");
        }
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundMovementReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundMovementRelayed>>(this));
        Bind("gppWanderLean", body =>
        {
            var report = PacketCodec.Read(tables, build, "MovementReport", body);
            return new RoundMovementReported(Int(report, "object_id"), PlayerMovementState.FullMask, Float(report, "x"),
                Float(report, "y"), Float(report, "z"), Byte(report, "yaw"), Byte(report, "pitch"), Byte(report, "state"),
                Tick(report));
        });
        Bind("gppWanderLeanOptimize", body =>
        {
            // A field whose mask bit is clear is absent; the typed report carries it as zero beside the mask.
            var sample = PacketCodec.Read(tables, build, "MovementSample", body);
            return new RoundMovementReported(Int(sample, "actor"), Byte(sample, "mask"), Float(sample, "x"),
                Float(sample, "y"), Float(sample, "z"), Byte(sample, "yaw"), Byte(sample, "pitch"), Byte(sample, "state"),
                Tick(sample));
        });

        void Bind(string name, Func<ReadOnlySpan<byte>, RoundMovementReported> decode)
        {
            RoomRoutes.Bind(context, protocol, game, logger, name, decode);
            // kr-1256 sub_95B910 selects the match TCP connection created by kr-1256 sub_9A74B0.
            RoomRoutes.Bind(context, relayProtocol, relay, logger, name, decode, messageProtocol: protocol);
        }
    }

    /// <summary>Sends one merged movement record (kr-1256 sub_709700 reads it: four words, three bytes, the tick).</summary>
    public ValueTask SendAsync(ISession session, RoundMovementRelayed message, CancellationToken ct)
    {
        var movement = message.Movement;
        return session.SendAsync(_relayed, PacketCodec.Write(tables, build, "MovementReport", new Dictionary<string, object?>
        {
            ["object_id"] = message.ActorId, ["x"] = movement.X, ["y"] = movement.Y, ["z"] = movement.Z,
            ["yaw"] = movement.Yaw, ["pitch"] = movement.Pitch, ["state"] = movement.State, ["tick"] = message.Tick,
        }), ct);
    }

    private static int Int(IReadOnlyDictionary<string, object?> values, string name) => Convert.ToInt32(values[name]);

    private static ushort Tick(IReadOnlyDictionary<string, object?> values) => Convert.ToUInt16(values["tick"]);

    private static float Float(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var value) ? Convert.ToSingle(value) : 0f;

    private static byte Byte(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var value) ? Convert.ToByte(value) : (byte)0;
}
