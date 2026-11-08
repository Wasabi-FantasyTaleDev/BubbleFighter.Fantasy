using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Maps the audited object-script peer messages onto authenticated room operations.</summary>
internal sealed class MapScriptObjectBinding(string gameProtocol, string relayProtocol,
    IOpcodeTable game, IOpcodeTable relay, PacketTables packets, string build)
    : IMessageSender<RoundMapObjectVisibilityChanged>, IMessageSender<RoundBreakableMapObjectCreated>
{
    private const string VisibilityName = "gppSetAddableObjectVisible_";
    private const string BreakableName = "gppCreateBreakableObject_";
    private static readonly PacketReadLimits Limits = new(MaxCollectionElements: 1024, MaxValues: 1032);

    /// <summary>Gets the game protocol on which accepted map changes are published.</summary>
    public string Protocol => gameProtocol;

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireSignature(packets, build, "MapObjectVisibility", "map object visibility",
            "name:list<u8>,visible:bool,sequence:u32");
        PacketLayoutContract.RequireSignature(packets, build, "BreakableMapObject", "breakable map objects",
            "name:list<u8>,hit_points:f32,sequence:u32");
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        Bind(VisibilityName, body => ReadVisibility(packets, build, body));
        Bind(BreakableName, body => ReadBreakable(packets, build, body));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundMapObjectVisibilityChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundBreakableMapObjectCreated>>(this));

        void Bind<T>(string name, Func<byte[], T> read) where T : IMessage
        {
            _ = game.S2C(name);
            Register(gameProtocol, game.C2S(name));
            Register(relayProtocol, relay.C2S(name));
            void Register(string protocol, ushort opcode)
            {
                context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
                {
                    var report = read(packet.Payload.ToArray());
                    if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("No map-script object handler is installed.");
                }));
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<T>>(new Input<T>(protocol)));
            }
        }
    }

    /// <summary>Publishes the native object name, visibility and sequence without transcoding.</summary>
    public ValueTask SendAsync(ISession session, RoundMapObjectVisibilityChanged message, CancellationToken ct) =>
        session.SendAsync(game.S2C(VisibilityName), WriteVisibility(packets, build, message), ct);

    /// <summary>Publishes a destructible map object's native name, health and sequence.</summary>
    public ValueTask SendAsync(ISession session, RoundBreakableMapObjectCreated message, CancellationToken ct) =>
        session.SendAsync(game.S2C(BreakableName), WriteBreakable(packets, build, message), ct);

    internal static RoundMapObjectVisibilityChanged ReadVisibility(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.Read(packets, build, "MapObjectVisibility", body, Limits);
        return new(ReadName(values), (bool)values["visible"]!, (int)values["sequence"]!);
    }

    internal static RoundBreakableMapObjectCreated ReadBreakable(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.Read(packets, build, "BreakableMapObject", body, Limits);
        var health = (float)values["hit_points"]!;
        if (!float.IsFinite(health) || health < 0)
            throw new PacketFormatException("Map object health must be finite and non-negative.", 0);
        return new(ReadName(values), health, (int)values["sequence"]!);
    }

    internal static byte[] WriteVisibility(PacketTables packets, string build, RoundMapObjectVisibilityChanged message) =>
        PacketCodec.Write(packets, build, "MapObjectVisibility", new Dictionary<string, object?>
        {
            ["name"] = message.Name.ToArray(), ["visible"] = message.Visible, ["sequence"] = message.Sequence,
        });

    internal static byte[] WriteBreakable(PacketTables packets, string build, RoundBreakableMapObjectCreated message) =>
        PacketCodec.Write(packets, build, "BreakableMapObject", new Dictionary<string, object?>
        {
            ["name"] = message.Name.ToArray(), ["hit_points"] = message.HitPoints, ["sequence"] = message.Sequence,
        });

    private static byte[] ReadName(IReadOnlyDictionary<string, object?> values)
    {
        var bytes = ((object?[])values["name"]!).Select(value => Convert.ToByte(value)).ToArray();
        if (bytes.Length == 0 || bytes.Contains((byte)0))
            throw new PacketFormatException("Map object name is empty or contains a null byte.", 0);
        return bytes;
    }

    private sealed record Input<T>(string Protocol) : IInboundMessageBinding<T> where T : IMessage;
}
