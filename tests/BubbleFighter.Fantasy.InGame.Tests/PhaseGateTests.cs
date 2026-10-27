using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class PhaseGateTests
{
    [Fact]
    public async Task UnexpectedPhaseIsCountedButTheHandlerStillRuns()
    {
        var gate = new PhaseGate(NullLogger.Instance);
        var calls = 0;
        var handler = gate.Expect(404, (_, _, _) => { calls++; return ValueTask.CompletedTask; }, SessionPhase.Room);
        var session = new RecordingSession(1);
        var player = session.SetLobbyPlayer(5, "p");
        var packet = new InboundPacket(404, ReadOnlyMemory<byte>.Empty);

        await handler(session, packet, CancellationToken.None);
        player.MoveTo(SessionPhase.Plaza);
        player.MoveTo(SessionPhase.Room);
        await handler(session, packet, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(1, gate.MismatchCount(404, SessionPhase.MainMenu));
        Assert.Equal(0, gate.MismatchCount(404, SessionPhase.Room));
    }

    [Fact]
    public async Task SessionsWithoutAPlayerAreNotChecked()
    {
        var gate = new PhaseGate(NullLogger.Instance);
        var handler = gate.Expect(579, (_, _, _) => ValueTask.CompletedTask, SessionPhase.Match);
        await handler(new RecordingSession(2), new InboundPacket(579, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        foreach (var phase in Enum.GetValues<SessionPhase>())
            Assert.Equal(0, gate.MismatchCount(579, phase));
    }
}
