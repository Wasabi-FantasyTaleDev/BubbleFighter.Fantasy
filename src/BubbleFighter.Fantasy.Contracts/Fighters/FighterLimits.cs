namespace BubbleFighter.Fantasy.Contracts.Fighters;

/// <summary>Bounds shared by fighter repositories and their callers.</summary>
public static class FighterLimits
{
    /// <summary>Maximum stored fighter slots per player: the local fighter and its partner.</summary>
    public const int MaxFighterSlots = 2;
}
