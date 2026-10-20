using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes shared round-start contracts for ordinary and one-seat tutorial flows.</summary>
internal static class RoundStartSender
{
    /// <summary>Sends native loading gates followed by the selected countdown sequence.</summary>
    internal static async ValueTask SendCountdownAsync(PacketTables tables, string build, IOpcodeTable opcodes,
        bool preCountdownFirst, ISession session, RoundCountdownStarted message, CancellationToken ct)
    {
        await session.SendAsync(opcodes.S2C("gspGameEverybodyLoadingComplete"),
            TutorialWire.WriteEverybodyLoadingComplete(tables, build,
                message.IsTutorial, false, false), ct).ConfigureAwait(false);
        var pre = opcodes.Find(PacketDirection.ServerToClient, "gspGamePreCountDown");
        var countdown = opcodes.Find(PacketDirection.ServerToClient, "gspGameCountDown");
        var body = TutorialWire.WriteGameCountDown(tables, build, message.Milliseconds);
        // An ordinary round includes the pre-countdown.
        // cn-324 sub_46DB00 resets display timers; its pre-countdown must precede the countdown.
        if (!message.IsTutorial && preCountdownFirst && pre is { } first && countdown is not null)
            await session.SendAsync(first, body, ct).ConfigureAwait(false);
        var code = !message.IsTutorial
            ? countdown ?? pre ?? opcodes.S2C("gspGameCountDown")
            : opcodes.S2C("gspGameCountDown");
        await session.SendAsync(code, body, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the native start words, preserving tutorial clocks and ordinary zero defaults.</summary>
    internal static ValueTask SendStartedAsync(PacketTables tables, string build, IOpcodeTable opcodes,
        ISession session, RoundStarted message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspGameStartGame"), TutorialWire.WriteGameStartGame(tables, build,
            message.TimeLimitMs, 0, message.IsTutorial ? message.TimeLimitMs : 0), ct);
}
