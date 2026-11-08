using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Identifies a native selected-stage goal event independently of multiplayer score credit.</summary>
public enum TutorialScoreEvent
{
    /// <summary>A tutorial target entered its bubble state.</summary>
    Bubble,
    /// <summary>A tutorial target received its finishing blow.</summary>
    Pang,
    /// <summary>The player completed a tutorial rescue.</summary>
    Help,
}

/// <summary>Carries one audited native score event for a hidden selected-stage tutorial.</summary>
/// <param name="Slot">The player seat resolved from the scorer's character reference.</param>
/// <param name="Event">The goal counter to advance.</param>
public sealed record TutorialScoreReported(int Slot, TutorialScoreEvent Event) : IMessage;

/// <summary>Publishes the owning tutorial's absolute goal counters without a reward or result transition.</summary>
/// <param name="Slot">The authenticated solo player's seat.</param>
/// <param name="Nickname">The name retained in the native score row.</param>
/// <param name="Bubbles">The accepted bubble count.</param>
/// <param name="Pangs">The accepted finishing-blow count.</param>
/// <param name="Helps">The accepted rescue count.</param>
public sealed record TutorialScoreUpdated(int Slot, string Nickname, int Bubbles, int Pangs, int Helps) : IMessage;
