using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Decodes script map effects on either native input link and echoes approved effects on the game link.</summary>
internal sealed class FinalZoneMapEffectBinding(string gameProtocol, string relayProtocol,
    IOpcodeTable game, IOpcodeTable relay, PacketTables packets, string build)
    : IMessageSender<RoundFinalZoneMapEffectChanged>
{
    /// <summary>Gets the game protocol whose peer dispatcher also consumes map effects.</summary>
    public string Protocol => gameProtocol;

    internal void Mount(IPluginContext context)
    {
        RequireLayout(packets, build);
        var gameInbound = game.C2S("gppArea32MapEffect_");
        var relayInbound = relay.C2S("gppArea32MapEffect_");
        _ = game.S2C("gppArea32MapEffect_");
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        Register(gameProtocol, gameInbound);
        Register(relayProtocol, relayInbound);
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundFinalZoneMapEffectChanged>>(this));

        void Register(string protocol, ushort opcode)
        {
            context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
            {
                var message = Read(packets, build, packet.Payload.Span);
                if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No map-script effect handler is installed.");
            }));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundFinalZoneMapEffectChanged>>(new Input(protocol)));
        }
    }

    /// <summary>Echoes the accepted script value, including to the master that produced it.</summary>
    public ValueTask SendAsync(ISession session, RoundFinalZoneMapEffectChanged message, CancellationToken ct) =>
        session.SendAsync(game.S2C("gppArea32MapEffect_"), Write(packets, build, message), ct);

    internal static void RequireLayout(PacketTables packets, string build) =>
        PacketLayoutContract.RequireSignature(packets, build, "Area32MapEffect", "map-script effects",
            "dummy:wstr,effect:wstr,sound:wstr,visible:bool,unknown_76:u32,sequence:u32");

    internal static RoundFinalZoneMapEffectChanged Read(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(packets, build, "Area32MapEffect", body);
        return new((string)values["dummy"], (string)values["effect"], (string)values["sound"],
            (bool)values["visible"], (int)values["unknown_76"], (int)values["sequence"]);
    }

    internal static byte[] Write(PacketTables packets, string build, RoundFinalZoneMapEffectChanged message) =>
        PacketCodec.Write(packets, build, "Area32MapEffect", new Dictionary<string, object?>
        {
            ["dummy"] = message.Dummy, ["effect"] = message.Effect, ["sound"] = message.Sound,
            ["visible"] = message.Visible, ["unknown_76"] = message.Unknown76, ["sequence"] = message.Sequence,
        });

    private sealed record Input(string Protocol) : IInboundMessageBinding<RoundFinalZoneMapEffectChanged>;
}
