using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Contracts.BanPick;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>Options of the server-wide <c>banpick</c> row.</summary>
public sealed class BanPickOptions : IValidatableObject
{
    /// <summary>Gets or sets the document the server starts with when no saved state exists. YAML key <c>policy</c>.</summary>
    public BanPickPolicyOptions Policy { get; set; } = new();

    /// <summary>
    /// Gets or sets the file administrators' edits are saved to; empty keeps the configured document read-only.
    /// YAML key <c>state_path</c>.
    /// </summary>
    public string StatePath { get; set; } = "data/banpick-policy.json";

    /// <summary>Gets or sets the largest saved document in bytes. YAML key <c>max_state_bytes</c>.</summary>
    [Range(1024, 67_108_864)] public int MaxStateBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Gets or sets how many entries an announcement names before it gives the count and points at the full list.
    /// YAML key <c>announcement_items</c>.
    /// </summary>
    [Range(1, 1000)] public int AnnouncementItems { get; set; } = 25;

    /// <summary>Gets or sets how long a host without the overlay has to press start again. YAML key <c>reclick_window_ms</c>.</summary>
    [Range(1000, 600_000)] public int ReclickWindowMs { get; set; } = 10_000;

    /// <summary>Gets or sets how long the overlay's start confirmation stays answerable. YAML key <c>confirm_timeout_ms</c>.</summary>
    [Range(1000, 600_000)] public int ConfirmTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Gets or sets the entries one room's list may hold; it bounds memory per room, not anything a player sees.
    /// YAML key <c>max_room_entries</c>.
    /// </summary>
    [Range(1, 65_536)] public int MaxRoomEntries { get; set; } = 4096;

    /// <summary>Gets or sets how many matches an ambiguous name lists. YAML key <c>search_results</c>.</summary>
    [Range(1, 200)] public int SearchResults { get; set; } = 10;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        ValidationResult? error = null;
        try { BanPickPolicyRules.Validate((Policy ?? throw new BanPickPolicyException("ban_pick_invalid")).ToContract()); }
        catch (BanPickPolicyException rejected)
        {
            error = new($"policy is invalid ({rejected.Code}{(rejected.Policy is null ? "" : $", policy '{rejected.Policy}'")}" +
                $"{(rejected.Rule is null ? "" : $", entry '{rejected.Rule}'")}{(rejected.Other is null ? "" : $" and rule '{rejected.Other}'")}).", [nameof(Policy)]);
        }
        if (error is not null) yield return error;
        if (StatePath is null) yield return new("state_path must be a path or empty.", [nameof(StatePath)]);
    }
}

/// <summary>The configured document; <see cref="ToContract"/> gives the shared record the validator and the rooms use.</summary>
public sealed class BanPickPolicyOptions
{
    /// <summary>Gets or sets the named item groups.</summary>
    public BanPickGroupOptions[] Groups { get; set; } = [];

    /// <summary>Gets or sets the server-wide policies.</summary>
    public BanPickPolicyEntryOptions[] Policies { get; set; } = [];

    /// <summary>Converts the bound configuration; null entries stay null so validation rejects them.</summary>
    public BanPickPolicy ToContract() => new(
        (Groups ?? throw new BanPickPolicyException("ban_pick_invalid")).Select(group => group is null ? null! :
            new BanPickGroup(group.Name, group.MainCategories, group.SubCategories, group.Functions, group.Items)).ToArray(),
        (Policies ?? throw new BanPickPolicyException("ban_pick_invalid")).Select(entry => entry is null ? null! :
            new BanPickPolicyEntry(entry.Id, entry.Name, entry.Description,
                new BanPickScope(entry.Builds, entry.Modes, entry.Rooms),
                (entry.Rules ?? throw new BanPickPolicyException("ban_pick_invalid", entry.Id))
                    .Select(rule => rule is null ? null! : new BanPickRule(rule.Id, rule.Type, rule.Items, rule.Groups)).ToArray())).ToArray());
}

/// <summary>A configured item group.</summary>
public sealed class BanPickGroupOptions
{
    /// <summary>Gets or sets the lowercase name rules and commands use.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the selected main category ids.</summary>
    public int[] MainCategories { get; set; } = [];
    /// <summary>Gets or sets the selected sub category ids.</summary>
    public int[] SubCategories { get; set; } = [];
    /// <summary>Gets or sets the selected function values.</summary>
    public int[] Functions { get; set; } = [];
    /// <summary>Gets or sets exact item ids.</summary>
    public int[] Items { get; set; } = [];
}

/// <summary>A configured server-wide policy.</summary>
public sealed class BanPickPolicyEntryOptions
{
    /// <summary>Gets or sets the stable identity.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets an optional label.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets an optional management note.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets the build ids; empty is every build.</summary>
    public string[] Builds { get; set; } = [];
    /// <summary>Gets or sets the mode ids; empty is every mode.</summary>
    public int[] Modes { get; set; } = [];
    /// <summary>Gets or sets all, public or password.</summary>
    public string Rooms { get; set; } = BanPickRoomScopes.All;
    /// <summary>Gets or sets the policy's typed rules.</summary>
    public BanPickRuleOptions[] Rules { get; set; } = [];
}

/// <summary>A configured rule.</summary>
public sealed class BanPickRuleOptions
{
    /// <summary>Gets or sets the stable identity within its policy.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets block, confirm or warn.</summary>
    public string Type { get; set; } = "";
    /// <summary>Gets or sets exact item ids.</summary>
    public int[] Items { get; set; } = [];
    /// <summary>Gets or sets group names.</summary>
    public string[] Groups { get; set; } = [];
}

/// <summary>Options of a build's <c>banpick.room</c> row.</summary>
public sealed class BanPickRoomOptions
{
    /// <summary>Gets or sets the game protocol whose rooms the row guards. YAML key <c>protocol</c>.</summary>
    [Required(AllowEmptyStrings = false)] public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the build's item workbook. YAML key <c>shop_item_client_path</c>.</summary>
    [Required(AllowEmptyStrings = false)] public string ShopItemClientPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the inclusive workbook size cap. YAML key <c>max_shop_item_bytes</c>.</summary>
    [Range(1, long.MaxValue)] public long MaxShopItemBytes { get; set; } = 134_217_728;
}
