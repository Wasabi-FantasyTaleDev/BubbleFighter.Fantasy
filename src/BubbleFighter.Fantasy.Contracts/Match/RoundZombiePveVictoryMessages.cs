using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Installs a single recipient's four display IDs and terminal rank rows before opening the mode-16 dialog.</summary>
/// <param name="Rows">The bounded cooperative rank snapshot.</param>
/// <param name="SelectedDisplayId">The display revealed at the locally chosen box.</param>
/// <param name="FirstOther">The first unchosen box display.</param>
/// <param name="SecondOther">The second unchosen box display.</param>
/// <param name="ThirdOther">The third unchosen box display.</param>
public sealed record RoundZombiePveVictoryRanks(IReadOnlyList<RoundPvePointRow> Rows, int SelectedDisplayId,
    int FirstOther, int SecondOther, int ThirdOther) : IMessage;

/// <summary>Opens the recipient's four-box Zombie PvE victory dialog after its snapshot is installed.</summary>
public sealed record RoundZombiePveVictoryOpened : IMessage;

/// <summary>Delivers the three native event words to the recipient's Zombie PvE result callback.</summary>
/// <param name="FirstWord">The first opaque event word.</param>
/// <param name="SecondWord">The second opaque event word.</param>
/// <param name="ThirdWord">The third opaque event word.</param>
public sealed record RoundZombiePveVictoryFollowUp(uint FirstWord, uint SecondWord, uint ThirdWord) : IMessage;
