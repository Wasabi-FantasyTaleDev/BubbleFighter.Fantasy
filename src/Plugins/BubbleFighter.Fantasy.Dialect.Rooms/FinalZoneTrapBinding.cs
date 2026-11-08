using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Native rod allocation and peer linking for the FinalZone item exchange.</summary>
internal sealed class FinalZoneTrapBinding(string gameProtocol, string relayProtocol,
    IOpcodeTable game, IOpcodeTable relay, PacketTables packets, string build)
    : IMessageSender<RoundFinalZoneTrapSpawned>, IMessageSender<RoundFinalZoneTrapLinked>
{
    /// <summary>Gets the game connection used to publish accepted rod and link messages.</summary>
    public string Protocol => gameProtocol;

    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        var place = game.C2S("gcpArea32TrapPlace_");
        var gameLink = game.C2S("gppArea32TrapLink_");
        var relayLink = relay.C2S("gppArea32TrapLink_");
        _ = game.S2C("gspArea32TrapSpawn_");
        _ = game.S2C("gppArea32TrapLink_");
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(router.Register(gameProtocol, place, async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(gameProtocol, session, ReadRequest(packets, build, packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("No FinalZone trap-placement handler is installed.");
        }));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundFinalZoneTrapRequested>>(new PlacementInput(gameProtocol)));
        RegisterLink(gameProtocol, gameLink);
        RegisterLink(relayProtocol, relayLink);
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundFinalZoneTrapSpawned>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundFinalZoneTrapLinked>>(this));

        void RegisterLink(string protocol, ushort opcode)
        {
            context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
            {
                if (!await messages.DispatchAsync(protocol, session, ReadLink(packets, build, packet.Payload.Span), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No FinalZone trap-link handler is installed.");
            }));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundFinalZoneTrapLinked>>(new LinkInput(protocol)));
        }
    }

    /// <summary>Creates one accepted endpoint, including the acknowledgement required by its owner.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneTrapSpawned message, CancellationToken ct) =>
        session.SendAsync(game.S2C("gspArea32TrapSpawn_"), WriteSpawn(packets, build, message), ct);

    /// <summary>Connects the accepted pair and clears the sender's native placement state.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneTrapLinked message, CancellationToken ct) =>
        session.SendAsync(game.S2C("gppArea32TrapLink_"), WriteLink(packets, build, message), ct);

    internal static RoundFinalZoneTrapRequested ReadRequest(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(packets, build, "Area32TrapRequest", body);
        var x = (float)values["x"];
        var y = (float)values["y"];
        var z = (float)values["z"];
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new PacketFormatException("FinalZone trap positions must be finite.", 12);
        return new((int)values["actor"], (int)values["item_spec_id"], (int)values["stage"], x, y, z);
    }

    internal static RoundFinalZoneTrapLinked ReadLink(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.Read(packets, build, "Area32TrapLink", body);
        return new((int)values["actor"]!, (int)values["first_object_id"]!, (int)values["second_object_id"]!,
            Convert.ToUInt16(values["weapon_id"]), Array.AsReadOnly(((object?[])values["unknown_16"]!).Select(value => (int)value!).ToArray()));
    }

    internal static byte[] WriteSpawn(PacketTables packets, string build, RoundFinalZoneTrapSpawned message) =>
        PacketCodec.Write(packets, build, "Area32TrapSpawn", new Dictionary<string, object?>
        {
            ["actor"] = message.Placement.Actor, ["object_id"] = message.ObjectId,
            ["item_spec_id"] = message.Placement.ItemSpecId, ["stage"] = message.Placement.Stage,
            ["x"] = message.Placement.X, ["y"] = message.Placement.Y, ["z"] = message.Placement.Z,
        });

    internal static byte[] WriteLink(PacketTables packets, string build, RoundFinalZoneTrapLinked message) =>
        PacketCodec.Write(packets, build, "Area32TrapLink", new Dictionary<string, object?>
        {
            ["actor"] = message.Actor, ["first_object_id"] = message.FirstObjectId,
            ["second_object_id"] = message.SecondObjectId, ["weapon_id"] = message.WeaponId,
            ["unknown_16"] = message.Unknown16.Cast<object?>().ToArray(),
        });

    internal static void RequireLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "Area32TrapRequest", "FinalZone traps",
            "actor:u32,item_spec_id:u32,stage:u32,x:f32,y:f32,z:f32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32TrapSpawn", "FinalZone traps",
            "actor:u32,object_id:u32,item_spec_id:u32,stage:u32,x:f32,y:f32,z:f32");
        PacketLayoutContract.RequireSignature(packets, build, "Area32TrapLink", "FinalZone traps",
            "actor:u32,first_object_id:u32,second_object_id:u32,weapon_id:u16,unknown_16:list<u32>");
    }

    private sealed record PlacementInput(string Protocol) : IInboundMessageBinding<RoundFinalZoneTrapRequested>;
    private sealed record LinkInput(string Protocol) : IInboundMessageBinding<RoundFinalZoneTrapLinked>;
}
