using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Companion;

/// <summary>One button of a client message box: a stable identity reported on selection and its caption.</summary>
/// <param name="Id">Identity of ASCII letters, digits, dots, underscores or hyphens.</param>
/// <param name="Label">Caption; some native button pictures already carry their own text.</param>
public sealed record ClientUiButton(string Id, string Label);

/// <summary>One coloured run of a local chat entry.</summary>
/// <param name="Text">Non-empty text.</param>
/// <param name="Color"><c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</param>
public sealed record ClientUiTextRun(string Text, string Color);

/// <summary>
/// A piece of the game's own interface the server asks a player's launcher to show: <c>messageBox</c>,
/// <c>notification</c>, <c>announcement.show</c>, <c>announcement.close</c> or <c>chat</c> (a local entry only; nothing is
/// sent to other players). The launcher uses the game's native control when the client supports it and, when
/// <see cref="Fallback"/> allows, draws a message box or notification in its overlay otherwise.
/// </summary>
/// <param name="Kind">The operation.</param>
/// <param name="Text">Body text; empty for <c>chat</c> (which uses <see cref="Runs"/>) and <c>announcement.close</c>.</param>
public sealed record ClientUiRequest(string Kind, string Text)
{
    /// <summary>Gets the message box title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Gets the message box icon: none, notice, warning, question or error.</summary>
    public string Icon { get; init; } = "notice";

    /// <summary>Gets the message box buttons; at least one for a message box, none otherwise.</summary>
    public IReadOnlyList<ClientUiButton> Buttons { get; init; } = [];

    /// <summary>Gets the caller's announcement identity, for builds whose native banner can be replaced or closed.</summary>
    public string? AnnouncementId { get; init; }

    /// <summary>Gets the chat entry style: normal or system.</summary>
    public string ChatStyle { get; init; } = "system";

    /// <summary>Gets the chat caption; null uses the launcher's default for the style.</summary>
    public string? ChatCaption { get; init; }

    /// <summary>Gets the coloured runs of a chat entry.</summary>
    public IReadOnlyList<ClientUiTextRun> Runs { get; init; } = [];

    /// <summary>Gets whether the launcher may draw a message box or notification in its overlay when the native control cannot show.</summary>
    public bool Fallback { get; init; } = true;
}

/// <summary>What became of a client interface request.</summary>
/// <param name="Status">
/// <c>selected</c>, <c>shown</c> (a notification, announcement or chat entry was presented), <c>closed</c>, <c>dismissed</c>,
/// <c>unsupported</c>, <c>notReady</c>, <c>busy</c>, <c>failed</c>, <c>outcomeUnknown</c>, <c>unavailable</c> (no launcher
/// connection negotiated the request) or <c>timeout</c>.
/// </param>
/// <param name="ButtonId">The selected button, for <c>selected</c>.</param>
/// <param name="Presenter"><c>native</c>, <c>overlay</c> or empty when nothing was shown.</param>
public sealed record ClientUiOutcome(string Status, string? ButtonId, string Presenter);

/// <summary>
/// Shows the game's own interface to one player through their launcher (Nirinsou Link): server plugins and developer
/// tools use it to open a message box, show a notification or banner, or add a local chat line, and learn the outcome.
/// </summary>
public interface IClientNativeUi
{
    /// <summary>Whether the player's launcher negotiated client interface requests.</summary>
    /// <param name="session">The player's game session.</param>
    bool Supports(ISession session);

    /// <summary>
    /// Sends the request and completes with its final outcome: the player's answer for a message box, the presentation
    /// for the other kinds. A player without a negotiated launcher gets <c>unavailable</c> at once.
    /// </summary>
    /// <param name="session">The player's game session.</param>
    /// <param name="request">The interface to show.</param>
    /// <param name="ct">Cancellation token; cancelling stops waiting, not what the player already sees.</param>
    /// <exception cref="ArgumentException">The request is malformed for its kind.</exception>
    ValueTask<ClientUiOutcome> ShowAsync(ISession session, ClientUiRequest request, CancellationToken ct);
}
