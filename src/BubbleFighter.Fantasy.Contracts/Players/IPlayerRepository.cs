using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Contracts.Players;

/// <summary>Looks up and creates player records for login.</summary>
public interface IPlayerRepository
{
    /// <summary>Gets whether accepted identity and onboarding writes survive process restart.</summary>
    bool PersistsState => false;

    /// <summary>Gets whether character creation commits its fighter and starter bags in the identity transaction.</summary>
    bool CreatesStarterInventoryAtomically => false;

    /// <summary>Gets whether the store commits paint consumption and player color in one operation.</summary>
    bool SupportsAtomicPaintApplication => false;

    /// <summary>Atomically changes a player's nickname and consumes one owned rename card; returns the native result code.</summary>
    ValueTask<int> RenameWithItemAsync(long userNum, string nickname, int itemId, CancellationToken ct) =>
        throw new NotSupportedException("This player repository does not support atomic consumable name changes.");

    /// <summary>Atomically consumes one owned, unexpired spray and saves its catalog-derived color index.
    /// Returns false without changes when the item is unavailable or the color is already applied.</summary>
    ValueTask<bool> ApplyPaintWithItemAsync(long userNum, int itemId, ushort colorIndex, CancellationToken ct) =>
        throw new NotSupportedException("This player repository does not support atomic paint application.");

    /// <summary>Finds a player by login name, or returns <see langword="null"/>.</summary>
    /// <param name="username">The login name.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlayerRecord?> FindByUsernameAsync(string username, CancellationToken ct);

    /// <summary>Checks whether the player completed native character creation rather than automatic account provisioning.</summary>
    /// <param name="userNum">The existing player identity.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> HasCreatedCharacterAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Creates a player with the given login name and nickname. Rejection of a taken name is
    /// store-defined: persistent stores rely on the unique username and nickname indexes and let
    /// the provider's duplicate error surface. Callers that need a definite answer use
    /// <see cref="FindByUsernameAsync"/> or <see cref="NicknameExistsAsync"/> first, or
    /// <see cref="CreateCharacterAsync"/>, which returns null for a taken account or name.
    /// </summary>
    /// <param name="username">The login name.</param>
    /// <param name="nickname">The display name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created record with its new user number and starting level.</returns>
    ValueTask<PlayerRecord> CreateAsync(string username, string nickname, CancellationToken ct);

    /// <summary>Checks whether an existing player owns the requested nickname.</summary>
    /// <param name="nickname">The display name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="exceptUsername">An existing placeholder account allowed to retain its own nickname.</param>
    ValueTask<bool> NicknameExistsAsync(string nickname, CancellationToken ct, string? exceptUsername = null);

    /// <summary>Atomically creates a player, chosen fighter and initial inventory, returning null for a taken account or name.</summary>
    /// <param name="username">The login account.</param>
    /// <param name="nickname">The chosen display name.</param>
    /// <param name="kit">The initial grants with the chosen character worn.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlayerRecord?> CreateCharacterAsync(string username, string nickname, StarterKit kit, CancellationToken ct);

    /// <summary>Checks whether a created player still needs to finish the initial tutorial.</summary>
    /// <param name="userNum">The created player identity.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> NeedsTutorialAsync(long userNum, CancellationToken ct);

    /// <summary>Persists successful completion reported by the active tutorial.</summary>
    /// <param name="userNum">The authenticated player identity.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask CompleteTutorialAsync(long userNum, CancellationToken ct);
}
