using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Serves the <c>pve_points</c> and <c>pve_rank_updates</c> capabilities: the reported unit, the point
/// delta and the rank rows are the structures audited in packets/pve-points.yaml. The delta is three
/// words (kr-812 <c>sub_9D95D0</c>, cn-717 <c>sub_7F4370</c>, id-400 <c>sub_72E4D0</c>, id-1809
/// <c>sub_4D8130</c>, cn-406 <c>sub_715B70</c>, cn-324 <c>sub_6EB8B0</c>) whose consumers are the live
/// row updates and local HUD sorts (kr-812 <c>sub_7FED80</c>, <c>sub_806790</c>), and the rank vector
/// is read by kr-812 <c>sub_80BE00</c>/<c>sub_80BDC0</c> with cn-717, id-400, id-1809 and cn-324
/// matching.
/// </summary>
internal sealed class PvePointsBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundPvePangReported>, IMessageSender<RoundPvePointsChanged>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "pve_points"))
            throw new ConfigException("pve_points requires an audited native score-unit and point-delta capability.");
        // Every field the binding casts or sizes is checked against the build's own table at load: the reported
        // unit index, the point delta it writes, the nested row vector and the row fields it fills.
        PacketLayoutContract.RequireFields(tables, build, "PvePangUnit", "Cooperative points", ("unit_index", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "PveGamePoint", "Cooperative points",
            ("seat", "u32"), ("subtract", "u32"), ("amount", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "PveRankInfo", "Cooperative ranking",
            ("rows", "list<PveRankRow>"));
        PacketLayoutContract.RequireFields(tables, build, "PveRankRow", "Cooperative ranking",
            ("seat", "u32"), ("unknown_8", "u32"), ("rank", "u32"));
        foreach (var name in new[] { "PvePangUnit", "PveGamePoint", "PveRankRow", "PveRankInfo" }) _ = tables.Resolve(build, name);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundPvePangReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundPvePointsChanged>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, table.C2S("gcpPvePangUnit"), async (session, packet, ct) =>
        {
            var words = PacketCodec.ReadScalars(tables, build, "PvePangUnit", packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, new RoundPvePangReported((int)words["unit_index"]), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Cooperative points have no typed handler.");
        }));
    }

    public async ValueTask SendAsync(ISession session, RoundPvePointsChanged message, CancellationToken ct)
    {
        // cn-324 sub_6EE7A0 increments the kill counter even for zero; initialization sends ranks only.
        if (message.Amount > 0)
            await session.SendAsync(table.S2C("gspNotifyGamePoint"), PacketCodec.Write(tables, build, "PveGamePoint",
                new Dictionary<string, object?> { ["seat"] = message.Slot, ["subtract"] = 0, ["amount"] = message.Amount }), ct).ConfigureAwait(false);
        // kr-812 sub_806790 sorts the live rows locally; sub_7FEE30 consumes result presentation instead.
        if (tables.CapabilityOf(build, "pve_rank_updates"))
            await session.SendAsync(table.S2C("gspNotifyRankInfo"), EncodeRanks(tables, build, message.Rows), ct).ConfigureAwait(false);
    }

    internal static byte[] EncodeRanks(PacketTables tables, string build, IReadOnlyList<RoundPvePointRow> rows)
    {
        if (rows.Count > 32 || rows.Any(row => row.Slot is < 0 or >= 32 || row.Points < 0 || row.Rank is < 1 or > 32))
            throw new InvalidOperationException("Cooperative ranking exceeds the native seat bound.");
        return PacketCodec.Write(tables, build, "PveRankInfo", new Dictionary<string, object?>
        {
            ["rows"] = rows.Select(row => new Dictionary<string, object?>
            {
                ["seat"] = row.Slot, ["unknown_8"] = row.Points, ["rank"] = row.Rank,
            }).ToArray(),
        });
    }
}
