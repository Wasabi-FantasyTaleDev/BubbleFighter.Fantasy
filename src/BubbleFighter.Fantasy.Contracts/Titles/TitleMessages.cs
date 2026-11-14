using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Titles;

/// <summary>Requests acquisition of a title definition using its catalogue medal requirements.</summary>
/// <param name="UserNumber">The client-supplied user number, which must match the authenticated user.</param>
/// <param name="TitleId">The title definition key, distinct from its associated stock entry.</param>
public sealed record TitleMakeRequested(int UserNumber, int TitleId) : IMessage;

/// <summary>Requests an equipment change for an already owned title.</summary>
/// <param name="UserNumber">The client-supplied user number, which must match the authenticated user.</param>
/// <param name="Action">The native operation: zero equips and one removes the selected title.</param>
/// <param name="TitleId">The selected title definition key.</param>
public sealed record TitleSetRequested(int UserNumber, int Action, int TitleId) : IMessage;

/// <summary>Reports acquisition after the granted inventory item and debited medals are published.</summary>
/// <param name="Result">The native result: zero is ordinary success; three opens the title reward dialog.</param>
/// <param name="TitleId">The title definition key used by the modern ordinary-success consumer.</param>
/// <param name="StockId">The stock entry resolved by the reward dialog, distinct from the title definition key.</param>
public sealed record TitleMakeReplied(int Result, int TitleId, int StockId) : IMessage;

/// <summary>Reports an equipment change after the updated inventory worn flags are published.</summary>
/// <param name="Result">The native result: one is equipped and two is removed.</param>
/// <param name="TitleId">The title definition key consumed by the modern equip and remove presentation.</param>
public sealed record TitleSetReplied(int Result, int TitleId) : IMessage;

/// <summary>Hides title definitions until the native catalogue reloads; this does not express ownership.</summary>
/// <param name="TitleIds">The additional title definition keys whose conceal flags are set.</param>
public sealed record TitleCatalogueConcealed(IReadOnlyList<int> TitleIds) : IMessage;

/// <summary>Requests the native medal information view.</summary>
/// <param name="TargetUserNumber">The user number of the player selected in the native room or player menu.</param>
public sealed record MedalInfoRequested(int TargetUserNumber) : IMessage;

/// <summary>Supplies one authoritative medal balance used by title acquisition requirements.</summary>
/// <param name="MedalId">The native medal key, formed from mode times 10000 plus the medal definition number.</param>
/// <param name="Count">The installed balance for that medal.</param>
public sealed record TitleMedalCount(int MedalId, int Count);

/// <summary>Replaces the native medal balance map and opens or refreshes its title information view.</summary>
/// <param name="Result">The native status; one installs the complete balance list.</param>
/// <param name="Medals">The complete balance map represented as medal keys and counts.</param>
public sealed record MedalInfoReplied(int Result, IReadOnlyList<TitleMedalCount> Medals) : IMessage;

/// <summary>Opens a medal or item reward presentation without changing the installed medal balances.</summary>
/// <param name="Reference">The medal definition key or fallback item reference displayed by the client.</param>
public sealed record TitleMedalPresented(int Reference) : IMessage;

/// <summary>One acquisition condition's progress for a title, drawn by the client as a bar and a "current/required" text.</summary>
/// <param name="ConditionId">The client's condition kind, such as 6 for Favorite (kr-1256 sub_113D820).</param>
/// <param name="Current">The progress made so far.</param>
/// <param name="Required">The progress the title asks for.</param>
public sealed record TitleConditionProgress(int ConditionId, int Current, int Required);

/// <summary>One title's condition progress rows.</summary>
/// <param name="TitleId">The title definition key.</param>
/// <param name="Conditions">The condition progress rows; the client matches each by its condition kind.</param>
public sealed record TitleProgress(int TitleId, IReadOnlyList<TitleConditionProgress> Conditions);

/// <summary>
/// Supplies title condition progress (S2C gspNotifyUserTitleInstance). It carries no ownership: the client keeps it until the title
/// window shows, and ownership still comes from the published inventory.
/// </summary>
/// <param name="Titles">The titles whose progress is installed.</param>
public sealed record TitleProgressNotified(IReadOnlyList<TitleProgress> Titles) : IMessage;
