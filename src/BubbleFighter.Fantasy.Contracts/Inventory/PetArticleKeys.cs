namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Defines the shared Article lookup range proven in the supported modern clients.</summary>
public static class PetArticleKeys
{
    // kr-1256 sub_806CE0 at 0x806CFF uses jae after cmp low,0x5F5E100 when the high word is zero.
    // cn-774 sub_9A6A70 at 0x9A6AA7 and tw-454 sub_C05410 at 0xC05447 use the same inclusive branch.
    /// <summary>Gets the smallest identity routed to Article lookup, including equality at the low-word boundary.</summary>
    public const ulong First = 100000000;

    // kr-1256 sub_806CE0, cn-774 sub_9A6A70 and tw-454 sub_C05410 route a negative high word to ordinary lookup.
    /// <summary>Gets the largest Article identity usable by every supported modern client's signed discriminator.</summary>
    public const ulong Last = long.MaxValue;

    /// <summary>Tests native usability without narrowing or altering the stored unsigned identity.</summary>
    public static bool IsAddressable(ulong key) => key is >= First and <= Last;
}
