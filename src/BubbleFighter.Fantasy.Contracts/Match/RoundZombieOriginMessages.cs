using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>The controlled character requested a prototype draw. Its reported form cannot select a recipient.</summary>
public sealed record RoundZombieOriginPrototypeRequested(int CharacterKind) : IMessage;

/// <summary>A private draw, scaled by the recipient's native prototype weight table.</summary>
public sealed record RoundZombieOriginPrototypeRoll(int Roll) : IMessage;

/// <summary>The objective that ended a Zombie Battle G round, shared by all seats.</summary>
public enum ZombieOriginOutcome
{
    /// <summary>No Zombie Battle G objective has been decided for this round.</summary>
    Undecided,

    /// <summary>The zombies converted every remaining fighter.</summary>
    ZombiesConvertedAllFighters,

    /// <summary>A fighter survived until the time limit.</summary>
    FightersSurvivedTimeout,

    /// <summary>The fighters popped all zombies.</summary>
    FightersPerfectWin,
}
