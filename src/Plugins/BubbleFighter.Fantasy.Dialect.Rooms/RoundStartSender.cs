using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes shared round-start contracts for ordinary and one-seat tutorial flows.</summary>
internal static class RoundStartSender
{
    // kr-1256 sub_9E1EB0 case 0x261, cn-774 sub_8D7D20 and tw-454 sub_A578B0 case 0x27E
    // install this native unlimited duration and timer ceiling.
    private const int NoTimeLimitMs = 0x3FFFFFFF;

    /// <summary>
    /// Sends native loading gates followed by the selected countdown sequence. The first word selects the match
    /// relay for the round's peer packets: the client stores it (cn-324 sub_6F0B30 at +7455, kr-812 sub_802E60 at
    /// +16811) and sends its gpp frames on the relay while the relay object exists (cn-324 sub_6F7F10, kr-812
    /// sub_80D4C0). An ordinary round sets it only when the row offers the relay and this seat did not fall back to
    /// its game connection; the tutorial keeps its own word.
    /// </summary>
    internal static ValueTask SendCountdownAsync(PacketTables tables, string build, IOpcodeTable opcodes,
        bool preCountdownFirst, bool selectMatchRelay, ISession session, RoundCountdownStarted message, CancellationToken ct) =>
        SendCountdownCoreAsync(tables, build, opcodes, preCountdownFirst,
            message.IsTutorial || (selectMatchRelay && !message.MatchOverGame), false, false, session, message, ct);

    /// <summary>Preserves configured relay, peer and optimizer flags while clearing transport selections for game fallback.</summary>
    internal static ValueTask SendCountdownAsync(PacketTables tables, string build, IOpcodeTable opcodes,
        IReadOnlyList<int> loadingFlags, ISession session, RoundCountdownStarted message, CancellationToken ct) =>
        SendCountdownCoreAsync(tables, build, opcodes, false,
            !message.MatchOverGame && loadingFlags[0] != 0, !message.MatchOverGame && loadingFlags[1] != 0,
            loadingFlags[2] != 0, session, message, ct);

    private static async ValueTask SendCountdownCoreAsync(PacketTables tables, string build, IOpcodeTable opcodes,
        bool preCountdownFirst, bool matchRelay, bool peer, bool optimizer,
        ISession session, RoundCountdownStarted message, CancellationToken ct)
    {
        await session.SendAsync(opcodes.S2C("gspGameEverybodyLoadingComplete"),
            TutorialWire.WriteEverybodyLoadingComplete(tables, build, matchRelay, peer, optimizer), ct).ConfigureAwait(false);
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
        SendStartedCoreAsync(tables, build, opcodes, session, message.TimeLimitMs,
            message.IsTutorial ? message.TimeLimitMs : 0, false, ct);

    /// <summary>Sends the native no-limit duration and trailing notification when the round requests an unbounded clock.</summary>
    internal static ValueTask SendStartedWithClockNotificationAsync(PacketTables tables, string build, IOpcodeTable opcodes,
        ISession session, RoundStarted message, CancellationToken ct) =>
        SendStartedCoreAsync(tables, build, opcodes, session,
            message.Unlimited ? NoTimeLimitMs : message.TimeLimitMs, 0, message.Unlimited, ct);

    private static async ValueTask SendStartedCoreAsync(PacketTables tables, string build, IOpcodeTable opcodes,
        ISession session, int timeLimitMs, int thirdWord, bool notifyUnlimitedClock, CancellationToken ct)
    {
        await session.SendAsync(opcodes.S2C("gspGameStartGame"),
            TutorialWire.WriteGameStartGame(tables, build, timeLimitMs, 0, thirdWord), ct).ConfigureAwait(false);
        if (notifyUnlimitedClock)
            await session.SendAsync(opcodes.S2C("gspGameClockUnbounded_"), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }
}
