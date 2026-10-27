using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;

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
    IReadOnlyList<int> EquippedItemIds, byte Team, int CountdownMs, int TimeLimitMs, bool NativeSupplies) : IMessage
{
    /// <summary>Gets whether this solo scene uses its authenticated game connection without a match relay.</summary>
    public bool GameConnectionOnly { get; init; }
}

/// <summary>Requests the initial solo tutorial from a fully initialized main menu.</summary>
/// <param name="ModeId">The native mode selected by the dialect's audited tutorial policy.</param>
/// <param name="MapKey">The startup tutorial map selected by the client.</param>
/// <param name="TimeLimitMs">The map's tutorial clock in milliseconds.</param>
/// <param name="NativeRequest">The complete dialect-validated request, retained without interpreting its wire layout.</param>
public sealed record InitialTutorialRequested(int ModeId, string MapKey, int TimeLimitMs,
    ReadOnlyMemory<byte> NativeRequest) : IMessage
{
    /// <summary>Gets the assigned native player team selected by the dialect's tutorial route.</summary>
    public byte Team { get; init; }
}

/// <summary>Installs the hidden solo room and starts loading its native tutorial scene.</summary>
/// <param name="Room">The admitted room and its resolved player appearance.</param>
/// <param name="NativeRequest">The dialect's original validated tutorial selection.</param>
public sealed record InitialTutorialEntered(RoomView Room, ReadOnlyMemory<byte> NativeRequest) : IMessage;

/// <summary>Leaves an active initial tutorial after the client's native exit confirmation.</summary>
public sealed record InitialTutorialExitRequested : IMessage;

/// <summary>Releases a tutorial's hidden room before its native menu return or another room admission.</summary>
public sealed record TutorialRoundClosed : IMessage;

/// <summary>
/// A tutorial-room stage was cleared: its client reported success in <c>gcpReqEndStage</c> (kr-812 <c>sub_701050</c>).
/// Sent before the round is closed, so the clear time can be taken from the running round.
/// </summary>
/// <param name="MapKey">The stage's MapMeta row, the map key the stage was started with.</param>
public sealed record TutorialStageCleared(string MapKey) : IMessage;
