using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Answers legacy C2S <c>gcpReqGotoLobby</c> with S2C <c>gspGotoLobby</c> bool32 0.
/// Nonzero would toast the client "party moved to lobby" string; stage 3 does not need it.
/// The client only sends the request after idle <c>gspUpdateParty</c>. This reply does not
/// open a public room. The login path's S2C 110 kind 1 is unchanged.
/// </summary>
internal sealed class GotoLobbyHandler
{
    private readonly ushort _reply;
    private readonly ILogger _logger;

    /// <summary>Binds the profile's <c>gspGotoLobby</c> opcode.</summary>
    /// <param name="reply">The S2C opcode from the profile table.</param>
    /// <param name="logger">The dialect row logger.</param>
    internal GotoLobbyHandler(ushort reply, ILogger logger)
    {
        _reply = reply;
        _logger = logger;
    }

    /// <summary>Rejects a non-empty body and replies with bool32 0 (no party-moved toast).</summary>
    /// <param name="session">The requesting session.</param>
    /// <param name="packet">The C2S <c>gcpReqGotoLobby</c> frame.</param>
    /// <param name="ct">Cancellation token.</param>
    internal ValueTask HandleAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        GotoLobbyCodec.ReadRequest(packet.Payload.Span);
        _logger.LogInformation(
            "Session {Id} C2S gcpReqGotoLobby; replying gspGotoLobby notice=0.",
            session.Id);
        return session.SendAsync(_reply, GotoLobbyCodec.WriteSuccess(), ct);
    }
}
