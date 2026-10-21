using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Answers the kr-1256 family's main-menu probe: C2S 521 (builders <c>kr-1256 sub_937560</c> and
/// <c>sub_937920</c>, empty body, sent twice by the main-menu stage constructor) with the empty
/// <c>gspSessionSignatureData</c> body (consumer <c>kr-1256 sub_9EBD80</c> -&gt; <c>kr-1256 sub_1155B00</c>). The lobby's
/// main-menu handler publishes the reply through this row, so the probe answers with the same bytes the former
/// modern login codec sent.
/// </summary>
internal sealed class MainMenuInitBinding(string protocol, PacketTables tables, string build, ushort replyOpcode)
    : IInboundMessageBinding<MainMenuInitRequest>, IMessageSender<EmptySessionSignature>
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, EmptySessionSignature message, CancellationToken ct) =>
        session.SendAsync(replyOpcode, LoginBurstWire.WriteSessionSignatureEmpty(tables, build), ct);
}
