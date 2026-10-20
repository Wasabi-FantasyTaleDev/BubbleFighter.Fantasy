using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.InGame.Tests;

// Records the contract boundary; the adapter's byte layout is pinned by the TCP architecture tests.
internal sealed class RecordingSignatureSender : IMessageSender<MatchSessionSignature>
{
    public string Protocol => "kr-1256/game";
    public List<(long SessionId, ushort Mode)> Sent { get; } = [];
    public ValueTask SendAsync(ISession session, MatchSessionSignature message, CancellationToken ct)
    {
        Sent.Add((session.Id, message.GameMode));
        return session.SendAsync(521, ReadOnlyMemory<byte>.Empty, ct);
    }
}
