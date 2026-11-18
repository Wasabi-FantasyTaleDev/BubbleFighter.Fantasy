using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Releases a player's previous social scene before the tutorial selection installs its own session.</summary>
public sealed record TutorialSelectionOpening : IMessage;

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
    private IReadOnlyList<RoomItemAttribute> _itemAttributes = [];
    private IReadOnlyList<int> _equippedItemIds = new RoundEquipmentSnapshot(EquippedItemIds, []).WornItemIds;

    /// <summary>Gets copied worn identities frozen for the tutorial scene admission.</summary>
    public IReadOnlyList<int> EquippedItemIds
    {
        get => _equippedItemIds;
        init => _equippedItemIds = new RoundEquipmentSnapshot(value, []).WornItemIds;
    }

    /// <summary>Gets bounded, copied modifiers from that admission; an empty vector clears previous scene modifiers.</summary>
    public IReadOnlyList<RoomItemAttribute> ItemAttributes
    {
        get => _itemAttributes;
        init => _itemAttributes = new RoundEquipmentSnapshot(EquippedItemIds, value).ItemAttributes;
    }

    /// <summary>Gets whether this solo scene uses its authenticated game connection without a match relay.</summary>
    public bool GameConnectionOnly { get; init; }
}

/// <summary>Requests a numbered solo stage or the configured first-run tutorial from a fully initialized main menu.</summary>
/// <param name="ModeId">The native mode selected by the dialect's audited tutorial policy.</param>
/// <param name="MapKey">The selected numbered map or the configured first-run destination.</param>
/// <param name="TimeLimitMs">The map's tutorial clock in milliseconds.</param>
/// <param name="NativeRequest">The complete dialect-validated request, retained without interpreting its wire layout.</param>
public sealed record InitialTutorialRequested(int ModeId, string MapKey, int TimeLimitMs,
    ReadOnlyMemory<byte> NativeRequest) : IMessage
{
    /// <summary>Gets the assigned native player team selected by the dialect's tutorial route.</summary>
    public byte Team { get; init; }

    /// <summary>Gets the selected stage's claimed physical directory for validation against the map table.</summary>
    public string StageDirectory { get; init; } = "";

    /// <summary>Gets the selected numbered stage's claimed difficulty.</summary>
    public int Difficulty { get; init; }
}

/// <summary>Installs the hidden solo room and starts loading its native tutorial scene.</summary>
/// <param name="Room">The admitted room and its resolved player appearance.</param>
/// <param name="NativeRequest">The dialect's original validated tutorial selection.</param>
public sealed record InitialTutorialEntered(RoomView Room, ReadOnlyMemory<byte> NativeRequest) : IMessage
{
    /// <summary>Gets the installed stage directory when the selected map has a distinct physical scene.</summary>
    public string StageDirectory { get; init; } = Room.MapKey;

    /// <summary>Gets the stage difficulty used by the native scene.</summary>
    public int Difficulty { get; init; } = 1;
}

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

/// <summary>The main-menu tutorial picker requests the current player's numbered clear records.</summary>
public sealed record TutorialStageListRequested : IMessage;

/// <summary>The per-player stage records reloaded when the main-menu tutorial picker opens.</summary>
/// <param name="Clears">The player's stored map-stage numbers and best records.</param>
public sealed record TutorialStageListReceived(IReadOnlyList<BubbleFighter.Fantasy.Contracts.Progress.TutorialStageClear> Clears) : IMessage;
