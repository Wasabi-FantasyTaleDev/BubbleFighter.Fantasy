using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Maps local region reports and the game snapshot independently of the BFRunner C2S space.</summary>
internal sealed class ControlPointBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IInboundMessageBinding<RoundControlPointChanged>, IControlPointSnapshotSender
{
    public string Protocol => protocol;
    public int NodeCount { get; } = int.TryParse(packets.ValuesOf(build).GetValueOrDefault("control_point_nodes") as string,
        out var count) && count is >= 1 and <= 5 ? count : throw new ConfigException($"Invalid control_point_nodes for {build}.");
    internal void Mount(IPluginContext context)
    {
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        _ = table.S2C("gspControlPointSnapshot_");
        PacketLayoutContract.RequireSignature(packets, build, "ControlPointSnapshot", "Control Point", "nodes:list<ControlPointNode>");
        PacketLayoutContract.RequireSignature(packets, build, "ControlPointNode", "Control Point",
            "owner_team:u32,capturing_team:u32,team_two_seats:list<u32>,team_one_seats:list<u32>,team_two_progress:u32,team_one_progress:u32,contested:bool,owner_highlight:bool");
        MountInput("gcpControlPointChange_", "ControlPointChange", "node:u32");
        if (table.Find(PacketDirection.ClientToServer, "gcpControlPointInitialChange_") is not null)
            MountInput("gcpControlPointInitialChange_", "ControlPointInitialChange", "node:u32,stage_max_node:u32");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundControlPointChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundControlPointsUpdated>>(this));

        void MountInput(string name, string structure, string signature)
        {
            PacketLayoutContract.RequireSignature(packets, build, structure, "Control Point", signature);
            context.Effect(router.Register(protocol, table.C2S(name), async (session, packet, ct) =>
            {
                var fields = PacketCodec.ReadScalars(packets, build, structure, packet.Payload.Span);
                // kr-1256 sub_497380 supplies a stage hint only on first entry. It cannot authorize a capture.
                if (!await messages.DispatchAsync(protocol, session, new RoundControlPointChanged((int)fields["node"]), ct)
                    .ConfigureAwait(false)) throw new InvalidOperationException("No Control Point round handler is installed.");
            }));
        }
    }

    public ValueTask SendAsync(ISession session, RoundControlPointsUpdated message, CancellationToken ct)
    {
        if (message.Nodes.Count != NodeCount) throw new ArgumentException($"The {build} strategy requires {NodeCount} nodes.", nameof(message));
        // kr-1256 sub_CE1E90 indexes names by slot from +12 for team 2 / +24 for team 1.
        // kr-1256 sub_D0E400 draws their respective gauges from +36 / +40.
        var nodes = message.Nodes.Select(p => new Dictionary<string, object?>
        {
            ["owner_team"] = p.Owner, ["capturing_team"] = p.CapturingTeam,
            ["team_two_seats"] = p.SecondTeamSeats, ["team_one_seats"] = p.FirstTeamSeats,
            ["team_two_progress"] = p.SecondTeamProgress, ["team_one_progress"] = p.FirstTeamProgress,
            // kr-1256 sub_D12360 / cn-774 sub_DAC5D0 / tw-454 sub_D60930: contested blink and owned icon brightness.
            ["contested"] = p.FirstTeamSeats.Count != 0 && p.SecondTeamSeats.Count != 0,
            ["owner_highlight"] = p.Owner == 1 ? p.SecondTeamSeats.Count == 0 : p.Owner == 2 && p.FirstTeamSeats.Count == 0,
        });
        var body = PacketCodec.Write(packets, build, "ControlPointSnapshot", new Dictionary<string, object?> { ["nodes"] = nodes });
        return session.SendAsync(table.S2C("gspControlPointSnapshot_"), body, ct);
    }
}
