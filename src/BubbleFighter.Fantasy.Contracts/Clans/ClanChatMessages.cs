using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// A clan chat line (clan C2S <c>ClanChat</c>, writer kr-1256 sub_5282A0, sent by kr-1256 sub_518F70 for a seated
/// member). Told to the clan as <see cref="ClanChatReceived"/>.
/// </summary>
/// <param name="Unknown0">The writer's first word, always 2.</param>
/// <param name="Nickname">The name the client put in; the server uses the sender's own name instead.</param>
/// <param name="Text">The line.</param>
public sealed record ClanChatRequested(int Unknown0, string Nickname, string Text) : IMessage;

/// <summary>A clan chat line, told to every seated member (clan S2C <c>ClanChatMessage</c>, reader kr-1256 sub_52EFC0).</summary>
/// <param name="Nickname">The sender's name.</param>
/// <param name="Text">The line.</param>
public sealed record ClanChatReceived(string Nickname, string Text) : IMessage;

/// <summary>
/// A whisper to one clan member (clan C2S <c>ClanWhisper</c>, writer kr-1256 sub_526160): kind 1 is typed, 3 and 4 are
/// the client's automatic answers (kr-1256 sub_52A8A0). Answered with <see cref="ClanWhisperAnswered"/>; the member
/// is sent <see cref="ClanWhisperReceived"/>.
/// </summary>
/// <param name="Kind">The whisper kind.</param>
/// <param name="Target">The member's name.</param>
/// <param name="Text">The whisper.</param>
/// <param name="Unknown3">A byte from the sender's player object (+78 +392) under a channel condition; passed through.</param>
public sealed record ClanWhisperRequested(int Kind, string Target, string Text, int Unknown3) : IMessage;

/// <summary>
/// The answer to <see cref="ClanWhisperRequested"/> (clan S2C <c>ClanWhisperResult</c>, reader kr-1256 sub_529D30). The
/// three strings are written in the order of <see cref="ClanWhisperReceived"/> (inferred, see packets/clan-server.yaml).
/// </summary>
/// <param name="Result"><see cref="Delivered"/>, <see cref="NotFound"/>, or another notice code.</param>
/// <param name="Kind">The whisper kind.</param>
/// <param name="From">The sender's name.</param>
/// <param name="To">The member's name.</param>
/// <param name="Text">The whisper.</param>
public sealed record ClanWhisperAnswered(int Result, int Kind, string From, string To, string Text) : IMessage
{
    /// <summary>The whisper reached the member; the client shows it in its own chat.</summary>
    public const int Delivered = 1;

    /// <summary>"The player cannot be found": not in the clan or not connected here.</summary>
    public const int NotFound = 2;

    /// <summary>A forbidden word. The server has no word filter, so nothing sends it.</summary>
    public const int ForbiddenWord = 3;

    /// <summary>"Try again later".</summary>
    public const int Later = 4;
}

/// <summary>A whisper from a clan member (clan S2C <c>ClanWhisperMessage</c>, reader kr-1256 sub_52A8A0).</summary>
/// <param name="Kind">The whisper kind.</param>
/// <param name="From">The sender's name; the client's automatic answers go to it.</param>
/// <param name="To">The receiver's name.</param>
/// <param name="Text">The whisper.</param>
/// <param name="Unknown4">The sender's flag byte, passed through.</param>
public sealed record ClanWhisperReceived(int Kind, string From, string To, string Text, int Unknown4) : IMessage;
