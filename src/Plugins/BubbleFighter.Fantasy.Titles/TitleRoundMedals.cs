using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Titles.Data;

namespace BubbleFighter.Fantasy.Titles;

internal sealed class TitleRoundMedals
{
    private readonly string _scope;
    private readonly Rule[] _rules;

    internal TitleRoundMedals(TitlesOptions options, TitleCatalog catalog)
    {
        _scope = options.BuildKey;
        _rules = options.RoundMedalRules.Select(rule => new Rule(rule.MedalId, rule.ModeIds.ToArray(),
            rule.Metric, rule.Minimum, rule.Maximum, rule.Count, rule.MinimumParticipants)).ToArray();
        if (_rules.Any(rule => !catalog.Medals.ContainsKey(rule.MedalId)))
            throw new ConfigException("Every round medal rule must name a medal in the active native catalogue.");
    }

    internal TitleMedalGrantCommand? Prepare(RoundTitleMedalsPreparing request)
    {
        if (request.UserNum <= 0 || request.ParticipantCount <= 0 || request.Stats is null
            || !Enum.IsDefined(request.Outcome) || !Enum.IsDefined(request.Reason)
            || request.Stats.Kills < 0 || request.Stats.Knockdowns < 0 || request.Stats.Rescues < 0
            || request.Stats.Deaths < 0 || request.Stats.Stars < 0)
            throw new InvalidDataException("Round medal preparation requires accepted terminal player counters.");
        var credits = _rules.Where(rule => rule.Modes.Contains(request.ModeId)
                && request.ParticipantCount >= rule.MinimumParticipants
                && Value(rule.Metric, request) is { } count && count >= rule.Minimum
                && (rule.Maximum is null || count <= rule.Maximum))
            .Select(rule => new TitleMedalCount(rule.MedalId, rule.Count)).ToArray();
        if (credits.Length == 0) return null;
        var command = new TitleMedalGrantCommand(request.UserNum, _scope, "round", "round:" + request.MatchId, credits);
        command.Validate();
        return command;
    }

    private static int? Value(string metric, RoundTitleMedalsPreparing request) => metric switch
    {
        "Completed" => 1,
        "Wins" => request.Outcome == RoundOutcome.Won ? 1 : 0,
        "Kills" => request.Stats.Kills,
        "Knockdowns" => request.Stats.Knockdowns,
        "Rescues" => request.Stats.Rescues,
        "Score" => request.Stats.Score,
        "Deaths" => request.Stats.Deaths,
        "Stars" => request.Stats.Stars,
        _ => throw new ConfigException("Unsupported terminal counter in a round medal rule."),
    };

    private sealed record Rule(int MedalId, int[] Modes, string Metric, int Minimum, int? Maximum,
        int Count, int MinimumParticipants);
}
