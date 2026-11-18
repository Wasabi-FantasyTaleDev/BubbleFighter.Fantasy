using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>The tab requests either a board's top page or the logged-in player's own page.</summary>
public sealed record PirateAssaultRankingRequested(string Build, uint RankId, bool Personal) : IMessage;

/// <summary>A native board in one client build's data domain; a null account selects the top page.</summary>
public sealed record PirateAssaultRankingKey(string Build, uint RankId, int? UserNum);

/// <summary>An authoritative native row. The three uninterpreted words and the supplied rank are preserved.</summary>
public sealed record PirateAssaultRankingRow(uint Unknown0, string Name, uint Unknown28, uint Unknown32,
    uint ClearTimeMilliseconds, uint Rank);

/// <summary>A complete ordered answer for the exact board and audience, at the source's OLE date.</summary>
public sealed record PirateAssaultRankingPage(PirateAssaultRankingKey Key, double TableTime,
    IReadOnlyList<PirateAssaultRankingRow> Rows) : IMessage;

/// <summary>Supplies already-ranked Pirate Assault pages; aggregate mode statistics are not a page source.</summary>
public interface IPirateAssaultRankingSource
{
    /// <summary>Returns a detached page in display order (possibly known empty), or null when the requested view is unknown.</summary>
    ValueTask<PirateAssaultRankingPage?> ReadAsync(PirateAssaultRankingKey key, CancellationToken ct);
}
