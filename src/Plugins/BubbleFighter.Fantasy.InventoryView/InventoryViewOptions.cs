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

    /// <summary>Gets or sets a file path to the client's ShopItem workbook supplying clothing parts and accessory slots; empty disables those positions.</summary>
    public string ItemLocationsPath { get; set; } = "";

    /// <summary>
    /// Gets or sets the client path of that workbook (for example <c>Config/ShopItem2.xml</c>), read from the
    /// build's client data; at most one of this and <see cref="ItemLocationsPath"/> is set.
    /// </summary>
    public string ItemLocationsClientPath { get; set; } = "";

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
    }
}
