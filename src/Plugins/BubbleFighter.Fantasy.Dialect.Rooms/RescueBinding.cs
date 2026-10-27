using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Serves the <c>rescue_missions</c> capability for the builds whose audited readers match: the
/// mission progress request comes in as C2S <c>gcpMissionProcess</c>, and the dialect publishes
/// S2C <c>gspMapMissionInfo</c>, <c>gspResMissionStarted</c>, <c>gspResMissionTimeOut</c> and
/// <c>gspResMissionOK</c>. Layouts and per-build readers live in packets/pve-rescue-missions.yaml —
/// the baseline readers are kr-812 <c>sub_7A38E0</c> (mission), <c>sub_80C080</c> (entries),
/// <c>sub_80A910</c> (started/timeout), <c>sub_6DB1B0</c> (succeeded) and <c>sub_9D95D0</c>
/// (progress), with cn-717, id-400, id-1809, cn-706 and cn-324 matching on the same wire fields;
/// cn-279 has no such class.
/// </summary>
internal sealed class RescueBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundRescueProgressReported>, IMessageSender<RoundRescueMissionsPrepared>,
      IMessageSender<RoundRescueMissionStarted>, IMessageSender<RoundRescueMissionFailed>, IMessageSender<RoundRescueMissionSucceeded>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "rescue_missions"))
            throw new ConfigException("rescue_missions requires audited native mission consumers and map groups.");
        // Every field the binding reads or writes is checked against the build's own table at load: the progress
        // triple, the three single-index replies, the prepared list and the mission entry it fills.
        PacketLayoutContract.RequireFields(tables, build, "PveRescueMissionProgress", "Cooperative rescue",
            ("mission_index", "u32"), ("character_type", "u32"), ("unknown_12", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "PveRescueMissionStarted", "Cooperative rescue",
            ("mission_index", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "PveRescueMissionTimedOut", "Cooperative rescue",
            ("next_mission_index", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "PveRescueMissionSucceeded", "Cooperative rescue",
            ("mission_index", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "PveRescueMissionsPrepared", "Cooperative rescue",
            ("missions", "list<PveRescueMission>"));
        PacketLayoutContract.RequireFields(tables, build, "PveRescueMission", "Cooperative rescue",
            ("mission_id", "u32"), ("start_time_ms", "u32"), ("duration_ms", "u32"), ("arguments", "list<wstr>"));
        foreach (var name in new[] { "PveRescueMission", "PveRescueMissionsPrepared", "PveRescueMissionStarted",
            "PveRescueMissionTimedOut", "PveRescueMissionSucceeded", "PveRescueMissionProgress" })
            _ = tables.Resolve(build, name);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundRescueProgressReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundRescueMissionsPrepared>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundRescueMissionStarted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundRescueMissionFailed>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundRescueMissionSucceeded>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, table.C2S("gcpMissionProcess"), async (session, packet, ct) =>
        {
            var report = ReadProgress(tables, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Cooperative rescue progress has no typed handler.");
        }));
    }

    internal static RoundRescueProgressReported ReadProgress(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var words = PacketCodec.ReadScalars(tables, build, "PveRescueMissionProgress", body);
        if ((int)words["unknown_12"] != 0)
            throw new PacketFormatException("Native rescue progress requires a zero trailing word.", body.Length - 4);
        return new((int)words["mission_index"], (int)words["character_type"]);
    }

    public ValueTask SendAsync(ISession session, RoundRescueMissionsPrepared message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspMapMissionInfo"), EncodePrepared(tables, build, message.Missions), ct);

    public ValueTask SendAsync(ISession session, RoundRescueMissionStarted message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspResMissionStart"), PacketCodec.Write(tables, build, "PveRescueMissionStarted",
            new Dictionary<string, object?> { ["mission_index"] = message.MissionIndex }), ct);

    public ValueTask SendAsync(ISession session, RoundRescueMissionFailed message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspResMissionTimeOut"), PacketCodec.Write(tables, build, "PveRescueMissionTimedOut",
            new Dictionary<string, object?> { ["next_mission_index"] = message.NextMissionIndex }), ct);

    public ValueTask SendAsync(ISession session, RoundRescueMissionSucceeded message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspResMissionOK"), PacketCodec.Write(tables, build, "PveRescueMissionSucceeded",
            new Dictionary<string, object?> { ["mission_index"] = message.MissionIndex }), ct);

    internal static byte[] EncodePrepared(PacketTables tables, string build, IReadOnlyList<RoundRescueMission> missions)
    {
        if (missions.Count > 6) throw new InvalidOperationException("Rescue definitions exceed the native map mission bound.");
        foreach (var mission in missions)
        {
            if (mission.MissionId <= 0 || mission.StartTimeMs < 0 || mission.DurationMs <= 0
                || mission.Arguments.Count > 4 || mission.Arguments.Any(argument => argument.Length > 64))
                throw new InvalidOperationException("Rescue definition is invalid or exceeds its wire bounds.");
        }
        return PacketCodec.Write(tables, build, "PveRescueMissionsPrepared", new Dictionary<string, object?>
        {
            ["missions"] = missions.Select(mission => new Dictionary<string, object?>
            {
                ["mission_id"] = mission.MissionId, ["start_time_ms"] = mission.StartTimeMs,
                ["duration_ms"] = mission.DurationMs, ["arguments"] = mission.Arguments,
            }).ToArray(),
        });
    }
}
