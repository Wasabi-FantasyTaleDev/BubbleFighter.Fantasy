using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Companion;

namespace BubbleFighter.Fantasy.Contracts.BanPick;

/// <summary>The server-wide ban-pick row's tunables that every build's room rules use.</summary>
/// <param name="AnnouncementItems">How many entries an announcement names before it gives the count instead.</param>
/// <param name="ReclickWindowMs">How long a host without the overlay has to press start again.</param>
/// <param name="ConfirmTimeoutMs">How long the overlay's start confirmation stays answerable.</param>
/// <param name="MaxRoomEntries">The entries one room's list may hold; a memory bound per room.</param>
/// <param name="SearchResults">How many matches an ambiguous name lists.</param>
public sealed record BanPickSettings(int AnnouncementItems, int ReclickWindowMs, int ConfirmTimeoutMs, int MaxRoomEntries, int SearchResults);

/// <summary>One item of a build's catalog as a search result.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Name">The display name in the build's language.</param>
public sealed record BanPickCatalogEntry(int Id, string Name);

/// <summary>
/// The server-wide ban-pick row as each build's room rules see it: the document, the tunables, the optional companion
/// channel and the registration that routes commands and overlay messages to the build.
/// </summary>
public interface IBanPickHub
{
    /// <summary>Gets the document store.</summary>
    IBanPickPolicyStore Store { get; }

    /// <summary>Gets the tunables.</summary>
    BanPickSettings Settings { get; }

    /// <summary>Gets the companion channel, or null when the profile has none.</summary>
    ICompanionHub? Companion { get; }

    /// <summary>Adds one build's rules; hand the handle to the registering row's effects.</summary>
    /// <param name="scope">The build's rules.</param>
    IDisposable Register(IBanPickScope scope);
}

/// <summary>One build's room rules as the server-wide row routes commands and overlay messages to them.</summary>
public interface IBanPickScope
{
    /// <summary>Gets the client build.</summary>
    string ClientBuild { get; }

    /// <summary>Returns the game session when it is seated in one of this build's rooms, or null.</summary>
    /// <param name="sessionId">A game session id.</param>
    ISession? FindSeated(long sessionId);

    /// <summary>Items of this build whose name contains the text.</summary>
    /// <param name="text">The text, compared ignoring case.</param>
    /// <param name="limit">The most results to return.</param>
    IReadOnlyList<BanPickCatalogEntry> Search(string text, int limit);

    /// <summary>Runs a room command for a seated player.</summary>
    /// <param name="session">The player's game session.</param>
    /// <param name="language">The caller's UI language.</param>
    /// <param name="arguments">The command's words after its name.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<CommandResult> RoomCommandAsync(ISession session, string language, IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>Handles one overlay message.</summary>
    /// <param name="type">The message type.</param>
    /// <param name="caller">Who sent it.</param>
    /// <param name="payload">The message payload.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<object?> CompanionAsync(string type, CompanionCaller caller, JsonElement payload, CancellationToken ct);
}
