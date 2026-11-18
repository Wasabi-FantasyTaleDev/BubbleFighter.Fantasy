using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>The numbered training stages offered by the native main-menu selector.</summary>
internal sealed class StageMainTutorialTable
{
    private readonly Dictionary<string, (string Directory, int Difficulty, int Clock)> _stages = new(StringComparer.Ordinal);

    internal StageMainTutorialTable(MapMetaTable meta)
    {
        foreach (var map in meta.Maps)
        {
            if (!map.StartsWith("sin_mission_", StringComparison.Ordinal) || !meta.IsSet(map, "isSinglePlayTutorial"))
                continue;
            // kr-1256 sub_99F9C0, cn-774 sub_B9FED0 and tw-454 sub_A7D8F0 select the numbered
            // eight categories. MapMeta stageNum is category + ten times the zero-based difficulty.
            if (!Number("stageNum", out var stage) || !Number("Difficulty", out var difficulty)
                || difficulty is < 1 or > 3 || stage - (difficulty - 1) * 10 is < 1 or > 8
                || !Number("TimeAttackTimeLimit", out var seconds) || seconds is <= 0 or > int.MaxValue / 1000)
                continue;
            var directory = meta.Get(map, "Directory");
            if (directory.Length > 0)
                _stages.Add(map, (directory, difficulty, seconds * 1000));

            bool Number(string name, out int value) =>
                int.TryParse(meta.Get(map, name), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }

    internal bool Accepts(InitialTutorialRequested request) =>
        _stages.TryGetValue(request.MapKey, out var stage)
        && stage == (request.StageDirectory, request.Difficulty, request.TimeLimitMs);
}
