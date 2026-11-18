using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Translates the two lobby queries for a dynamic-config Pirate Assault board.</summary>
internal sealed class PirateAssaultRankingBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IInboundMessageBinding<PirateAssaultRankingRequested>, IMessageSender<PirateAssaultRankingPage>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        const string consumer = "Pirate Assault lobby rankings";
        PacketLayoutContract.RequireSignature(packets, build, "PirateAssaultRankingRequest", consumer, "rank_id:u32");
        PacketLayoutContract.RequireSignature(packets, build, "PirateAssaultRankingReply", consumer,
            "rank_id:u32,rows:list<PirateAssaultRankingRow>,table_time:f64");
        PacketLayoutContract.RequireSignature(packets, build, "PirateAssaultRankingRow", consumer,
            "unknown_0:u32,name:wstr,unknown_28:u32,unknown_32:u32,clear_time_ms:u32,rank:u32");
        var top = table.C2S("gcpReqPirateAssaultTopRanking_");
        var personal = table.C2S("gcpReqPirateAssaultUserRanking_");
        _ = ReplyOpcode(false);
        _ = ReplyOpcode(true);
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        Bind(top, false);
        Bind(personal, true);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PirateAssaultRankingRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PirateAssaultRankingPage>>(this));

        void Bind(ushort opcode, bool ownPage) =>
            context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
            {
                var request = Read(packets, build, packet.Payload.Span, ownPage);
                if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No Pirate Assault lobby ranking handler is installed.");
            }));
    }

    private ushort ReplyOpcode(bool personal) => table.S2C(personal
        ? "gspRepGetPirateAssaultUserRanking" : "gspRepGetPirateAssaultTopRanking");

    public ValueTask SendAsync(ISession session, PirateAssaultRankingPage message, CancellationToken ct) =>
        session.SendAsync(ReplyOpcode(message.Key.UserNum.HasValue), Write(packets, build, message), ct);

    internal static PirateAssaultRankingRequested Read(PacketTables packets, string build, ReadOnlySpan<byte> body, bool personal)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "PirateAssaultRankingRequest", body);
        return new(build, unchecked((uint)(int)fields["rank_id"]), personal);
    }

    internal static byte[] Write(PacketTables packets, string build, PirateAssaultRankingPage page)
    {
        if (page.Key.Build != build || !double.IsFinite(page.TableTime))
            throw new InvalidOperationException("The Pirate Assault page must match the build and have a finite source date.");
        // Even an empty name needs its count and five words. Reject an unrepresentable source page before projecting its rows.
        if (page.Rows.Count > (ushort.MaxValue - 16) / 24)
            throw new ArgumentOutOfRangeException(nameof(page), "The complete Pirate Assault page exceeds the packet bound.");
        return PacketCodec.Write(packets, build, "PirateAssaultRankingReply", new Dictionary<string, object?>
        {
            ["rank_id"] = page.Key.RankId,
            ["table_time"] = page.TableTime,
            ["rows"] = page.Rows.Select(row => new Dictionary<string, object?>
            {
                ["unknown_0"] = row.Unknown0, ["name"] = row.Name, ["unknown_28"] = row.Unknown28,
                ["unknown_32"] = row.Unknown32, ["clear_time_ms"] = row.ClearTimeMilliseconds, ["rank"] = row.Rank,
            }),
        });
    }
}
