using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed class PvePointsBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundPvePangReported>, IMessageSender<RoundPvePointsChanged>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "pve_points"))
            throw new ConfigException("pve_points requires an audited native score-unit and point-delta capability.");
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
