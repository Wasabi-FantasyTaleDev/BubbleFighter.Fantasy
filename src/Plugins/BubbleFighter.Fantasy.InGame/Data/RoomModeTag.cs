using System.Globalization;
using System.Text.RegularExpressions;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// Reads the <c>#id#</c> mode tag a player can put in a new room's title (for example <c>Fun room #8#</c>) to force
/// the room's game mode. The tag is removed from the title the room shows; the rest of the title is unchanged.
/// </summary>
internal static partial class RoomModeTag
{
    /// <summary>The smallest mode id a tag or command may name; zero is no mode.</summary>
    internal const int MinModeId = 1;

    /// <summary>The largest mode id: the room carries its mode in an unsigned 16-bit word.</summary>
    internal const int MaxModeId = ushort.MaxValue;

    [GeneratedRegex(@"#(\d+)#", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>The outcome of reading a title.</summary>
    /// <param name="Title">The title to show: without the tag when it named a valid mode, otherwise unchanged.</param>
    /// <param name="ModeId">The mode the tag names, or null when there is no valid tag.</param>
    /// <param name="RejectedTag">The tag text of a tag whose number is not a mode id, or null.</param>
    internal readonly record struct Result(string Title, int? ModeId, string? RejectedTag);

    /// <summary>Parses a room mode id as typed by a player: digits only, within the mode id range.</summary>
    /// <param name="text">The text.</param>
    /// <param name="modeId">The id when valid.</param>
    /// <returns>True when <paramref name="text"/> is a number from 1 to 65535.</returns>
    internal static bool TryParseModeId(string text, out int modeId) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out modeId) && modeId is >= MinModeId and <= MaxModeId;

    /// <summary>Reads the first <c>#number#</c> tag of a title.</summary>
    /// <param name="title">The title the player typed; may be empty.</param>
    /// <returns>The title to show and the mode the tag names, if any.</returns>
    internal static Result Extract(string title)
    {
        var match = Pattern().Match(title);
        if (!match.Success)
            return new(title, null, null);
        if (!TryParseModeId(match.Groups[1].Value, out var modeId))
            return new(title, null, match.Value);
        var before = title[..match.Index].TrimEnd();
        var after = title[(match.Index + match.Length)..].TrimStart();
        var joined = before.Length > 0 && after.Length > 0 ? before + " " + after : before + after;
        return new(joined, modeId, null);
    }
}
