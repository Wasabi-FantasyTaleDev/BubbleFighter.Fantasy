using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
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
    private readonly StarScoreFields? _starScores;
    private readonly bool _zombieOrigin;

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
        _ = LiveScoreKinds.Require(packets, build);
        _ = RoundResultWire.CollectedLucciField(packets, build);
    }

    /// <summary>Uses the audited mask-row grammar and the deployment's existing scoreboard mode selection.</summary>
    internal RoundResultSender(string protocol, PacketTables packets, string build, Func<ISession, IOpcodeTable> opcodes,
        IReadOnlyCollection<int> scoreboardModes, RoundResultWire.MaskedRows maskedRows, IGameText? texts, bool zombieOrigin = false)
    {
        Protocol = protocol;
        _packets = packets;
        _build = build;
        _opcodes = opcodes;
        _scoreboardModes = scoreboardModes;
        _maskedRows = maskedRows;
        _texts = texts;
        _zombieOrigin = zombieOrigin;
        _starScores = StarScoreFields.Load(packets, build);
        _ = LiveScoreKinds.Require(packets, build);
        PacketLayoutContract.RequireFields(packets, build, "RoundLiveScore", "Round result sender",
            ("team", "u8"), ("total", "u16"), ("scorer_slot", "u8"), ("victim_slot", "u8"),
            ("scorer_stats", "list<RoundLiveScoreStat>"), ("victim_stats", "list<RoundLiveScoreStat>"),
            ("unknown_32", "list<RoundLiveScoreStat>"));
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

    /// <summary>Reports whether this sender's configured native result surface includes the requested mode.</summary>
    public bool SupportsScoreboard(int modeId) => _fixedRows is not null || _scoreboardModes.Contains(modeId);

    /// <summary>Sends native live-score vectors, full-board restatements or separate team totals.</summary>
    public async ValueTask SendAsync(ISession session, RoundScoreUpdated message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.CollectedLucciOnly && RoundResultWire.CollectedLucciField(_packets, _build) is null)
            return;
        if (_fixedRows is { } fixedRows)
        {
            if (fixedRows.LiveScorePacket)
                await session.SendAsync(Resolve(session, "gspUpdateScoreData"),
                    RoundResultWire.WriteScoreUpdate(_packets, _build, message), ct).ConfigureAwait(false);
            else if (message.Scoreboard is { } board)
                await session.SendAsync(Resolve(session, "gspGameBroadcastPlayerScoreData"),
                    RoundResultWire.WriteScoreboard(_packets, _build, board), ct).ConfigureAwait(false);
            return;
        }
        RoundResultWire.RequireModernScoreUpdate(message);
        // kr-1256 sub_9E8B20 applies held-star stats and a separate team-star vector before the next full board.
        var stars = _starScores is { } fields && message.Scoreboard is { } starBoard && fields.Modes.Contains(starBoard.ModeId)
            ? fields : null;
        // kr-1256 sub_9E8B20, cn-774 sub_8E33A0 and tw-454 sub_A63910 apply seats independently of the scalar team's index.
        // Held stars retain their admitted mode-specific path; ordinary updates carry combat and WeaponMaster counters.
        var first = stars is not null
            ? RoundResultWire.WriteStarScoreUpdate(_packets, _build, stars, message)
            : RoundResultWire.WriteScoreUpdate(_packets, _build, message with { ScoringTeam = 1 }, includeStars: false);
        var second = PacketCodec.Write(_packets, _build, "RoundLiveScore", new Dictionary<string, object?>
        {
            ["team"] = (byte)2,
            ["total"] = checked((ushort)message.TeamTotals[1]),
        });
        // Validate both bodies before the first send, so an invalid second total cannot publish a partial update.
        var opcode = Resolve(session, "gspUpdateScoreData");
        await session.SendAsync(opcode, first, ct).ConfigureAwait(false);
        await session.SendAsync(opcode, second, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the existing recipient outcome or the native result-open event.</summary>
    public async ValueTask SendAsync(ISession session, RoundEnded message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_fixedRows is { } fixedRows)
        {
            if (RoundResultWire.HordeOutcomeNotification(fixedRows, message) is { } notification)
                await session.SendAsync(Resolve(session, notification), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            await session.SendAsync(Resolve(session, "gspGameEndGame"),
                RoundResultWire.WriteGameEndForRecipient(_packets, _build, fixedRows, message), ct).ConfigureAwait(false);
            return;
        }
        // kr-1256 sub_787000 selects the result by these two words and the local role; both sides receive the same pair.
        var (word0, word1) = _zombieOrigin && message.ModeId == (int)GameModeId.ZombieOrigin ? message.ZombieOriginOutcome switch
        {
            ZombieOriginOutcome.ZombiesConvertedAllFighters => (1u, 0u),
            ZombieOriginOutcome.FightersPerfectWin => (1u, 1u),
            ZombieOriginOutcome.FightersSurvivedTimeout or ZombieOriginOutcome.Undecided => (0u, 0u),
            _ => throw new InvalidOperationException($"Unknown Zombie Battle G outcome {message.ZombieOriginOutcome}."),
        } : (0u, 0u);
        await session.SendAsync(Resolve(session, "gspRoundResultOpen_", "Unknown612"),
            PacketCodec.Write(_packets, _build, "RoundResultOpen", new Dictionary<string, object?>
            {
                ["unknown_n0"] = word0,
                ["unknown_n1"] = word1,
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
