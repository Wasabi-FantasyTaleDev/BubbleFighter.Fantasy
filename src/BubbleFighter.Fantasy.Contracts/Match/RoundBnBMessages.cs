using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A BnB actor's bomb-placement report, written by kr-1256 sub_B43E40.</summary>
/// <param name="Actor">The reporting character's scene identity, preserving the native word's bits.</param>
/// <param name="GridIndex">The requested cell's zero-based index in the BnB map.</param>
public sealed record RoundBnBPlacementReported(int Actor, int GridIndex) : IMessage;

/// <summary>An accepted BnB placement published to every participant, including the reporting actor.</summary>
/// <param name="Actor">The character placing the bomb.</param>
/// <param name="GridIndex">The map cell consumed by kr-1256 sub_6FC0C0 and kr-1256 sub_BFF340.</param>
public sealed record RoundBnBPlacementPublished(int Actor, int GridIndex) : IMessage;

/// <summary>The generation and map cell of one bomb selected for explosion by kr-1256 sub_B440C0.</summary>
/// <param name="Generation">The map-wide placement generation checked by kr-1256 sub_BFF540, preserving all word bits.</param>
/// <param name="GridIndex">The zero-based cell index paired with that generation.</param>
public sealed record RoundBnBBomb(int Generation, int GridIndex);

/// <summary>A BnB actor's ordered bomb-expiry report, written by kr-1256 sub_6E78A0.</summary>
/// <param name="Actor">The reporting character's scene identity.</param>
/// <param name="Bombs">The reported generation and grid pairs, in native list order.</param>
public sealed record RoundBnBExplosionReported(int Actor, IReadOnlyList<RoundBnBBomb> Bombs) : IMessage;

/// <summary>An accepted BnB explosion list published through the native chain-explosion consumer.</summary>
/// <param name="Actor">The reporting character whose accepted event is sent to every participant, including itself.</param>
/// <param name="Bombs">The generation and grid pairs consumed in order by kr-1256 sub_BFF540.</param>
public sealed record RoundBnBExplosionPublished(int Actor, IReadOnlyList<RoundBnBBomb> Bombs) : IMessage;

/// <summary>A BnB actor's item-pickup report, written by kr-1256 sub_B44490.</summary>
/// <param name="Actor">The reporting character's scene identity.</param>
/// <param name="GridIndex">The requested item cell's zero-based map index.</param>
public sealed record RoundBnBPickupReported(int Actor, int GridIndex) : IMessage;

/// <summary>A BnB pickup acknowledgement; kr-1256 sub_6FC240 takes any remaining item and clears the reporting actor's pending latch.</summary>
/// <param name="Actor">The reporting character whose pending pickup is acknowledged, including when another actor took the item.</param>
/// <param name="GridIndex">The bounded cell named by the pickup acknowledgement.</param>
public sealed record RoundBnBPickupPublished(int Actor, int GridIndex) : IMessage;
