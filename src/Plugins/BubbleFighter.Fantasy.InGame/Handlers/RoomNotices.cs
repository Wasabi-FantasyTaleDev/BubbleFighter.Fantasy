using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Sends localized server notices to one player or to every member of a room. A deployment without a notice
/// sender or without the localization service sends nothing, as the chat commands always did.
/// </summary>
/// <param name="notices">The build's notice encoder, or null.</param>
/// <param name="texts">The localization service, or null.</param>
internal sealed class RoomNotices(IMessageSender<ServerNotice>? notices, IGameText? texts)
{
    /// <summary>Sends one message key, formatted in the player's region language, to that player.</summary>
    /// <param name="session">The receiving player.</param>
    /// <param name="key">The game message key.</param>
    /// <param name="arguments">Named placeholder values, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async ValueTask SendAsync(ISession session, string key, IReadOnlyDictionary<string, string>? arguments, CancellationToken ct)
    {
        if (notices is null || texts is null)
            return;
        await notices.SendAsync(session, new ServerNotice(texts.Format(session.Profile, key, arguments)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells every member of the room, the master included, that the room's mode is now <paramref name="modeId"/>,
    /// naming the mode in each member's language (English when the language has no name, "mode N" when none is known).
    /// </summary>
    /// <param name="room">The room.</param>
    /// <param name="modeId">The mode id the room now has.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async ValueTask AnnounceModeAsync(PlayRoom room, int modeId, CancellationToken ct)
    {
        if (notices is null || texts is null)
            return;
        RoomSeat[] members;
        lock (room.Gate)
            members = [.. room.Seats];
        var id = modeId.ToString(CultureInfo.InvariantCulture);
        foreach (var member in members)
        {
            var profile = member.Game.Profile;
            if (!texts.TryFormat(profile, "mode." + id, out var name))
                name = texts.Format(profile, "mode.unnamed", new Dictionary<string, string> { ["id"] = id });
            await notices.SendAsync(member.Game,
                new ServerNotice(texts.Format(profile, "room.mode.changed", new Dictionary<string, string> { ["mode"] = name })), ct)
                .ConfigureAwait(false);
        }
    }
}
