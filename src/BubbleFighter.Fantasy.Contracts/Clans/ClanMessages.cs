using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The clan server asked this client to fetch its clan passport (C2S 25, empty body): the client sends it from
/// the clan-server bounce (kr-1256 sub_525170 case 0x3EA; the typed legacy sender is kr-812 sub_498E70) and
/// writes no field on the game port. The client expects no answer on this connection.
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
/// The three empty open snapshots the main clan window consumes, in the client's order: the passport (S2C 21,
/// reader kr-1256 sub_99C1D0), the mark list (S2C 140, parser kr-1256 sub_99CFA0) and the guild info (S2C 918,
/// handler kr-1256 sub_9B2690). The game-port bodies are empty today: no owned clan identity, mark row or
/// guild id is proven.
/// </summary>
public sealed record ClanOpened : IMessage;

/// <summary>
/// The attendance state the clan attendance pane shows. The server keeps no cards, so the game-port body is
/// the empty counted list (S2C 1508, list reader kr-1256 sub_10C5590).
/// </summary>
public sealed record ClanAttendanceStateUpdated : IMessage;
