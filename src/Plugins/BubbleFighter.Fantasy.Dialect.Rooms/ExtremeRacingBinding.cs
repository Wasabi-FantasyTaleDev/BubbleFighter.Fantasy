using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The ExtremeRacing round's exchange (round mode 24): it reads the goal touch and writes the race clocks, the round
/// change and the record updates through <c>packets/extreme-racing.yaml</c>, numbered by the profile's opcode table.
/// Which round runs, when a clock starts and how records are scored is the round flow's decision. The checkpoint
/// touch is a round-feed route in <c>packets/builds.yaml</c>, not part of this binding.
/// </summary>
internal sealed class ExtremeRacingBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundExtremeRacingGoalReported>, IMessageSender<RoundExtremeRacingTimerChanged>,
      IMessageSender<RoundExtremeRacingRoundChanged>, IMessageSender<RoundExtremeRacingRecordsUpdated>
{
    // kr-1256 sub_CE6BA0 scope 1: every record, the ranking board and the team round totals.
    private const int AllRecordsScope = 1;

    /// <summary>Gets the game connection whose opcode table this binding uses.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the audited layouts, then registers the goal request and the three senders.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        var goal = table.C2S("gcpExtremeRacingGoal_");
        _ = table.S2C("gspExtremeRacingTimer_");
        _ = table.S2C("gspExtremeRacingRound_");
        _ = table.S2C("gspExtremeRacingUpdatePlayRecord_");
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        context.Effect(services.Get<IPacketRouter>().Register(protocol, goal, async (session, packet, ct) =>
        {
            var report = ReadGoal(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No ExtremeRacing goal handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundExtremeRacingGoalReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundExtremeRacingTimerChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundExtremeRacingRoundChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundExtremeRacingRecordsUpdated>>(this));
        logger.LogInformation("dialect.rooms: ExtremeRacing exchange of {Build} mounted for {Protocol}.", build, protocol);
    }

    /// <summary>Writes a race clock change.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingTimerChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingTimer_"), WriteTimer(packets, build, message), ct);

    /// <summary>Writes a race round change.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingRoundChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingRound_"), WriteRound(packets, build, message), ct);

    /// <summary>Writes record updates for every listed seat.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingRecordsUpdated message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingUpdatePlayRecord_"), WriteRecords(packets, build, message), ct);

    /// <summary>Reads the one-word goal touch, with no trailing bytes.</summary>
    internal static RoundExtremeRacingGoalReported ReadGoal(PacketTables packets, string build, ReadOnlySpan<byte> payload) =>
        new((int)PacketCodec.ReadScalars(packets, build, "ExtremeRacingGoal", payload)["round"]);

    /// <summary>Writes the code, the duration and the activity word.</summary>
    internal static byte[] WriteTimer(PacketTables packets, string build, RoundExtremeRacingTimerChanged message) =>
        PacketCodec.Write(packets, build, "ExtremeRacingTimer", new Dictionary<string, object?>
        {
            ["code"] = (int)message.Timer,
            ["duration_ms"] = message.DurationMs,
            ["active"] = message.Active,
        });

    /// <summary>Writes the round word after the unused leading word, which keeps its table default.</summary>
    internal static byte[] WriteRound(PacketTables packets, string build, RoundExtremeRacingRoundChanged message) =>
        PacketCodec.Write(packets, build, "ExtremeRacingRound", new Dictionary<string, object?> { ["round"] = message.Round });

    /// <summary>Writes the all-records scope and each seat's field updates.</summary>
    internal static byte[] WriteRecords(PacketTables packets, string build, RoundExtremeRacingRecordsUpdated message) =>
        PacketCodec.Write(packets, build, "ExtremeRacingRecords", new Dictionary<string, object?>
        {
            ["scope"] = AllRecordsScope,
            ["records"] = message.Records.Select(record => (object?)new Dictionary<string, object?>
            {
                ["seat"] = checked((ushort)record.Seat),
                ["values"] = record.Values.Select(value => (object?)new Dictionary<string, object?>
                {
                    ["field"] = checked((byte)value.Field),
                    ["value"] = value.Value,
                }).ToArray(),
            }).ToArray(),
        });

    /// <summary>Checks every structure this binding reads or writes.</summary>
    internal static void RequireLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingTimer", "ExtremeRacing",
            "code:u32,duration_ms:u32,active:bool");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRound", "ExtremeRacing",
            "unknown_0:u32,round:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingGoal", "ExtremeRacing", "round:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRecords", "ExtremeRacing",
            "scope:u32,records:list<ExtremeRacingRecord>");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRecord", "ExtremeRacing",
            "seat:u16,values:list<ExtremeRacingRecordValue>");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRecordValue", "ExtremeRacing",
            "field:u8,value:u32");
    }
}
