using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The mode word of a clan search, as the client's search dialog sets it (kr-1256 sub_57BA90): a button picks the
/// list and the recruiting check box picks its recruiting-only twin. Only the name search carries text.
/// </summary>
public static class ClanSearchMode
{
    /// <summary>The search button with a typed text: clans whose name contains it.</summary>
    public const int Name = 4;

    /// <summary>The points button: every clan, the most points first.</summary>
    public const int Points = 5;

    /// <summary>The refresh button: every clan.</summary>
    public const int All = 6;

    /// <summary>The refresh button with the recruiting check box ticked.</summary>
    public const int AllRecruiting = 7;

    /// <summary>The points button with the recruiting check box ticked.</summary>
    public const int PointsRecruiting = 8;
}

/// <summary>
/// A clan search (clan C2S <c>ClanSearch</c>, writer kr-1256 sub_526020). It is answered with <see cref="ClanSearchSent"/>.
/// </summary>
/// <param name="Mode">The dialog's mode word, one of <see cref="ClanSearchMode"/>.</param>
/// <param name="Page">The 1-based page.</param>
/// <param name="Text">The text a clan name must contain; the dialog clears it for every mode but the name search.</param>
public sealed record ClanSearchRequested(int Mode, int Page, string Text) : IMessage;

/// <summary>One row of <see cref="ClanSearchSent"/>.</summary>
/// <param name="Clan">The clan.</param>
/// <param name="MasterName">The master's nickname.</param>
/// <param name="LastLoginAt">The latest clan login of any of its seated members (UTC).</param>
public sealed record ClanSearchRow(ClanRecord Clan, string MasterName, DateTime LastLoginAt);

/// <summary>
/// One page of a clan search (clan S2C <c>ClanSearchResult</c>, handler kr-1256 sub_5290A0). The client shows the
/// rows in a list of five and keeps the page for its previous and next buttons (kr-1256 sub_57CFE0).
/// </summary>
/// <param name="Mode">The request's mode word, echoed.</param>
/// <param name="Page">The page answered.</param>
/// <param name="Text">The request's text, echoed.</param>
/// <param name="Clans">The clans of the page.</param>
public sealed record ClanSearchSent(int Mode, int Page, string Text, IReadOnlyList<ClanSearchRow> Clans) : IMessage;
