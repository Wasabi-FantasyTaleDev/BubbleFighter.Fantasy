using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// A page of the clan board (clan C2S <c>ClanBoardRequest</c>, writer kr-1256 sub_527360). Answered with
/// <see cref="ClanBoardSent"/>.
/// </summary>
/// <param name="Mode"><see cref="Newest"/>, <see cref="Older"/> or <see cref="Newer"/>.</param>
/// <param name="Cursor">For <see cref="Older"/> the shown page's last post id, for <see cref="Newer"/> its first.</param>
public sealed record ClanBoardRequested(int Mode, int Cursor) : IMessage
{
    /// <summary>The newest page (kr-1256 sub_583F60).</summary>
    public const int Newest = 1;

    /// <summary>The posts older than the cursor (kr-1256 sub_584A80).</summary>
    public const int Older = 2;

    /// <summary>The posts newer than the cursor (kr-1256 sub_584A80).</summary>
    public const int Newer = 3;
}

/// <summary>
/// One board page (clan S2C <c>ClanBoard</c>, reader kr-1256 sub_5285B0), newest first. The client offers the older
/// page while the last post is not <see cref="OldestPostId"/> and the newer page while the first is not
/// <see cref="NewestPostId"/>.
/// </summary>
/// <param name="NewestPostId">The newest post id on the whole board; 0 when it is empty.</param>
/// <param name="OldestPostId">The oldest post id on the whole board; 0 when it is empty.</param>
/// <param name="Posts">The page's posts, newest first.</param>
public sealed record ClanBoardSent(long NewestPostId, long OldestPostId, IReadOnlyList<ClanPostRecord> Posts) : IMessage;

/// <summary>A new board post (clan C2S <c>ClanBoardWrite</c>, writer kr-1256 sub_527430, sender kr-1256 sub_51B600).</summary>
/// <param name="Unknown0">The sender's word at kr-1256 dword_18E56E8 +60, as the member report's first word.</param>
/// <param name="Body">The post, 1 to 60 characters in the client's editor.</param>
public sealed record ClanBoardWriteRequested(int Unknown0, string Body) : IMessage;

/// <summary>The answer to <see cref="ClanBoardWriteRequested"/> (clan S2C <c>ClanBoardWriteResult</c>, reader kr-1256 sub_528BD0).</summary>
/// <param name="Result"><see cref="Posted"/>, one of the two refusals, or another value for no notice.</param>
public sealed record ClanBoardWriteAnswered(int Result) : IMessage
{
    /// <summary>The post was stored; the client asks for the newest page.</summary>
    public const int Posted = 1;

    /// <summary>The post holds a forbidden word. The server has no word filter, so nothing sends it.</summary>
    public const int ForbiddenWord = -1;

    /// <summary>Posting too often in a row. The server sets no such limit, so nothing sends it.</summary>
    public const int TooFrequent = -2;

    /// <summary>A refusal with no notice of its own.</summary>
    public const int Failed = 0;
}

/// <summary>A post deletion (clan C2S <c>ClanBoardDelete</c>, writer kr-1256 sub_5274E0, sender kr-1256 sub_584750).</summary>
/// <param name="UserNum">The sender's own id.</param>
/// <param name="PostId">The post to delete.</param>
public sealed record ClanBoardDeleteRequested(int UserNum, int PostId) : IMessage;

/// <summary>A deletion was handled (clan S2C <c>ClanBoardDeleted</c>, no body): the client asks for the board again.</summary>
public sealed record ClanBoardDeleted : IMessage;
