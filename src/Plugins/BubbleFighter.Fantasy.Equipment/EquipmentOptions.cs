using BubbleFighter.Fantasy.Contracts.Inventory;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Equipment;

/// <summary>
/// Configuration for the <see cref="EquipmentPlugin"/> row. Protocol name and
/// <see cref="VersionMajor"/> are supplied by the profile; this plugin does not
/// hard-code a client region in its id.
/// </summary>
public sealed class EquipmentOptions : IValidatableOptions
{
    /// <summary>Gets or sets the game-connection protocol name this row handles.</summary>
    [Required]
    public string Protocol { get; set; } = "kr-1256/game";

    /// <summary>Gets or sets the client profile id that selected this dialect.</summary>
    [Required]
    public string ClientProfile { get; set; } = "kr-1256";

    /// <summary>
    /// Gets or sets the client major version this row serves. The row is
    /// build-neutral: the mounted codec bindings decide which wire surface exists.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int VersionMajor { get; set; } = 1256;

    /// <summary>
    /// Gets or sets whether this row registers its typed equipment handler. Disable
    /// both this row and the equipment codec row to remove the entire capability;
    /// an enabled codec without a handler rejects requests instead of acknowledging them.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of distinct worn keys tracked per connection.
    /// Each connection's set lives in the plugin-owned <see cref="Sessions.EquippedItemScope"/>
    /// and is dropped on close; this bound caps a misbehaving client that presents an
    /// unbounded stream of distinct keys.
    /// </summary>
    [Range(1, 4096)]
    public int MaxEquippedPerSession { get; set; } = 256;

    /// <summary>
    /// Gets or sets the maximum number of live connections whose equipped sets this row
    /// retains. The table refuses a new connection beyond the cap instead of growing
    /// without bound; size it at or above the game listener's connection limit.
    /// </summary>
    [Range(1, 65536)]
    public int MaxTrackedConnections { get; set; } = 1024;

    /// <summary>
    /// Gets or sets whether requests persist exclusive equipment selection and refresh
    /// inventory flags, weapon presets and the actual equipped view. Requires IInventoryRepository,
    /// IInventoryPushService, IEquipmentSelection and IEquippedViewPush; activation fails if a
    /// provider is missing instead of acknowledging a change that cannot update the model.
    /// With <see langword="false"/> the row acknowledges requests and keeps only the
    /// bounded connection-local diagnostic set; durable worn state is unchanged.
    /// </summary>
    public bool PersistWornState { get; set; } = true;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (string.IsNullOrWhiteSpace(Protocol))
            yield return new ValidationResult("protocol must not be empty.", [nameof(Protocol)]);
    }
}
