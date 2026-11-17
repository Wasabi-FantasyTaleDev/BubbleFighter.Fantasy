namespace BubbleFighter.Fantasy.Contracts.CashInbox;

/// <summary>Protocol and configuration bounds for cash inbox storage and wire publication.</summary>
public static class CashInboxLimits
{
    /// <summary>The maximum number of active inbox entries a player may keep when capped by configuration.</summary>
    public const int MaxEntriesPerPlayerCap = 256;

    /// <summary>Default maximum active cash inbox entries per player.</summary>
    public const int DefaultMaxEntriesPerPlayer = 64;

    /// <summary>Minimum allowed value for the encoded cash inbox update frame ceiling.</summary>
    public const int MinFrameBytes = 512;

    /// <summary>Default byte ceiling for <c>gspCashInvenUpdateAllInventorySlot</c> payloads.</summary>
    public const int DefaultMaxFrameBytes = 60_000;

    /// <summary>Maximum allowed frame ceiling for a single <c>gspCashInvenUpdateAllInventorySlot</c> payload.</summary>
    public const int MaxFrameBytesCap = 65_535;

    /// <summary>Default cash inbox entry lifetime in seconds (30 days).</summary>
    public const int DefaultTtlSeconds = 2_592_000;

    /// <summary>Maximum configurable cash inbox entry lifetime in seconds.</summary>
    public const int MaxTtlSeconds = 315_360_000;

    /// <summary>Maximum UTF-16 character length stored for the slot note text (<c>unknown_24</c>).</summary>
    public const int MaxNoteChars = 256;

    /// <summary>Maximum length of an idempotent system/reward claim key.</summary>
    public const int MaxClaimKeyChars = 64;

    /// <summary>Largest slot identifier expressible in the 32-bit wire slot field.</summary>
    public const long MaxCashSlotId = uint.MaxValue;
}
