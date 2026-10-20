namespace BubbleFighter.Fantasy.Packets;

/// <summary>Bounds decoded packet trees before strings, collections or nested maps are allocated.</summary>
/// <param name="MaxStringCharacters">Maximum UTF-16 character count for one string.</param>
/// <param name="MaxCollectionElements">Maximum element count of one counted list or fixed run.</param>
/// <param name="MaxValues">Maximum total fields and collection elements in the decoded tree.</param>
/// <param name="MaxDepth">Maximum nesting depth, including the root structure and collection containers.</param>
public sealed record PacketReadLimits(int MaxStringCharacters = 1024, int MaxCollectionElements = 4096,
    int MaxValues = 16384, int MaxDepth = 16)
{
    internal void Validate()
    {
        if (MaxStringCharacters is < 0 or > (ushort.MaxValue - 4) / 2)
            throw new ArgumentOutOfRangeException(nameof(MaxStringCharacters));
        if (MaxCollectionElements is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxCollectionElements));
        if (MaxValues is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxValues));
        if (MaxDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxDepth));
    }
}
