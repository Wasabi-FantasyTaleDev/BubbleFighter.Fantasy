using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Translates shared result events while retaining each native result grammar and publication order.</summary>
internal sealed class RoundResultSender : IMessageSender<RoundScoreUpdated>, IMessageSender<RoundScoreboard>,
    IMessageSender<RoundEnded>, IMessageSender<ResultStageAdvanced>, IRoundResultSequencePolicy
{
    private readonly PacketTables _packets;
    private readonly string _build;
    private readonly Func<ISession, IOpcodeTable> _opcodes;
    private readonly PlayValues.ScoreValues? _fixedRows;
    private readonly RoundResultWire.MaskedRows? _maskedRows;
    private readonly IReadOnlyCollection<int> _scoreboardModes = [];
    private readonly IGameText? _texts;

    /// <summary>Uses the build's fixed-row result settings and recipient-local opcode table.</summary>
    internal RoundResultSender(string protocol, PacketTables packets, string build, Func<ISession, IOpcodeTable> opcodes,
        PlayValues.ScoreValues fixedRows)
    {
        Protocol = protocol;
        _packets = packets;
        _build = build;
        _opcodes = opcodes;
        _fixedRows = fixedRows;
        RoundResultWire.RequireFixedScalarLayouts(packets, build);
        _ = RoundResultWire.CollectedLucciField(packets, build);
    }

    /// <summary>Uses the audited mask-row grammar and the deployment's existing scoreboard mode selection.</summary>
    internal RoundResultSender(string protocol, PacketTables packets, string build, Func<ISession, IOpcodeTable> opcodes,
        IReadOnlyCollection<int> scoreboardModes, RoundResultWire.MaskedRows maskedRows, IGameText? texts)
    {
        Protocol = protocol;
        _packets = packets;
        _build = build;
        _opcodes = opcodes;
        _scoreboardModes = scoreboardModes;
        _maskedRows = maskedRows;
        _texts = texts;
        PacketLayoutContract.RequireFields(packets, build, "RoundLiveScore", "Round result sender",
            ("team", "u8"), ("total", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultOpen", "Round result sender",
            ("unknown_n0", "u32"), ("unknown_n1", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "RoundResultClose", "Round result sender", ("unknown_n0", "u8"));
    }

    /// <summary>Gets the native protocol scope whose adapters delegate to this sender.</summary>
    public string Protocol { get; }

    /// <summary>Gets whether the native result-open event must precede its scoreboard.</summary>
    public bool EndBeforeScoreboard => _fixedRows is null
        || _fixedRows.TimeOverEnd == PlayValues.TimeOverEndBody.Scoreboard;

    /// <summary>Gets whether the native scoreboard carries rewards in one publication.</summary>
    public bool SingleScoreboard => _maskedRows is not null;

    /// <summary>Sends native live-score vectors, full-board restatements or separate team totals.</summary>
    public async ValueTask SendAsync(ISession session, RoundScoreUpdated message, CancellationToken ct)
    {
        if (message.CollectedLucciOnly && RoundResultWire.CollectedLucciField(_packets, _build) is null)
            return;
        if (_fixedRows is { } fixedRows)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (fixedRows.LiveScorePacket)
                await session.SendAsync(Resolve(session, "gspUpdateScoreData"),
                    RoundResultWire.WriteScoreUpdate(_packets, _build, message), ct).ConfigureAwait(false);
            else if (message.Scoreboard is { } board)
                await session.SendAsync(Resolve(session, "gspGameBroadcastPlayerScoreData"),
                    RoundResultWire.WriteScoreboard(_packets, _build, board), ct).ConfigureAwait(false);
            return;
        }
        // kr-1256 sub_9E3420 applies one total per team; fixed-row clients also consume seat vectors.
        for (byte team = 1; team <= 2; team++)
            await session.SendAsync(Resolve(session, "gspUpdateScoreData"),
                PacketCodec.Write(_packets, _build, "RoundLiveScore", new Dictionary<string, object?>
                {
                    ["team"] = team,
                    ["total"] = checked((ushort)message.TeamTotals[team - 1]),
                }), ct).ConfigureAwait(false);
    }

    /// <summary>Sends the existing recipient outcome or the native result-open event.</summary>
    public async ValueTask SendAsync(ISession session, RoundEnded message, CancellationToken ct)
    {
        if (_fixedRows is { } fixedRows)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (RoundResultWire.HordeOutcomeNotification(fixedRows, message) is { } notification)
                await session.SendAsync(Resolve(session, notification), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            await session.SendAsync(Resolve(session, "gspGameEndGame"),
                RoundResultWire.WriteGameEndForRecipient(_packets, _build, fixedRows, message), ct).ConfigureAwait(false);
            return;
        }
        // kr-1256 sub_9E88C0 installs the two result-open words before the masked scoreboard.
        await session.SendAsync(Resolve(session, "gspRoundResultOpen_", "Unknown612"),
            PacketCodec.Write(_packets, _build, "RoundResultOpen", new Dictionary<string, object?>
            {
                ["unknown_n0"] = 0,
                ["unknown_n1"] = 0,
            }), ct).ConfigureAwait(false);
    }

    /// <summary>Sends a native fixed-row or masked scoreboard without computing its outcome or rewards.</summary>
    public ValueTask SendAsync(ISession session, RoundScoreboard message, CancellationToken ct)
    {
        if (_fixedRows is { } fixedRows)
        {
            ArgumentNullException.ThrowIfNull(message);
            var body = RoundResultWire.WriteScoreboard(_packets, _build, message);
            if (fixedRows.TimeOverEnd == PlayValues.TimeOverEndBody.Scoreboard && !message.WithRewards)
                return session.SendAsync(Resolve(session, "gspGameTimeOverEnd"), body, ct);
            if (!message.TimeOver)
                return session.SendAsync(Resolve(session, "gspGameBroadcastPlayerScoreData"), body, ct);
            return session.SendAsync(Resolve(session, "gspGameTimeOverEnd"),
                RoundResultWire.WriteTimeOverTotals(_packets, _build, message.RoundWins ?? message.TeamTotals), ct);
        }
        if (!_scoreboardModes.Contains(message.ModeId))
            return ValueTask.CompletedTask;
        return session.SendAsync(Resolve(session, "gspRoundScoreboard_", "Unknown614"),
            RoundResultWire.WriteMaskedScoreboard(_packets, _build, _maskedRows
                ?? throw new InvalidOperationException("Masked result grammar is missing."), session, _texts, message), ct);
    }

    /// <summary>Sends only the reward-stage notification selected by the native result grammar.</summary>
    public ValueTask SendAsync(ISession session, ResultStageAdvanced message, CancellationToken ct)
    {
        if (_fixedRows is not null)
            ArgumentNullException.ThrowIfNull(message);
        if (message.Stage != ResultStage.Reward)
            return ValueTask.CompletedTask;
        return _fixedRows is not null
            ? session.SendAsync(Resolve(session, "gspGameShowResult_"), ReadOnlyMemory<byte>.Empty, ct)
            : session.SendAsync(Resolve(session, "gspRoundRewardStage_", "Unknown615"),
                PacketCodec.Write(_packets, _build, "RoundResultClose",
                    new Dictionary<string, object?> { ["unknown_n0"] = 0 }), ct);
    }

    private ushort Resolve(ISession session, params string[] names)
    {
        var table = _opcodes(session);
        foreach (var name in names)
            if (table.Find(PacketDirection.ServerToClient, name) is { } opcode)
                return opcode;
        throw new ConfigException($"Typed results require ServerToClient {string.Join(" or ", names)}.");
    }
}
