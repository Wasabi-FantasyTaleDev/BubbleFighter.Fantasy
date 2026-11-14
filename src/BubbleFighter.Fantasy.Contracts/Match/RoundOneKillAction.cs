namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>The two native stages of a OneKill action, before room admission.</summary>
public enum RoundOneKillKind
{
    /// <summary>The attacker asks the victim's controller to confirm the action.</summary>
    Request,

    /// <summary>The victim's controller confirms the terminal action and its attacker.</summary>
    Confirmation,
}

/// <summary>Identifies the participants of a native OneKill frame without changing either participant's life.</summary>
/// <param name="Kind">Whether the frame requests the action or confirms its victim's death.</param>
/// <param name="Attacker">The exact scene handle of the attacking participant.</param>
/// <param name="Victim">The exact scene handle of the targeted participant.</param>
public sealed record RoundOneKillAction(RoundOneKillKind Kind, int Attacker, int Victim);
