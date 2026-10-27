using BubbleFighter.Fantasy.Contracts.Inventory;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>
/// Configuration for the <see cref="InventoryViewPlugin"/> row. Protocol name and
/// <see cref="VersionMajor"/> are supplied by the profile; this plugin does not
/// hard-code a client region in its id.
/// </summary>
/// <remarks>
/// The row is disabled by default. An enabled request-only row registers the
/// fire-and-forget logger; <see cref="PublishEquippedViewPush"/> additionally
/// exposes selection and view services and requires typed local and remote senders.
/// </remarks>
public sealed class InventoryViewOptions : IValidatableOptions
{
    /// <summary>Gets or sets the game-connection protocol name this row handles.</summary>
    [Required]
    public string Protocol { get; set; } = "kr-1256/game";

    /// <summary>Gets or sets the client profile id that selected this dialect.</summary>
    [Required]
    public string ClientProfile { get; set; } = "kr-1256";

    /// <summary>
    /// Gets or sets the client major version this row serves. The row is
    /// build-neutral: the mounted view senders decide which wire surface exists.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int VersionMajor { get; set; } = 1256;

    /// <summary>
    /// Gets or sets the C2S opcode-table name of a request this row only logs, or empty (the default) to register
    /// none. kr-1256 C2S 806 (builder sub_80DA80) is the item use <c>gcpReqInventoryUseItem</c>, which the modern
    /// item-use binding answers, so no build routes it here.
    /// </summary>
    public string ViewRequest { get; set; } = "";

    /// <summary>
    /// Gets or sets the directory holding the packet tables that state the view request's body
    /// (<c>InventoryUseRequest</c> of <c>packets/inventory-use.yaml</c>). Read only when
    /// <see cref="ViewRequest"/> names a request; rows that log no request never touch it.
    /// </summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";

    /// <summary>
    /// Gets or sets whether this row registers its view request handler. Defaults to
    /// <see langword="false"/>: the plugin ships dormant and the Host must not
    /// change behavior unless a profile row explicitly opts in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets whether this row publishes <see cref="IEquippedViewPush"/> so
    /// the in-game room path can send the S2C 806 equipped view. Defaults to
    /// <see langword="false"/>, keeping the row dormant.
    /// </summary>
    /// <remarks>
    /// S2C 806 is sent as part of the room-entry
    /// sequence, immediately after the room install, carrying the player's
    /// equipped character and weapons. The client handler upserts that record
    /// into the profile map the in-room spawner and the appearance rebuilder
    /// read, so without it a room's character has no equipped set to build from.
    /// </remarks>
    public bool PublishEquippedViewPush { get; set; }

    /// <summary>
    /// Gets or sets whether the published preset bar comes from per-player persisted pages instead of the
    /// worn-derived records. Defaults to <see langword="false"/>: the shipped bar stays derived and this row
    /// never touches the page store, so the S2C 837/833 bytes are unchanged until a profile opts in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <see langword="true"/> the first publication of a player materializes five server-owned pages
    /// (seeded from the worn primary and special, the derived page's own convention) through
    /// <see cref="IPlayerInventoryState"/>; afterwards the persisted pages win over later worn changes,
    /// because the swap chain has no client-to-server save packet - the client can only ask to activate a
    /// page (<c>kr-1256 sub_808F10</c>, <c>kr-812 sub_97A8D0</c>). The bar then carries all five pages:
    /// <c>kr-1256 sub_5FB4F0</c> offers indices 0-2 unconditionally and enables 3-4 exactly when their
    /// records exist, and <c>kr-812 sub_987BA0</c> materializes exactly five id/flag pairs.
    /// </para>
    /// <para>
    /// Requires <see cref="PublishEquippedViewPush"/> and a persistence row publishing
    /// <see cref="IPlayerInventoryState"/>; activation fails loud when either is missing. The name follows
    /// this row's verb-first switches (<c>publish_equipped_view_push</c>) and the equipment row's
    /// <c>persist_worn_state</c>.
    /// </para>
    /// </remarks>
    public bool PersistPresetPages { get; set; }

    /// <summary>Gets or sets a file path to the client's ShopItem workbook supplying clothing parts and accessory slots; empty disables those positions.</summary>
    public string ItemLocationsPath { get; set; } = "";

    /// <summary>
    /// Gets or sets the client path of that workbook (for example <c>Config/ShopItem2.xml</c>), read from the
    /// build's client data; at most one of this and <see cref="ItemLocationsPath"/> is set.
    /// </summary>
    public string ItemLocationsClientPath { get; set; } = "";

    /// <summary>
    /// Gets or sets a file path to the client's <c>Config/FighterConfig.xml</c>, whose Field 1..31 name row names
    /// the characters the <c>allowChr</c>/<c>denyChr</c> columns list. Read only by
    /// <see cref="Selection"/> <c>catalog_masks</c>; empty fails that mode's validation.
    /// </summary>
    public string FighterConfigPath { get; set; } = "";

    /// <summary>
    /// Gets or sets the client path of that FighterConfig workbook, read from the build's client data; at most one
    /// of this and <see cref="FighterConfigPath"/> is set, and <c>catalog_masks</c> requires exactly one of them.
    /// </summary>
    public string FighterConfigClientPath { get; set; } = "";

    /// <summary>
    /// Gets or sets how the equipment selection refuses conflicting articles. <c>id_ranges</c> (the default) keeps the
    /// native positions: the character seat and the four weapon families as item-id ranges, plus the clothing-part and
    /// accessory-slot groups of <see cref="ItemLocationsPath"/>. <c>catalog_masks</c> derives every non-character
    /// position from the workbook's own location columns (<c>baseLocation</c>, <c>slotLocation</c>,
    /// <c>partLocation</c>, <c>secPartLocation</c>; sheet reader kr-1256 <c>sub_A17CF0</c>): two articles conflict
    /// when their slot or part name sets intersect, and a wear whose <c>allowChr</c>/<c>denyChr</c> excludes the worn
    /// character is refused, mirroring the client's own gate (kr-1256 <c>sub_5F59D0</c>). Default-off until a live
    /// client confirms the derived rules: an unequipped weapon position does not return to its initial item under
    /// <c>catalog_masks</c>, and the id ranges stay the reviewed behavior. YAML key <c>selection</c>.
    /// </summary>
    [RegularExpression("id_ranges|catalog_masks")]
    public string Selection { get; set; } = "id_ranges";

    /// <summary>Gets or sets the native mainCategory value identifying clothing in the selected workbook.</summary>
    [Required]
    public string ClothingCategory { get; set; } = "Clothes";

    /// <summary>Gets or sets the native mainCategory value identifying accessory slots in the selected workbook.</summary>
    [Required]
    public string AccessoryCategory { get; set; } = "Accessories";

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (string.IsNullOrWhiteSpace(Protocol))
            yield return new ValidationResult("protocol must not be empty.", [nameof(Protocol)]);
        if (ClothingCategory == AccessoryCategory)
            yield return new ValidationResult("Clothing and accessory categories must be distinct.", [nameof(ClothingCategory), nameof(AccessoryCategory)]);
        if (PersistPresetPages && (!Enabled || !PublishEquippedViewPush))
            yield return new ValidationResult("persist_preset_pages requires enabled and publish_equipped_view_push.",
                [nameof(PersistPresetPages), nameof(Enabled), nameof(PublishEquippedViewPush)]);
        if (Selection == "catalog_masks")
        {
            if (string.IsNullOrWhiteSpace(ItemLocationsClientPath) == string.IsNullOrWhiteSpace(ItemLocationsPath))
                yield return new ValidationResult(
                    "selection catalog_masks requires exactly one of item_locations_client_path or item_locations_path: the location columns come from that workbook.",
                    [nameof(Selection), nameof(ItemLocationsClientPath), nameof(ItemLocationsPath)]);
            if (string.IsNullOrWhiteSpace(FighterConfigClientPath) == string.IsNullOrWhiteSpace(FighterConfigPath))
                yield return new ValidationResult(
                    "selection catalog_masks requires exactly one of fighter_config_client_path or fighter_config_path: allowChr and denyChr resolve through the Field 1..31 names.",
                    [nameof(Selection), nameof(FighterConfigClientPath), nameof(FighterConfigPath)]);
        }
    }
}
