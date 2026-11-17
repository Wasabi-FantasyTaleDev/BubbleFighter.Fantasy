using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The Captain round's exchange: it reads the dying client's <c>gcpDropCaptain</c> and
/// writes the <c>gspCreateCaptain</c> that names a team's Captain. Which seat is Captain, and whether a report
/// is a Captain's fall, is the round flow's decision; this binding only translates. The layouts are
/// <c>packets/mode-control.yaml</c> and the numbers come from the profile's opcode table.
/// </summary>
internal sealed class CaptainModeBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundCaptainDropped>, IInboundMessageBinding<RoundFinishingBlowReported>,
      IInboundMessageBinding<RoundSoccerGoalReported>,
      IMessageSender<RoundCaptainAppointed>
{
    /// <summary>Gets the game connection whose opcode table and typed routes this binding uses.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the audited layouts and registers the death report and the announcement sender.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        var dropOpcode = table.C2S("gcpDropCaptain");
        _ = table.S2C("gspCreateCaptain");
        var services = context.Services;
        var router = services.Get<IPacketRouter>();
        var messages = services.Get<IMessageRouter>();
        context.Effect(router.Register(protocol, dropOpcode, ReadDropAsync));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundCaptainDropped>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundCaptainAppointed>>(this));
        if (packets.Resolve(build, "RoomEnterGame").Any(field => field.Name == "room_state"))
        {
            // kr-1256 sub_46FB10 / cn-774 sub_4EB780 / tw-454 sub_56E420 use the separate full-score route.
            var scores = new CaptainScoreCodec(packets, build);
            // The soccer host reports its goals on the same full-score route (kr-1256 sub_6E6F90 kind 42; cn-774
            // sub_8A75F0 and tw-454 sub_71A9A0 kind 21): the goal box at unknown_8, the last toucher's seat as reporter.
            var soccerGoal = PlayValues.Of(packets, build).Score.SoccerGoal;
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinishingBlowReported>>(this));
            if (soccerGoal > 0)
                context.Effect(services.RegisterMany<IInboundMessageBinding<RoundSoccerGoalReported>>(this));
            context.Effect(router.Register(protocol, table.C2S("gcpGameUpdateScore"), async (session, packet, ct) =>
            {
                IMessage? report;
                try
                {
                    var fields = PacketCodec.ReadScalars(packets, build, "RoundScoreReport", packet.Payload.Span);
                    report = soccerGoal > 0 && Convert.ToInt32(fields["kind"]) == soccerGoal
                        ? new RoundSoccerGoalReported(unchecked((int)fields["unknown_8"]), Convert.ToInt32(fields["reporter_slot"]))
                        : scores.Read(fields);
                }
                catch (PacketFormatException ex)
                {
                    logger.LogWarning("Session {Id} full score report does not match the expected layout ({Reason}); ignored.",
                        session.Id, ex.Message);
                    return;
                }
                var handled = report switch
                {
                    null => true,
                    RoundSoccerGoalReported goal => await messages.DispatchAsync(protocol, session, goal, ct).ConfigureAwait(false),
                    RoundFinishingBlowReported kill => await messages.DispatchAsync(protocol, session, kill, ct).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("The full score route decoded an unsupported report."),
                };
                if (!handled)
                    throw new InvalidOperationException("No handler is installed for the full score report.");
            }));
        }
        logger.LogInformation("dialect.rooms: Captain exchange of {Build} mounted for {Protocol}.", build, protocol);
        return;

        async ValueTask ReadDropAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var report = ReadDrop(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No Captain death handler is installed.");
        }
    }

    /// <summary>Writes the announcement body through the audited table.</summary>
    public ValueTask SendAsync(ISession session, RoundCaptainAppointed message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspCreateCaptain"), WriteAppointed(packets, build, message), ct);

    /// <summary>Reads C2S <c>gcpDropCaptain</c>: one word, the dying player's own seat, no trailing bytes.</summary>
    internal static RoundCaptainDropped ReadDrop(PacketTables packets, string build, ReadOnlySpan<byte> payload) =>
        new((int)PacketCodec.ReadScalars(packets, build, "gcpDropCaptain", payload)["seat"]);

    /// <summary>Writes S2C <c>gspCreateCaptain</c>: one word, the Captain's seat.</summary>
    internal static byte[] WriteAppointed(PacketTables packets, string build, RoundCaptainAppointed message) =>
        PacketCodec.Write(packets, build, "gspCreateCaptain", new Dictionary<string, object?> { ["seat"] = message.Slot });

    /// <summary>Checks every structure this binding reads or writes.</summary>
    internal static void RequireLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireFields(packets, build, "gcpDropCaptain", "Captain mode", ("seat", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "gspCreateCaptain", "Captain mode", ("seat", "u32"));
    }
}
