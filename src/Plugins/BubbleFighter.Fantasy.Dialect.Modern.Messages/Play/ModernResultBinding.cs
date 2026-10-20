using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>
/// The modern typed round's life and result packets: the reset request and its grant, the live team score, and the
/// result screen's open, scoreboard and close (kr-1256 sub_9E2FE0 cases 0x264, 0x266 and 0x267). None of them has a
/// kr-812 class proven to match, so they keep their numbered names.
/// </summary>
internal sealed class ModernResultBinding(string protocol, IOpcodeTable table, IReadOnlyCollection<int> scoreboardModes, ILogger logger)
    : IInboundMessageBinding<RoundResetRequested>, IInboundMessageBinding<RoundEndRequested>, IMessageSender<RoundResetGranted>, IMessageSender<RoundScoreUpdated>,
      IMessageSender<RoundScoreboard>, IMessageSender<RoundEnded>, IMessageSender<ResultStageAdvanced>, IRoundResultSequencePolicy
{
    private static readonly string[] ServerNames = ["Unknown632", "Unknown985", "Unknown612", "Unknown614", "Unknown615"];

    private IReadOnlyDictionary<string, ushort> _s2c = new Dictionary<string, ushort>();

    public string Protocol => protocol;

    /// <summary>The 612 handler (kr-1256 sub_9E88C0) opens the result scene the scoreboard then fills.</summary>
    public bool EndBeforeScoreboard => true;

    /// <summary>One S2C 614 carries the rewards between 612 and 615.</summary>
    public bool SingleScoreboard => true;

    internal void Mount(IPluginContext context)
    {
        _s2c = ServerNames.ToDictionary(name => name, name => table.Find(PacketDirection.ServerToClient, name)
            ?? throw new ConfigException($"The modern typed result requires S2C {name}."));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundResetRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundEndRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundResetGranted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScoreboard>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundEnded>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ResultStageAdvanced>>(this));
        // kr-1256 sub_6E3730 writes the actor, two tagged optional words and a bool32 after the death timer.
        ModernRoutes.Bind(context, protocol, table, logger, "Unknown602",
            body => new RoundResetRequested(MatchLife1256.ReadReset(body), body.ToArray()));
        // kr-1256 sub_6E5740 sends an empty 625 when the client's stage is over (callers sub_AC3FE0, sub_B38850).
        ModernRoutes.Bind(context, protocol, table, logger, "Unknown625", body =>
        {
            ModernRoutes.RequireEmpty(body, "Unknown625");
            return new RoundEndRequested();
        });
    }

    /// <summary>Grants a reset: kr-1256 sub_6F5E50 reads the request's fields again, then one more word.</summary>
    public ValueTask SendAsync(ISession session, RoundResetGranted message, CancellationToken ct) =>
        session.SendAsync(S2C("Unknown632"), MatchLife1256.ResetReplyForTimedScore(message.Request.Span), ct);

    /// <summary>Sends both team totals (kr-1256 sub_9E3420 reads team, total, two slots and three empty vectors).</summary>
    public async ValueTask SendAsync(ISession session, RoundScoreUpdated message, CancellationToken ct)
    {
        for (byte team = 1; team <= 2; team++)
            await session.SendAsync(S2C("Unknown985"),
                MatchLife1256.TeamScore(team, checked((ushort)message.TeamTotals[team - 1])), ct).ConfigureAwait(false);
    }

    /// <summary>Opens the result screen (kr-1256 sub_9E88C0 reads two words, which the packet relay path sends as zero).</summary>
    public ValueTask SendAsync(ISession session, RoundEnded message, CancellationToken ct) =>
        session.SendAsync(S2C("Unknown612"), GameToClient.ResultOpen(0, 0), ct);

    /// <summary>
    /// Fills the result rows in the modes that keep team totals: each seat's player, team and level, and the reward
    /// figures once they are known (stat block +364 to +384 hold the level floor, the total, the next floor, the
    /// experience and the Lucci earned).
    /// </summary>
    public ValueTask SendAsync(ISession session, RoundScoreboard message, CancellationToken ct)
    {
        if (!scoreboardModes.Contains(message.ModeId))
            return ValueTask.CompletedTask;
        var rows = message.Rows.Select(row => ResultScoreboardRow.ForSeat(row.Slot, row.Nickname, null) with
        {
            UserNumber = checked((int)row.UserNumber),
            Unknown52 = checked((byte)row.Team),
            Unknown500 = 1,
            UnknownStatBlock = Stats(row),
        });
        var totals = message.TeamTotals;
        var record = MatchLife1256.Scoreboard(checked((ushort)totals[0]), checked((ushort)totals[1]), rows);
        return session.SendAsync(S2C("Unknown614"), GameToClient.ScoreboardData(record), ct);
    }

    /// <summary>Closes the result into the reward stage (kr-1256 sub_9E9EF0 takes one flag, sent as zero).</summary>
    public ValueTask SendAsync(ISession session, ResultStageAdvanced message, CancellationToken ct) =>
        message.Stage == ResultStage.Reward
            ? session.SendAsync(S2C("Unknown615"), GameToClient.ResultClose(0), ct)
            : ValueTask.CompletedTask;

    private static int[] Stats(RoundScoreboardRow row)
    {
        var stats = new int[ResultScoreboardRow.StatSlotCount];
        stats[0] = row.Reward?.Level ?? row.Level;
        if (row.Reward is { } reward)
        {
            stats[22] = checked((int)reward.LevelFloorExp);
            stats[23] = checked((int)reward.TotalExp);
            stats[24] = checked((int)reward.NextLevelFloorExp);
            stats[25] = reward.ExpEarned;
            stats[26] = reward.LucciEarned;
        }
        return stats;
    }

    private ushort S2C(string name) => _s2c[name];
}
