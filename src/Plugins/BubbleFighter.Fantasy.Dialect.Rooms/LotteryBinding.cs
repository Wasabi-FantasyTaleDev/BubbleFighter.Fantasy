using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Serves the <c>horde_lottery</c> capability: the card-and-seat request (kr-812 <c>sub_840530</c>
/// writes, kr-812 <c>sub_6DB1B0</c> reads; cn-717, id-400, id-1809, cn-706 and cn-324 match), the
/// three-word selection (kr-812 <c>sub_7AA780</c>/<c>sub_9D95D0</c>; cn-324 <c>sub_420900</c> consumes
/// card, seat and the optional second card), and the counted reveal vector (kr-812 <c>sub_840820</c>,
/// with cn-717, cn-324, cn-279, id-400, id-1809 and cn-706 matching) — layouts and per-build readers
/// in packets/horde-lottery.yaml.
/// </summary>
internal sealed class LotteryBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundLotteryRequested>, IMessageSender<RoundLotteryOpened>, IMessageSender<RoundLotteryClosed>,
      IMessageSender<RoundLotterySelected>, IMessageSender<RoundLotteryRevealed>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "horde_lottery"))
            throw new ConfigException("horde_lottery requires an audited native award dialog, selection and close path.");
        // The request card and seat this handler casts, the selection it writes, and the reveal vector it sizes.
        PacketLayoutContract.RequireFields(tables, build, "HordeLotteryRequest", "Horde lottery", ("card", "u32"), ("seat", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "HordeLotterySelected", "Horde lottery", ("card", "u32"), ("seat", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "HordeLotteryRevealed", "Horde lottery", ("items", "list<u32>"));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundLotteryRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundLotteryOpened>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundLotteryClosed>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundLotterySelected>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundLotteryRevealed>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, table.C2S("gcpReqSelectAward"), async (session, packet, ct) =>
        {
            var request = Read(tables, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Reward-card selection has no typed handler.");
        }));
    }

    internal static RoundLotteryRequested Read(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "HordeLotteryRequest", body);
        return new((int)values["card"], (int)values["seat"]);
    }

    public ValueTask SendAsync(ISession session, RoundLotteryOpened message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspGameAwardDialog"), ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendAsync(ISession session, RoundLotteryClosed message, CancellationToken ct) =>
        // cn-324 sub_6F2390 dispatches event 177 before restoring the room stage.
        session.SendAsync(table.S2C("gspGameReturnAfterAward_"), ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendAsync(ISession session, RoundLotterySelected message, CancellationToken ct) =>
        message.Rejected
            ? session.SendAsync(table.S2C("gspRepAwardSelectFail"), ReadOnlyMemory<byte>.Empty, ct)
            : session.SendAsync(table.S2C("gspRepAwardSelectSuccess"), PacketCodec.Write(
                tables, build, "HordeLotterySelected", new Dictionary<string, object?> { ["card"] = message.Card, ["seat"] = message.Seat }), ct);

    public ValueTask SendAsync(ISession session, RoundLotteryRevealed message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspHordeModeRewardList"), WriteReveal(tables, build, message.Items), ct);

    internal static byte[] WriteReveal(PacketTables tables, string build, IReadOnlyList<int> items)
    {
        var fields = tables.Resolve(build, "HordeLotteryRevealed");
        if (items.Count != 10 || fields.Count != 1 || fields[0].Type != "list<u32>" || fields[0].Name != "items")
            throw new ConfigException("Reward-card reveal requires the native ten-card vector.");
        return PacketCodec.Write(tables, build, "HordeLotteryRevealed", new Dictionary<string, object?> { ["items"] = items });
    }
}
