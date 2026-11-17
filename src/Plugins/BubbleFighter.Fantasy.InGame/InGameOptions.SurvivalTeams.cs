using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets audited individual Area32 identities and bounded FinalZone groups in manual rooms.</summary>
    public bool SurvivalTeamAssignments { get; set; }

    /// <summary>Gets or sets how many native FinalZone groups manual rooms may use; each has at most two members.</summary>
    [Range(1, Sessions.SurvivalTeams.MaximumGroups)]
    public int SurvivalMaximumGroups { get; set; } = Sessions.SurvivalTeams.MaximumGroups;

    /// <summary>Gets or sets whether a current two-member party shares a FinalZone group when entering or changing mode.</summary>
    public bool SurvivalPairParties { get; set; } = true;
}
