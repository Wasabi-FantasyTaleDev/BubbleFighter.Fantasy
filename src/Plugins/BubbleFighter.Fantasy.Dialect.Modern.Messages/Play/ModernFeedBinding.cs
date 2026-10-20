using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Dialects;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>
/// The modern typed round's gameplay feed: the in-round actions, the damager spawns and the relay motion frames the
/// server passes between the players unread. They arrive on the game or the match relay connection and go to the
/// other players' relay connections; the round flow subscribes on the game protocol, so relay frames are dispatched
/// there too (the room registry knows both connections of a seat).
/// </summary>
internal sealed class ModernFeedBinding(string protocol, string relayProtocol)
    : IInboundMessageBinding<RoundFeedSent>, IMessageSender<RoundFeedReceived>, IInboundMessageBinding<RoundLifeReported>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundFeedSent>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundFeedReceived>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundLifeReported>>(this));
        // Each action's layout is checked and its actor words read (kr-1256 sub_6D55D0 readers); actions the
        // sender's own client also applies on receipt come back to it as well. The life actions also report
        // what happened to a character, after the packet itself has gone on.
        foreach (var action in MatchAction1256.All)
            Register(context, action.InboundOpcode, body => Action(action, body), relay: true, game: true, Life);
        // A relayed hit resolves only against a damager the receiving client minted from the same spawn frames,
        // so every spawn goes on unchanged and in order.
        foreach (var code in InGameOpcodes.DamagerSpawns)
            Register(context, code, body => new RoundFeedSent(code, body.ToArray()) { OnRelay = true }, relay: true, game: true, null);
        // The relay motion frames (kr-1256 sub_95FAE0 writes 20003 and 20004) come only on the relay.
        foreach (var code in new[] { InGameOpcodes.RelayMotion, InGameOpcodes.RelayWander })
            Register(context, code, body => new RoundFeedSent(code, body.ToArray()) { OnRelay = true }, relay: true, game: false, null);
    }

    /// <summary>A feed packet goes out under the number it came in with.</summary>
    public ValueTask SendAsync(ISession session, RoundFeedReceived message, CancellationToken ct) =>
        session.SendAsync(message.Code, message.Payload, ct);

    private static RoundFeedSent Action(MatchAction1256 action, ReadOnlySpan<byte> body)
    {
        var checkedAction = MatchAction1256.Read(action.InboundOpcode, body);
        List<int>? actors = null;
        if (checkedAction.ActorOffset >= 0)
        {
            actors = [BinaryPrimitives.ReadInt32LittleEndian(body[checkedAction.ActorOffset..])];
            if (checkedAction.AlternateActorOffset >= 0)
                actors.Add(BinaryPrimitives.ReadInt32LittleEndian(body[checkedAction.AlternateActorOffset..]));
        }
        return new RoundFeedSent(checkedAction.OutboundOpcode, body.ToArray())
        {
            OnRelay = true, IncludeSender = checkedAction.EchoSender, ReportedActors = actors,
        };
    }

    // The character-life meaning of the life actions, as the packet relay path reads them (kr-1256 readers
    // 0x6F8280, 0x4A6F90, 0x6F7B30, 0x6D97D0, 0x6E2CA0, 0x6FAC30, 0x6F6900, 0x6F6200); null for the others.
    private static RoundLifeReported? Life(ushort code, ReadOnlySpan<byte> payload)
    {
        var body = payload.ToArray();
        int Word(int offset) => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset));
        return code switch
        {
            // Victim, source, position, a word, then whether the hit killed outright (+24) rather than bubbled.
            38282 => new(Word(24) == 0 ? RoundLifeEvent.Bubbled : RoundLifeEvent.Died, Word(0)),
            38281 => new(RoundLifeEvent.Bubbled, Word(0)),
            38272 or 38273 => new(RoundLifeEvent.Released, Word(0)),
            38274 => new(RoundLifeEvent.BubbleExpired, Word(0)),
            // The pop applies only when its first word succeeds; then the victim and the popper.
            38277 when Word(0) != 0 => new(RoundLifeEvent.Finished, Word(4)) { Initiator = Word(8) },
            32836 => new(RoundLifeEvent.DeathCleaned, Word(0)),
            32926 => new(RoundLifeEvent.ResetConfirmed, Word(0)),
            _ => null,
        };
    }

    private void Register(IPluginContext context, ushort code, Func<ReadOnlySpan<byte>, RoundFeedSent> read, bool relay, bool game,
        Func<ushort, ReadOnlySpan<byte>, RoundLifeReported?>? life)
    {
        var packets = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        PacketHandler handler = async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, read(packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("No round feed handler is installed.");
            if (life?.Invoke(code, packet.Payload.Span) is { } report)
                await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false);
        };
        if (game)
            context.Effect(packets.Register(protocol, code, handler));
        if (relay)
            context.Effect(packets.Register(relayProtocol, code, handler));
    }
}
