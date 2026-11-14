using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets whether racing maps receive their native object initialization before play; disabled by default.</summary>
    public bool ExtremeRacingInitialization { get; set; }

    /// <summary>
    /// Gets or sets whether racing maps load their <c>cannon.ini</c> and the round forwards validated cannon shots; it
    /// requires <c>extreme_racing_initialization</c> and schedules no cannon fire by itself. Disabled by default.
    /// </summary>
    public bool ExtremeRacingCannon { get; set; }

    /// <summary>
    /// Gets or sets whether the room issues cannon commands from each cannon's <c>INTERVAL</c> while a race round runs; it
    /// requires <c>extreme_racing_cannon</c>. The cadence is a server proposal, not a native one: no client code that reads
    /// <c>INTERVAL</c> has been found. Disabled by default.
    /// </summary>
    public bool ExtremeRacingCannonFire { get; set; }

    /// <summary>Gets or sets the maximum bytes read from any racing map resource.</summary>
    [Range(8, 67_108_864)]
    public int ExtremeRacingMaximumFileBytes { get; set; } = 8_388_608;

    /// <summary>Gets or sets the maximum helper records accepted from a racing world block.</summary>
    [Range(1, 65_536)]
    public int ExtremeRacingMaximumHelpers { get; set; } = 4096;

    /// <summary>Gets or sets the maximum objects allocated by one racing initialization.</summary>
    [Range(1, 4096)]
    public int ExtremeRacingMaximumObjects { get; set; } = 1024;

    /// <summary>Gets or sets the explicit object identities and coin rounds for each supported stage directory.</summary>
    public ExtremeRacingStageOptions[] ExtremeRacingStages { get; set; } = [];

    internal IEnumerable<ValidationResult> ValidateExtremeRacingInitialization()
    {
        if (ExtremeRacingStages is null || ExtremeRacingStages.Length > 128
            || ExtremeRacingStages.Any(stage => stage is null || !stage.IsValid(ExtremeRacingMaximumObjects))
            || ExtremeRacingStages.Select(stage => stage.StageDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != ExtremeRacingStages.Length)
            yield return new ValidationResult("extreme_racing_stages requires at most 128 distinct safe stage directories with bounded identities and explicit coin rounds.");
        else if (ExtremeRacingInitialization && ExtremeRacingStages.Length == 0)
            yield return new ValidationResult("extreme_racing_initialization requires at least one explicit extreme_racing_stages policy.");
        if (ExtremeRacingCannon && !ExtremeRacingInitialization)
            yield return new ValidationResult("extreme_racing_cannon requires extreme_racing_initialization.");
        if (ExtremeRacingCannonFire && !ExtremeRacingCannon)
            yield return new ValidationResult("extreme_racing_cannon_fire requires extreme_racing_cannon.");
    }
}

/// <summary>Deployment policy for object identities and the coin rounds absent from a racing map's helper data.</summary>
public sealed class ExtremeRacingStageOptions
{
    /// <summary>Gets or sets the exact native stage directory containing the racing map resources.</summary>
    public string StageDirectory { get; set; } = "";

    /// <summary>Gets or sets the first identity of the interleaved cannon and disposer objects.</summary>
    public uint FirstCannonIdentity { get; set; }

    /// <summary>Gets or sets the first identity assigned to coin helpers in numeric suffix order.</summary>
    public uint FirstCoinIdentity { get; set; }

    /// <summary>Gets or sets the first identity assigned to stone disposer helpers.</summary>
    public uint FirstStoneDisposerIdentity { get; set; }

    /// <summary>Gets or sets the zero-based coin effect variant; the initial implementation accepts only variant zero.</summary>
    public int? CoinVariant { get; set; }

    /// <summary>Gets or sets each coin helper's one-based race round in numeric suffix order.</summary>
    public int[] CoinRounds { get; set; } = [];

    internal bool IsValid(int maximumObjects) => !string.IsNullOrWhiteSpace(StageDirectory)
        && StageDirectory.Length <= 128
        && StageDirectory.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        && CoinVariant == 0 && CoinRounds is { Length: > 0 and <= 4096 }
        && CoinRounds.Length <= maximumObjects && CoinRounds.All(round => round is >= 1 and <= 5)
        && ValidIdentity(FirstCannonIdentity, maximumObjects)
        && ValidIdentity(FirstCoinIdentity, maximumObjects)
        && ValidIdentity(FirstStoneDisposerIdentity, maximumObjects);

    private static bool ValidIdentity(uint first, int maximumObjects) => first > 0 && maximumObjects > 0
        && (ulong)first + (uint)maximumObjects <= (uint)RoomRegistry.SceneEntity(0);
}
