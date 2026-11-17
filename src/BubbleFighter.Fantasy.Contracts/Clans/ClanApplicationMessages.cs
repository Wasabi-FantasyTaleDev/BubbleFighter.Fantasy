using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// An application to a clan (clan C2S <c>ClanApply</c>, writer kr-1256 sub_527010). The search dialog sends it for a
/// clan whose recruitment word is not 2 (kr-1256 sub_57BA90); it is answered with <see cref="ClanApplyAnswered"/>.
/// </summary>
/// <param name="ClanId">The clan of the selected search row.</param>
/// <param name="ClanName">That row's clan name.</param>
public sealed record ClanApplyRequested(int ClanId, string ClanName) : IMessage;

/// <summary>
/// A request to join a clan that seats applicants at once (clan C2S <c>ClanJoin</c>, writer kr-1256 sub_527130). The
/// search dialog sends it for a clan whose recruitment word is 2 (kr-1256 sub_57BA90); it is answered with
/// <see cref="ClanApplyAnswered"/>.
/// </summary>
/// <param name="ClanId">The clan of the selected search row.</param>
/// <param name="ClanName">That row's clan name.</param>
/// <param name="Nickname">The sender's own nickname.</param>
public sealed record ClanJoinRequested(int ClanId, string ClanName, string Nickname) : IMessage;

/// <summary>
/// The answer to <see cref="ClanApplyRequested"/> and <see cref="ClanJoinRequested"/> (clan S2C <c>ClanApplyResult</c>,
/// reader kr-1256 sub_52E2A0, consumer kr-1256 sub_51F920). The client stores <see cref="Role"/> as its own clan role
/// for every result, and on <see cref="Applied"/> also the clan id and name it applied to.
/// </summary>
/// <param name="Result">One of the result constants; any other value shows the general failure notice.</param>
/// <param name="Role">The sender's clan role after the request, one of <see cref="ClanRole"/>, or 0 without a row.</param>
/// <param name="ClanId">The clan applied to; 0 on a refusal.</param>
/// <param name="ClanName">The clan's name; empty on a refusal.</param>
public sealed record ClanApplyAnswered(int Result, int Role, int ClanId, string ClanName) : IMessage
{
    /// <summary>The application is stored ("join request sent").</summary>
    public const int Applied = 1;

    /// <summary>The player joined the client's training clan; the client also raises it itself (kr-1256 sub_533DE0).</summary>
    public const int JoinedTrainingClan = 2;

    /// <summary>The clan already has as many applicants as it may.</summary>
    public const int TooManyApplicants = -1;

    /// <summary>The player is already in a clan.</summary>
    public const int AlreadyInClan = -2;

    /// <summary>The clan is waiting to be disbanded and takes no applications.</summary>
    public const int Disbanding = -3;

    /// <summary>The clan is not recruiting.</summary>
    public const int NotRecruiting = -5;

    /// <summary>The clan does not seat applicants at once, so a join request is refused.</summary>
    public const int NotOpenRecruiting = -6;

    /// <summary>A value with no notice of its own: the client shows its general failure text.</summary>
    public const int Failed = 0;
}

/// <summary>
/// A request to withdraw the sender's application (clan C2S <c>ClanCancelApplication</c>, writer kr-1256 sub_527250,
/// sent by kr-1256 sub_57BA90 when the selected search row is the clan applied to). Answered with
/// <see cref="ClanCancelApplicationAnswered"/>.
/// </summary>
/// <param name="ClanId">The clan the sender applied to.</param>
public sealed record ClanCancelApplicationRequested(int ClanId) : IMessage;

/// <summary>
/// The answer to <see cref="ClanCancelApplicationRequested"/> (clan S2C <c>ClanCancelApplicationResult</c>, consumer
/// kr-1256 sub_5201C0). On <see cref="Cancelled"/> the client clears its stored clan id and role.
/// </summary>
/// <param name="Result"><see cref="Cancelled"/>, or any other value for the failure path.</param>
/// <param name="Unknown4">A second word kr-1256 sub_5201C0 never reads; the server echoes the clan id.</param>
public sealed record ClanCancelApplicationAnswered(int Result, int Unknown4) : IMessage
{
    /// <summary>The application was withdrawn.</summary>
    public const int Cancelled = 1;

    /// <summary>Any value but <see cref="Cancelled"/>: the client keeps its application.</summary>
    public const int Failed = 0;
}

/// <summary>The applicant list request of the clan's master or deputy (clan C2S <c>ClanApplicantsRequest</c>, writer kr-1256 sub_5279B0, empty body).</summary>
public sealed record ClanApplicantsRequested : IMessage;

/// <summary>One row of <see cref="ClanApplicantsSent"/>.</summary>
/// <param name="UserNum">The applicant's player id.</param>
/// <param name="Nickname">The applicant's nickname.</param>
/// <param name="Role">The row's role word; the client sends it back as the old role when it turns an applicant away.</param>
public sealed record ClanApplicantRow(long UserNum, string Nickname, int Role);

/// <summary>
/// The applicants of the sender's clan (clan S2C <c>ClanApplicants</c>, reader kr-1256 sub_52C750); empty for a
/// sender who is not the clan's master or deputy. While the clan's recruitment word is 0 the client turns every
/// listed applicant away itself.
/// </summary>
/// <param name="Applicants">The applicants, in application order.</param>
public sealed record ClanApplicantsSent(IReadOnlyList<ClanApplicantRow> Applicants) : IMessage;
