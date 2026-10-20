using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed class LotteryBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IInboundMessageBinding<RoundLotteryRequested>, IMessageSender<RoundLotteryOpened>, IMessageSender<RoundLotteryClosed>,
      IMessageSender<RoundLotterySelected>, IMessageSender<RoundLotteryRevealed>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "horde_lottery"))
            throw new ConfigException("horde_lottery requires an audited native award dialog, selection and close path.");
        foreach (var name in new[] { "HordeLotteryRequest", "HordeLotterySelected", "HordeLotteryRevealed" })
            _ = tables.Resolve(build, name);
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
