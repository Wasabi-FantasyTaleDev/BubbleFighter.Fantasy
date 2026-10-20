using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

/// <summary>Deployment-controlled cooperative reward-card selection and item grants.</summary>
public sealed partial class InGameOptions
{
    /// <summary>Gets or sets a file path to the native shop workbook used to validate card identities and grant terms.</summary>
    public string LotteryWorkbookPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets that workbook's client path (for example <c>Config/ShopItem2.xml</c>), read from the build's
    /// client data; exactly one of this and <see cref="LotteryWorkbookPath"/> is set when reward cards are on.
    /// </summary>
    public string LotteryWorkbookClientPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a custom result-to-card interval; zero opens cards when the ordinary result would return to the room.</summary>
    [Range(0, 60000)]
    public int LotteryOpenDelayMs { get; set; }
    /// <summary>Gets or sets modes whose successful completion opens reward cards; empty disables them.</summary>
    [MaxLength(32)]
    public int[] LotteryModes { get; set; } = [];

    /// <summary>Gets or sets the time allowed for choosing a card before automatic selection.</summary>
    [Range(1000, 60000)]
    public int LotterySelectTimeMs { get; set; } = 7000;

    /// <summary>Gets or sets the interval displaying revealed cards before returning to the room.</summary>
    [Range(1000, 60000)]
    public int LotteryRevealTimeMs { get; set; } = 5000;

    /// <summary>Gets or sets the bounded retry count for concurrent inventory changes.</summary>
    [Range(1, 10)]
    public int LotteryGrantRetryCount { get; set; } = 3;

    /// <summary>Gets or sets whether cards reserve crew pieces and currency before drawing other groups.</summary>
    public bool LotteryGroupedDraw { get; set; }

    /// <summary>Gets or sets the maximum cards sharing one equipment or bundle family, across durations.</summary>
    [Range(1, 10)]
    public int LotteryMaxDuplicates { get; set; } = 3;

    /// <summary>Gets or sets the percent selection weight retained for each existing occurrence.</summary>
    [Range(1, 100)]
    public int LotteryRepeatWeightPercent { get; set; } = 25;

    /// <summary>Gets or sets weights for one, two or three crew pieces on each board.</summary>
    public int[] LotteryCrewCountWeights { get; set; } = [];

    /// <summary>Gets or sets weights for one, two, three or four currency cards on each board.</summary>
    public int[] LotteryCoinCountWeights { get; set; } = [];

    /// <summary>Gets or sets relative weights of the groups filling unreserved cards.</summary>
    public LotteryGroupWeight[] LotteryGroupWeights { get; set; } = [];

    /// <summary>Gets or sets weighted equipment rewards, verified to exist in this client's resources.</summary>
    [MaxLength(128)]
    public LotteryItem[] LotteryItems { get; set; } = [];
}

/// <summary>One configurable reward-card entry; weights are reconstruction values rather than official odds.</summary>
public sealed class LotteryItem
{
    /// <summary>Gets or sets the selection family; bundle duration variants share the same family.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the currency credited atomically; currency cards do not create inventory rows.</summary>
    [Range(0, 1000000)]
    public int Lucci { get; set; }

    /// <summary>Gets or sets the equipment components of a native StockSet bundle.</summary>
    [MaxLength(10)]
    public LotteryItem[] Components { get; set; } = [];

    /// <summary>Gets or sets the native Stock identity used for the card's image, name and duration.</summary>
    [Range(1000000, 2999999)]
    public int DisplayId { get; set; }
    /// <summary>Gets or sets the native equipment identity, not a shop Stock or StockSet identity.</summary>
    [Range(0, int.MaxValue)]
    public int ItemId { get; set; }

    /// <summary>Gets or sets the consumable count, or one for equipment.</summary>
    [Range(1, 100000)]
    public int Quantity { get; set; } = 1;

    /// <summary>Gets or sets whether repeated grants increase a consumable count instead of renewing equipment.</summary>
    public bool Stackable { get; set; }

    /// <summary>Gets or sets relative selection weight.</summary>
    [Range(1, 100000)]
    public int Weight { get; set; } = 1;

    /// <summary>Gets or sets ownership duration in days; zero grants permanent ownership.</summary>
    [Range(0, 3650)]
    public int Days { get; set; }

    /// <summary>Gets or sets an optional description for administrators.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>Relative draw weight for one optional reward group after reserved cards are allocated.</summary>
public sealed class LotteryGroupWeight
{
    /// <summary>Gets or sets the configured reward family name.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the positive relative category weight.</summary>
    [Range(1, 100000)]
    public int Weight { get; set; }
}
