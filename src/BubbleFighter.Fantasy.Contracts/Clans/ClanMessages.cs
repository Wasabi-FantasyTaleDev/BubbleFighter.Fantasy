using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The clan server asked this client to fetch its clan passport (C2S <c>gcpReqClanPassport</c>, empty body):
/// the client sends it when the clan connection's hello arrives (kr-1256 sub_525170 case 0x3EA; the typed
/// legacy sender is kr-812 sub_498E70) and writes no field. It is answered with <see cref="ClanPassportIssued"/>.
/// </summary>
public sealed record ClanServerAckReceived : IMessage;

/// <summary>
/// The main clan window opened, or the session started with a clan (C2S 2040, empty body): the client sends it
/// from the window click (kr-1256 sub_5A9410) and from the session init (kr-1256 sub_516EE0) and shows the
/// passport, mark and guild panes it is answered with.
/// </summary>
public sealed record ClanMainOpened : IMessage;

/// <summary>
/// The attendance card check-in (C2S 2041, empty body): the client sends it only while the attendance card
/// word at +1468 is 1 (kr-1256 sub_57F010) and shows the attendance pane it is answered with.
/// </summary>
public sealed record ClanAttendanceCheckedIn : IMessage;

/// <summary>
/// The two empty open snapshots the main clan window consumes, in the client's order: the mark list (S2C 140,
/// parser kr-1256 sub_99CFA0) and the guild info (S2C 918, handler kr-1256 sub_9B2690). The bodies are empty
/// today: no mark row or guild id is proven. The passport is not part of the open: the client forwards every
/// passport as a clan login (kr-1256 sub_9D2FF0 case 21), so it is sent only as <see cref="ClanPassportIssued"/>.
/// </summary>
public sealed record ClanOpened : IMessage;

/// <summary>
/// The attendance state the clan attendance pane shows. The server keeps no cards, so the game-port body is
/// the empty counted list (S2C 1508, list reader kr-1256 sub_10C5590).
/// </summary>
public sealed record ClanAttendanceStateUpdated : IMessage;

/// <summary>
/// The clan passport (S2C <c>gspClanPassport</c>) answered to <see cref="ClanServerAckReceived"/>. The client
/// keeps none of it and forwards every field as its clan-connection login (kr-1256 sub_9D2FF0 case 21 into
/// kr-1256 sub_525DF0; kr-812 sub_819690 into kr-812 sub_498C30), so the server states who the game session
/// is and adds a ticket only it can check.
/// </summary>
/// <param name="UserNumber">The game session's account id.</param>
/// <param name="Nickname">The game session's display nickname.</param>
/// <param name="Ticket">The single-use login ticket; empty when clan authentication is off.</param>
public sealed record ClanPassportIssued(int UserNumber, string Nickname, ReadOnlyMemory<byte> Ticket) : IMessage;

/// <summary>
/// The clan connection's heartbeat (clan C2S <c>ClanHeartbeat</c>, empty body), sent every 3000 ms while the
/// socket is up (kr-1256 sub_525050).
/// </summary>
public sealed record ClanServerHeartbeatReceived : IMessage;

/// <summary>
/// The clan connection's login (clan C2S <c>ClanLogin</c>): the passport echoed back by kr-1256 sub_525DF0.
/// </summary>
/// <param name="UserNumber">The account id the passport stated.</param>
/// <param name="Nickname">The nickname the passport stated.</param>
/// <param name="Ticket">The ticket the passport carried.</param>
public sealed record ClanServerLoginRequested(int UserNumber, string Nickname, ReadOnlyMemory<byte> Ticket) : IMessage;

/// <summary>
/// The empty clan S2C <c>ClanHello</c>: it makes the client ask its game connection for the passport
/// (kr-1256 sub_525170 case 0x3EA).
/// </summary>
public sealed record ClanServerHello : IMessage;

/// <summary>What the client does with a clan login answer (kr-1256 sub_525170 case 0x3EE).</summary>
public enum ClanServerLoginResult
{
    /// <summary>The client marks the connection logged in and requests the clan home.</summary>
    Accepted = 1,

    /// <summary>The client shows "clan server authentication was not completed" (kr-1256 sub_51AC70).</summary>
    NotAuthenticated = -1,

    /// <summary>The client shows "clan server login failed" (kr-1256 sub_51AC70).</summary>
    Failed = -2,
}

/// <summary>The clan S2C <c>ClanLoginResult</c> answer to <see cref="ClanServerLoginRequested"/>.</summary>
/// <param name="Result">The one word the client reads.</param>
public sealed record ClanServerLoginAnswered(ClanServerLoginResult Result) : IMessage;
