using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed class RescueBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundRescueProgressReported>, IMessageSender<RoundRescueMissionsPrepared>,
      IMessageSender<RoundRescueMissionStarted>, IMessageSender<RoundRescueMissionFailed>, IMessageSender<RoundRescueMissionSucceeded>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "rescue_missions"))
            throw new ConfigException("rescue_missions requires audited native mission consumers and map groups.");
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
