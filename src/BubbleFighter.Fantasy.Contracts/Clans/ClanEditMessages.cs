using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The recruitment words of a clan. The clan management dialog (kr-1256 sub_5BAC70) sends exactly these three
/// from its buttons; the search dialog sends a join request instead of an application for <see cref="Open"/>.
/// </summary>
public static class ClanRecruitment
{
    /// <summary>The clan takes no applications.</summary>
    public const int Closed = 0;

    /// <summary>The clan takes applications for its master or deputy to answer.</summary>
    public const int ByApplication = 1;

    /// <summary>The clan seats a player who asks at once.</summary>
    public const int Open = 2;
}

/// <summary>The master's new introduction (clan C2S <c>ClanIntroductionEdit</c>, writer kr-1256 sub_527D30, sender kr-1256 sub_5BAC70).</summary>
/// <param name="Introduction">The new introduction.</param>
public sealed record ClanIntroductionEditRequested(string Introduction) : IMessage;

/// <summary>
/// The answer to <see cref="ClanIntroductionEditRequested"/> (clan S2C <c>ClanIntroductionEditResult</c>, reader kr-1256
/// sub_52DC60): on <see cref="Changed"/> the client stores the text as its clan's introduction (clan state +200).
/// </summary>
/// <param name="Result"><see cref="Changed"/>; any other value shows the forbidden-word notice.</param>
/// <param name="Introduction">The stored introduction.</param>
public sealed record ClanIntroductionEditAnswered(int Result, string Introduction) : IMessage
{
    /// <summary>The introduction was stored.</summary>
    public const int Changed = 1;
}

/// <summary>
/// The clan's introduction changed, told to its other members (clan S2C <c>ClanIntroductionChanged</c>, reader kr-1256
/// sub_52E000, which shows "the introduction was changed to '%s'" and stores it at clan state +200).
/// </summary>
/// <param name="Unknown0">A leading word the reader skips; the server writes 0.</param>
/// <param name="Introduction">The new introduction.</param>
public sealed record ClanIntroductionChanged(int Unknown0, string Introduction) : IMessage;

/// <summary>The master's new notice (clan C2S <c>ClanNoticeEdit</c>, writer kr-1256 sub_527CA0, sender kr-1256 sub_5B82C0).</summary>
/// <param name="Notice">The new notice.</param>
public sealed record ClanNoticeEditRequested(string Notice) : IMessage;

/// <summary>
/// The answer to <see cref="ClanNoticeEditRequested"/> (clan S2C <c>ClanNoticeEditResult</c>, reader kr-1256 sub_52D7A0).
/// The reader changes nothing itself: the notice arrives in <see cref="ClanNoticeChanged"/>.
/// </summary>
/// <param name="Result"><see cref="Changed"/>, or <see cref="ForbiddenWord"/> for the forbidden-word notice.</param>
public sealed record ClanNoticeEditAnswered(int Result) : IMessage
{
    /// <summary>The notice was stored.</summary>
    public const int Changed = 1;

    /// <summary>The text holds a forbidden word. The server has no word filter, so nothing sends it.</summary>
    public const int ForbiddenWord = -1;
}

/// <summary>
/// The clan's notice changed, told to every member including the editor (clan S2C <c>ClanNoticeChanged</c>, reader
/// kr-1256 sub_52DA40, which shows "the notice was changed to '%s'" and stores it at clan state +176 through
/// kr-1256 sub_51D1C0).
/// </summary>
/// <param name="Notice">The new notice.</param>
public sealed record ClanNoticeChanged(string Notice) : IMessage;

/// <summary>The master's recruitment choice (clan C2S <c>ClanRecruitmentEdit</c>, writer kr-1256 sub_528460, sender kr-1256 sub_51C750).</summary>
/// <param name="ClanId">The sender's clan id (clan state +44).</param>
/// <param name="Recruitment">One of <see cref="ClanRecruitment"/>.</param>
public sealed record ClanRecruitmentEditRequested(int ClanId, int Recruitment) : IMessage;

/// <summary>
/// The answer to <see cref="ClanRecruitmentEditRequested"/> (clan S2C <c>ClanRecruitmentEditResult</c>, kr-1256
/// sub_525170 case 0x44F). When <see cref="Result"/> is not 0 the client stores the recruitment word (clan state
/// +160); for <see cref="ClanRecruitment.Closed"/> it then asks for the applicant list and turns every applicant away.
/// </summary>
/// <param name="Unknown0">A leading word the case skips; the server echoes the clan id.</param>
/// <param name="Result">1 when the word was stored, 0 when it was not.</param>
/// <param name="Recruitment">The clan's recruitment word after the request.</param>
public sealed record ClanRecruitmentEditAnswered(int Unknown0, int Result, int Recruitment) : IMessage;

/// <summary>The master's new clan mark (clan C2S <c>ClanMarkEdit</c>, writer kr-1256 sub_530C50, sender kr-1256 sub_9CA450).</summary>
/// <param name="ClanId">The sender's clan id (clan state +44); the writer sends nothing for 0 or 99.</param>
/// <param name="Mark">The chosen mark code.</param>
public sealed record ClanMarkEditRequested(int ClanId, int Mark) : IMessage;

/// <summary>
/// The clan's mark changed (clan S2C <c>ClanMarkChanged</c>, reader kr-1256 sub_5335F0, which stores it at clan state
/// +224 and shows "the clan mark was updated").
/// </summary>
/// <param name="Mark">The new mark code.</param>
public sealed record ClanMarkChanged(int Mark) : IMessage;
