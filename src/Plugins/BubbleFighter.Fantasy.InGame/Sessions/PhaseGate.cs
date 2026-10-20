using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// Declares in which <see cref="SessionPhase"/> a game-connection request is expected and reports
/// requests that arrive in another phase. It observes only: the handler still runs, because the
/// declared sets must be confirmed before they may reject a
/// packet. Connections without a player (the relay connection) are not checked.
/// </summary>
internal sealed class PhaseGate(ILogger logger)
{
    private const int MaxTrackedMismatches = 1024;
    private readonly ConcurrentDictionary<(string Request, SessionPhase Phase), long> _mismatches = new();

    /// <summary>Wraps <paramref name="handler"/> so a request outside <paramref name="expected"/> is reported.</summary>
    public PacketHandler Expect(ushort opcode, PacketHandler handler, params SessionPhase[] expected) =>
        (session, packet, ct) =>
        {
            if (session.TryFeature<IPlayerSession>(out var player)
                && Array.IndexOf(expected, player.Phase) < 0)
                Report($"C2S {opcode}", player.Phase, session.Id, expected);
            return handler(session, packet, ct);
        };

    /// <summary>Wraps a typed <paramref name="handler"/> so a request outside <paramref name="expected"/> is reported.</summary>
    public MessageHandler<T> Expect<T>(MessageHandler<T> handler, params SessionPhase[] expected) where T : IMessage =>
        (session, message, ct) =>
        {
            if (session.TryFeature<IPlayerSession>(out var player)
                && Array.IndexOf(expected, player.Phase) < 0)
                Report(typeof(T).Name, player.Phase, session.Id, expected);
            return handler(session, message, ct);
        };

    /// <summary>How many requests of <paramref name="opcode"/> arrived in <paramref name="phase"/> unexpectedly.</summary>
    public long MismatchCount(ushort opcode, SessionPhase phase) => _mismatches.GetValueOrDefault(($"C2S {opcode}", phase));

    private void Report(string request, SessionPhase phase, long sessionId, SessionPhase[] expected)
    {
        var key = (request, phase);
        if (!_mismatches.ContainsKey(key) && _mismatches.Count >= MaxTrackedMismatches)
            return;
        var count = _mismatches.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (count == 1 || count % 100 == 0)
        {
            logger.LogWarning(
                "{Request} arrived on session {Id} in phase {Phase}; expected {Expected} ({Count} so far). Handled anyway.",
                request, sessionId, phase, string.Join("/", expected), count);
        }
    }
}
