using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The bomb-mission round's state exchange: it reads the acting client's
/// <c>gcpBombState</c> and <c>gcpBombMissionOver</c> requests through the audited tables and writes the
/// <c>gspBombState</c> the round broadcasts. Who may report which transition, and what a mission over ends,
/// is the round flow's decision; this binding only translates. The layouts are <c>packets/mode-control.yaml</c>
/// and the numbers come from the profile's opcode table.
/// </summary>
internal sealed class BombModeBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundBombStateReported>, IInboundMessageBinding<RoundBombMissionOverReported>,
      IMessageSender<RoundBombStateChanged>
{
    /// <summary>Gets the game connection whose opcode table and typed routes this binding uses.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the audited layouts and registers two requests, the state sender and the death-observation sender.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        var stateOpcode = table.C2S("gcpBombState");
        var overOpcode = table.C2S("gcpBombMissionOver");
        _ = table.S2C("gspBombState");
        _ = table.S2C("gspObserver");
        var services = context.Services;
        var router = services.Get<IPacketRouter>();
        var messages = services.Get<IMessageRouter>();
        context.Effect(router.Register(protocol, stateOpcode, ReadStateAsync));
        context.Effect(router.Register(protocol, overOpcode, ReadMissionOverAsync));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBombStateReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBombMissionOverReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundBombStateChanged>>(this));
        new RoundObservationSender(protocol, table, packets, build).Mount(context);
        logger.LogInformation("dialect.rooms: bomb-mission state exchange of {Build} mounted for {Protocol}.", build, protocol);
        return;

        async ValueTask ReadStateAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var report = ReadState(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No bomb-mission state handler is installed.");
        }

        async ValueTask ReadMissionOverAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var report = ReadMissionOver(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No bomb-mission over handler is installed.");
        }
    }

    /// <summary>Writes the broadcast body through the audited table.</summary>
    public ValueTask SendAsync(ISession session, RoundBombStateChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspBombState"), WriteState(packets, build, message), ct);

    /// <summary>
    /// Reads C2S <c>gcpBombState</c>: five words (operator, state, position), no trailing bytes; the state word
    /// must be one of the client's cases 0..6 (kr-812 sub_4245B0).
    /// </summary>
    internal static RoundBombStateReported ReadState(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(packets, build, "gcpBombState", payload);
        var state = (int)values["state"];
        if (state is < (int)RoundBombState.Reset or > (int)RoundBombState.Defused)
            throw new PacketFormatException($"gcpBombState carries unknown state {state}.", 0);
        return new RoundBombStateReported((RoundBombState)state, (int)values["operator"],
            (float)values["position_x"], (float)values["position_y"], (float)values["position_z"]);
    }

    /// <summary>Reads the native winner byte or word followed by two operator words, with no trailing bytes.</summary>
    internal static RoundBombMissionOverReported ReadMissionOver(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(packets, build, "gcpBombMissionOver", payload);
        return new RoundBombMissionOverReported(Convert.ToInt32(values["winning_team"]),
            (int)values["planter"], (int)values["defuser"]);
    }

    /// <summary>Writes the operator, state and position, with the timer flag only when the native table states it.</summary>
    internal static byte[] WriteState(PacketTables packets, string build, RoundBombStateChanged message) =>
        PacketCodec.Write(packets, build, "gspBombState", new Dictionary<string, object?>
        {
            ["operator"] = message.Operator,
            ["state"] = (int)message.State,
            ["position_x"] = message.PositionX,
            ["position_y"] = message.PositionY,
            ["position_z"] = message.PositionZ,
            ["timer_aligned"] = message.TimerAligned,
        });

    /// <summary>Checks every structure this binding reads or writes.</summary>
    internal static void RequireLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireFields(packets, build, "gcpBombState", "Bomb mission",
            ("operator", "u32"), ("state", "u32"), ("position_x", "f32"), ("position_y", "f32"), ("position_z", "f32"));
        PacketLayoutContract.RequireSignature(packets, build, "gcpBombMissionOver", "Bomb mission",
            "winning_team:u32,planter:u32,defuser:u32", "winning_team:u8,planter:u32,defuser:u32");
        PacketLayoutContract.RequireSignature(packets, build, "gspBombState", "Bomb mission",
            "operator:u32,state:u32,position_x:f32,position_y:f32,position_z:f32,timer_aligned:bool",
            "operator:u32,state:u32,position_x:f32,position_y:f32,position_z:f32");
        RoundObservationSender.RequireLayout(packets, build);
    }
}
