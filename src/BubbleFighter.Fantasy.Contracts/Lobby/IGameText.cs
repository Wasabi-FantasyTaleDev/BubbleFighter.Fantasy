using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Resolves game-facing messages for a client region with a configured fallback language.</summary>
public interface IGameText
{
    /// <summary>Formats a notice in the receiving user's Launcher language, falling back to the deployment default when unspecified.</summary>
    string Format(ISession session, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
        Format("", key, arguments);

    /// <summary>Resolves a key in the receiving user's Launcher language or the deployment fallback without inferring language from the build.</summary>
    bool TryFormat(ISession session, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null) =>
        TryFormat("", key, out text, arguments);

    /// <summary>Formats a resource key for the client's region, replacing named placeholders with caller values.</summary>
    string Format(string region, string key, IReadOnlyDictionary<string, string>? arguments = null);

    /// <summary>
    /// Formats a resource key like <see cref="Format(string, string, IReadOnlyDictionary{string, string}?)"/> but reports a key that no language of the lookup defines
    /// instead of throwing, for catalogs such as game mode names where a missing entry is an expected case.
    /// </summary>
    /// <param name="region">The client region selecting the language.</param>
    /// <param name="key">The resource key.</param>
    /// <param name="text">The formatted text when the key exists; otherwise empty.</param>
    /// <param name="arguments">Named placeholder values, or null.</param>
    /// <returns>True when the key exists in the region's language or the fallback language.</returns>
    bool TryFormat(string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null);

    /// <summary>
    /// Formats a resource key for one client build. A build can have its own wording (for example the mode names its
    /// client shows) layered over its region's language; implementations without per-build wording use the region.
    /// </summary>
    /// <param name="profile">The client build the text is for.</param>
    /// <param name="key">The resource key.</param>
    /// <param name="arguments">Named placeholder values, or null.</param>
    /// <returns>The formatted text.</returns>
    string Format(ClientProfile profile, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
        Format(profile.Region, key, arguments);

    /// <summary>Like <see cref="TryFormat(string, string, out string, IReadOnlyDictionary{string, string}?)"/> but for one client build.</summary>
    /// <param name="profile">The client build the text is for.</param>
    /// <param name="key">The resource key.</param>
    /// <param name="text">The formatted text when the key exists; otherwise empty.</param>
    /// <param name="arguments">Named placeholder values, or null.</param>
    /// <returns>True when the key exists in the build's, the region's or the fallback language.</returns>
    bool TryFormat(ClientProfile profile, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null) =>
        TryFormat(profile.Region, key, out text, arguments);
}
