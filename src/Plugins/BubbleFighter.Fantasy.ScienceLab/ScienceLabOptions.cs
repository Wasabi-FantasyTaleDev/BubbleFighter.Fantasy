using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.ScienceLab;

/// <summary>Configures explicit legacy laboratory economics independently of native UI eligibility.</summary>
public sealed class ScienceLabOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether this row accepts native science-room requests.</summary>
    public bool Enabled { get; set; }
    /// <summary>Gets or sets the audited game protocol served by this row.</summary>
    [Required] public string Protocol { get; set; } = "game";
    /// <summary>Gets or sets the canonical inventory scope that must match the native catalog.</summary>
    public string BuildKey { get; set; } = "";
    /// <summary>Gets or sets the native item, subcategory, set and stock workbook.</summary>
    [Required] public string CatalogPath { get; set; } = "Config/ShopItem2.xml";
    /// <summary>Gets or sets the native science-room card and additive definitions.</summary>
    [Required] public string ConfigurationPath { get; set; } = "Config/SynthesisSystem.ini";
    /// <summary>Gets or sets the byte cap applied before loading either native source.</summary>
    [Range(1, int.MaxValue)] public int MaxWorkbookBytes { get; set; } = 134217728;
    /// <summary>Gets or sets the combined row bound of selected worksheets and configuration lines.</summary>
    [Range(1, int.MaxValue)] public int MaxCatalogRows { get; set; } = 65536;
    /// <summary>Gets or sets the largest nonempty sparse column accepted by the workbook reader.</summary>
    [Range(1, int.MaxValue)] public int MaxCatalogColumns { get; set; } = 256;
    /// <summary>Gets or sets the maximum characters in one native cell or configuration line.</summary>
    [Range(1, int.MaxValue)] public int MaxCellCharacters { get; set; } = 16384;
    /// <summary>Gets or sets the maximum accounts whose recipe and result publication may be in flight.</summary>
    [Range(1, int.MaxValue)] public int MaxInFlightAccounts { get; set; } = 128;
    /// <summary>Gets or sets the deployment's bound for configured decomposition recipes.</summary>
    [Range(1, int.MaxValue)] public int MaxDecompositionRecipes { get; set; } = 64;
    /// <summary>Gets or sets the deployment's bound for candidates in one decomposition reward pool.</summary>
    [Range(1, int.MaxValue)] public int MaxRewardsPerRecipe { get; set; } = 128;
    /// <summary>Gets or sets the audited duration-synthesis card identity; zero disables synthesis.</summary>
    [Range(0, int.MaxValue)] public int SynthesisCardItemId { get; set; }
    /// <summary>Gets or sets the explicit card debit shared by accepted recipes.</summary>
    [Range(1, int.MaxValue)] public int? CardQuantity { get; set; }
    /// <summary>Gets or sets the survivor policy: first selection or uniform selection, with no inferred default.</summary>
    public string SynthesisSurvivor { get; set; } = "";
    /// <summary>Gets or sets the finite duration cap, below the native 730-day permanent presentation threshold.</summary>
    [Range(double.Epsilon, 730)] public double? SynthesisMaximumDays { get; set; }
    /// <summary>Gets or sets the summed duration that the deployment elects to promote to permanent ownership.</summary>
    [Range(double.Epsilon, 730)] public double? SynthesisPermanentAtDays { get; set; }
    /// <summary>Gets or sets the native unlimited-duration additive identity; zero disables that additive.</summary>
    [Range(0, int.MaxValue)] public int PermanentAdditiveItemId { get; set; }
    /// <summary>Gets or sets the explicit unlimited-additive debit.</summary>
    [Range(1, int.MaxValue)] public int? PermanentAdditiveQuantity { get; set; }
    /// <summary>Gets or sets the explicit chance of permanent synthesis when its additive is selected.</summary>
    [Range(0, 10000)] public int? PermanentAdditiveBasisPoints { get; set; }
    /// <summary>Gets or sets the native guaranteed-bonus additive identity; zero disables that additive.</summary>
    [Range(0, int.MaxValue)] public int BonusAdditiveItemId { get; set; }
    /// <summary>Gets or sets the explicit guaranteed-bonus additive debit.</summary>
    [Range(1, int.MaxValue)] public int? BonusAdditiveQuantity { get; set; }
    /// <summary>Gets or sets the guaranteed reward when the bonus additive is accepted.</summary>
    public LaboratoryRewardOptions? BonusReward { get; set; }
    /// <summary>Gets or sets the bonus granted when the unlimited additive fails its roll.</summary>
    public LaboratoryRewardOptions? PermanentFailureReward { get; set; }
    /// <summary>Gets or sets explicit decomposition card/category reward pools and their weights.</summary>
    public LaboratoryDecompositionOptions[] Decomposition { get; set; } = [];

    /// <summary>Requires complete explicit policy before activating any destructive recipe.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Enabled) yield break;
        if (string.IsNullOrWhiteSpace(BuildKey)) yield return new("Enabled science-lab requires build_key.");
        if (CardQuantity is null) yield return new("Enabled science-lab requires explicit card_quantity.");
        if (SynthesisCardItemId == 0 && (Decomposition is null || Decomposition.Length == 0))
            yield return new("Enabled science-lab requires at least one configured operation.");
        if (SynthesisCardItemId > 0 && (SynthesisSurvivor is not ("first" or "uniform")
            || SynthesisMaximumDays is null || SynthesisPermanentAtDays is null
            || !double.IsFinite(SynthesisMaximumDays.Value) || !double.IsFinite(SynthesisPermanentAtDays.Value)
            || SynthesisMaximumDays <= 0 || SynthesisPermanentAtDays > 730
            || SynthesisMaximumDays >= SynthesisPermanentAtDays))
            yield return new("Synthesis requires an explicit survivor, finite maximum and a higher permanent threshold.");
        if (PermanentAdditiveItemId > 0 && (PermanentAdditiveQuantity is null || PermanentAdditiveBasisPoints is null
            || PermanentAdditiveBasisPoints < 10000 && PermanentFailureReward is null))
            yield return new("The permanent additive requires explicit debit, chance and a failure reward when it can fail.");
        if (BonusAdditiveItemId > 0 && (BonusAdditiveQuantity is null || BonusReward is null))
            yield return new("The bonus additive requires an explicit debit and reward.");
        if (new[] { SynthesisCardItemId, PermanentAdditiveItemId, BonusAdditiveItemId }.Where(id => id > 0).Distinct().Count()
            != new[] { SynthesisCardItemId, PermanentAdditiveItemId, BonusAdditiveItemId }.Count(id => id > 0))
            yield return new("Science-room cards and additives must have distinct identities.");
        if (Decomposition is null || Decomposition.Length > MaxDecompositionRecipes || Decomposition.Any(recipe => recipe is null)
            || Decomposition.Select(recipe => (recipe.CardItemId, recipe.Category)).Distinct().Count() != Decomposition.Length)
            yield return new("Decomposition recipes must be distinct and bounded.");
        else
            foreach (var recipe in Decomposition)
            {
                if (recipe.Rewards?.Length > MaxRewardsPerRecipe)
                    yield return new("Decomposition rewards exceed max_rewards_per_recipe.");
                foreach (var result in recipe.Validate()) yield return result;
            }
        foreach (var reward in new[] { BonusReward, PermanentFailureReward }.OfType<LaboratoryRewardOptions>())
            foreach (var result in reward.Validate()) yield return result;
    }
}

/// <summary>Defines one explicit native catalog reward with a bounded selection weight.</summary>
public sealed class LaboratoryRewardOptions
{
    /// <summary>Gets or sets the native Stock identity whose item and advertised quantity match this reward.</summary>
    public int StockId { get; set; }
    /// <summary>Gets or sets the result's native item identity.</summary>
    public int ItemId { get; set; }
    /// <summary>Gets or sets counted or finite-day stock reward semantics.</summary>
    public InventoryGrantKind Kind { get; set; }
    /// <summary>Gets or sets the integral count or days advertised by the selected native Stock row.</summary>
    public double Amount { get; set; }
    /// <summary>Gets or sets the maximum resulting owned count or duration.</summary>
    public double Maximum { get; set; }
    /// <summary>Gets or sets this reward's positive weight in a decomposition pool.</summary>
    public int Weight { get; set; } = 1;
    /// <summary>Creates the immutable term used by the store without rerolling its identity.</summary>
    public InventoryLaboratoryGrantTerm Term() => new(ItemId, Kind, Amount, Maximum);
    /// <summary>Reports malformed reward terms before the plugin accepts requests.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (StockId is < 1000000 or >= 2000000)
            yield return new("Laboratory reward stock_id must use the native ordinary Stock identity range.");
        if (Kind == InventoryGrantKind.Permanent)
            yield return new("Permanent stock rewards have no audited native result presentation; synthesis-primary permanent ownership is configured separately.");
        if (Kind == InventoryGrantKind.Days && Amount != Math.Truncate(Amount))
            yield return new("Stock-backed day rewards require the native integral Stock.duration.");
        string? error = null;
        try { Term().Validate(); }
        catch (ArgumentException exception) { error = exception.Message; }
        if (error is not null) yield return new(error);
        if (Weight < 1) yield return new("Laboratory reward weight must be positive.");
    }
}

/// <summary>Associates one native decomposition card and material category with an explicit reward pool.</summary>
public sealed class LaboratoryDecompositionOptions
{
    /// <summary>Gets or sets the native ordinary or advanced decomposition card identity.</summary>
    public int CardItemId { get; set; }
    /// <summary>Gets or sets the native accessories, clothing or weapons category value.</summary>
    public int Category { get; set; }
    /// <summary>Gets or sets the configured reward candidates; an empty list never invents a fallback.</summary>
    public LaboratoryRewardOptions[] Rewards { get; set; } = [];
    /// <summary>Checks the card, category, reward count and total selection weight.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (CardItemId <= 0 || Category is < 0 or > 2 || Rewards is null || Rewards.Length < 1
            || Rewards.Any(reward => reward is null))
        {
            yield return new("Decomposition requires a positive card, native category and at least one reward.");
            yield break;
        }
        if (Rewards.Select(reward => reward.ItemId).Distinct().Count() != Rewards.Length)
            yield return new("Decomposition reward identities must be distinct.");
        // Random.Next uses an Int32 exclusive upper bound for the cumulative weighted pool.
        if (Rewards.Sum(reward => (long)reward.Weight) > int.MaxValue)
            yield return new("Decomposition total reward weight exceeds the Int32 random-sampling range.");
        foreach (var reward in Rewards)
            foreach (var result in reward.Validate()) yield return result;
    }
}
