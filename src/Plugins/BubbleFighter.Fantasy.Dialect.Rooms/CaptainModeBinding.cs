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
            context.Effect(services.RegisterMany<IInboundMessageBinding<RoundFinishingBlowReported>>(this));
            context.Effect(router.Register(protocol, table.C2S("gcpGameUpdateScore"), async (session, packet, ct) =>
            {
                RoundFinishingBlowReported? report;
                try { report = scores.Read(packet.Payload.Span); }
                catch (PacketFormatException ex)
                {
                    logger.LogWarning("Session {Id} Captain score does not match the expected layout ({Reason}); ignored.",
                        session.Id, ex.Message);
                    return;
                }
                if (report is not null && !await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No Captain score handler is installed.");
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
