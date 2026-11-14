namespace BubbleFighter.Fantasy.BanPick;

/// <summary>
/// The Nirinsou Link messages of the ban-pick overlay, schema version 1. The server decides everything; the overlay only
/// shows what it is sent and reads lists. Every change the overlay makes is a "bp" command it runs through the
/// launch-bound command channel (<c>game.command.execute</c>), so it is checked exactly like a typed command. A player
/// whose launcher did not negotiate a type gets the same information as chat announcements.
/// </summary>
internal static class BanPickCompanion
{
    internal const int Version = 1;

    /// <summary>
    /// Server to client: the player's room state. Sent on entering a room, when its list or host changes and on leaving
    /// (then <c>in_room</c> is false); it says whether the player is the host, so the overlay knows when to offer the editor,
    /// and <c>playing</c> while a round of the room is under way (sent when a start goes ahead and again when the room waits).
    /// </summary>
    internal const string ListChanged = "game.ban.list.changed";
    /// <summary>Server to the room host: some members wear banned items; a yes runs the payload's command.</summary>
    internal const string StartConfirm = "game.ban.start.confirm";
    /// <summary>Server to client: an item was refused, or someone wears a restricted item.</summary>
    internal const string Warn = "game.ban.warn";

    /// <summary>Client to server request: the room's and the server's current lists.</summary>
    internal const string ListGet = "game.ban.list.get";
    /// <summary>Client to server request: items and groups matching a text, for the host's picker.</summary>
    internal const string CatalogSearch = "game.ban.catalog.search";

    internal static readonly string[] Offered = [ListChanged, StartConfirm, Warn];
    internal static readonly string[] Routed = [ListGet, CatalogSearch];
}
