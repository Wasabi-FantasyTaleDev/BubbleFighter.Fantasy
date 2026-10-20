using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Seats one player in an unlisted tutorial round before its dialect sends the native scene burst.</summary>
/// <param name="ModeId">Native tutorial actor mode.</param>
/// <param name="MapKey">Resolved tutorial map.</param>
/// <param name="CharacterItemId">Equipped character.</param>
/// <param name="EquippedItemIds">Current appearance and weapons.</param>
/// <param name="Team">Native tutorial team.</param>
/// <param name="CountdownMs">Native tutorial countdown duration.</param>
/// <param name="TimeLimitMs">Map's tutorial clock.</param>
/// <param name="NativeSupplies">Whether the tutorial owns its supplies without server scheduling.</param>
public sealed record TutorialRoundOpened(int ModeId, string MapKey, int CharacterItemId,
    IReadOnlyList<int> EquippedItemIds, byte Team, int CountdownMs, int TimeLimitMs, bool NativeSupplies) : IMessage;

/// <summary>Releases a tutorial's hidden room before its native menu return or another room admission.</summary>
public sealed record TutorialRoundClosed : IMessage;

/// <summary>
/// A tutorial-room stage was cleared: its client reported success in <c>gcpReqEndStage</c> (kr-812 <c>sub_701050</c>).
/// Sent before the round is closed, so the clear time can be taken from the running round.
/// </summary>
/// <param name="MapKey">The stage's MapMeta row, the map key the stage was started with.</param>
public sealed record TutorialStageCleared(string MapKey) : IMessage;
