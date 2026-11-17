namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The role word of a clan member row, as the client keeps it for its own player (kr-1256 sub_51A3A0 accepts
/// exactly 1, 100, 101 and 102 as "has a clan name to show") and reads it in every roster row.
/// </summary>
public static class ClanRole
{
    /// <summary>An application waiting for an answer: the client stores the clan id as "applied to" (kr-1256 sub_51A3A0).</summary>
    public const int Applicant = 1;

    /// <summary>An ordinary member.</summary>
    public const int Member = 100;

    /// <summary>The clan master: the one role the disband notice arms the countdown for (kr-1256 sub_52E860).</summary>
    public const int Master = 101;

    /// <summary>The deputy master.</summary>
    public const int Deputy = 102;

    /// <summary>Whether the role is a seat in the clan rather than an application.</summary>
    /// <param name="role">The stored role word.</param>
    public static bool IsMember(int role) => role is Member or Master or Deputy;
}

/// <summary>One clan. A clan belongs to no client build: every build's players see the same row.</summary>
/// <param name="ClanId">The clan id, positive.</param>
/// <param name="Name">The clan name, unique.</param>
/// <param name="MasterUserNum">The master's player id.</param>
/// <param name="Introduction">The public introduction shown in the search list.</param>
/// <param name="Notice">The notice shown to members.</param>
/// <param name="Level">The clan level.</param>
/// <param name="Points">The clan's level points.</param>
/// <param name="Mark">The clan mark code; 0 for none.</param>
/// <param name="Capacity">How many members the clan may hold.</param>
/// <param name="Recruitment">The recruitment mode word the master chose.</param>
/// <param name="CreatedAt">When the clan was created (UTC).</param>
/// <param name="DisbandAt">When a requested disband takes effect (UTC); null while none is pending.</param>
/// <param name="MemberCount">How many member rows hold a seat (applications are not counted).</param>
public sealed record ClanRecord(
    long ClanId, string Name, long MasterUserNum, string Introduction, string Notice, int Level, int Points,
    int Mark, int Capacity, int Recruitment, DateTime CreatedAt, DateTime? DisbandAt, int MemberCount);

/// <summary>One member or applicant of a clan. A player has at most one row.</summary>
/// <param name="ClanId">The clan the row belongs to.</param>
/// <param name="UserNum">The player id.</param>
/// <param name="Nickname">The player's nickname when the row was last refreshed.</param>
/// <param name="Level">The player's level when the row was last refreshed.</param>
/// <param name="Role">The role word, one of <see cref="ClanRole"/>.</param>
/// <param name="JoinedAt">When the row was created (UTC).</param>
/// <param name="SeenAt">When the player's clan connection last logged in (UTC).</param>
public sealed record ClanMemberRecord(
    long ClanId, long UserNum, string Nickname, int Level, int Role, DateTime JoinedAt, DateTime SeenAt);

/// <summary>One clan board post.</summary>
/// <param name="PostId">The post id, positive and ascending in time.</param>
/// <param name="ClanId">The clan whose board holds it.</param>
/// <param name="UserNum">The author's player id.</param>
/// <param name="Nickname">The author's nickname when posting.</param>
/// <param name="Level">The author's level when posting.</param>
/// <param name="Body">The post text.</param>
/// <param name="CreatedAt">When it was posted (UTC).</param>
public sealed record ClanPostRecord(
    long PostId, long ClanId, long UserNum, string Nickname, int Level, string Body, DateTime CreatedAt);

/// <summary>One page of a clan search.</summary>
/// <param name="Total">How many clans match in all.</param>
/// <param name="Clans">The requested slice, in the order the search asked for.</param>
public sealed record ClanSearchPage(int Total, IReadOnlyList<ClanRecord> Clans);

/// <summary>The order of a clan search: the client's search list sorts either way (kr-1256 sub_57BA90).</summary>
public enum ClanSearchOrder
{
    /// <summary>By clan id, the oldest clan first.</summary>
    ClanId,

    /// <summary>By points, the most first; clans with equal points by clan id.</summary>
    Points,
}

/// <summary>
/// Stores clans, their member and applicant rows and their board posts. Every operation is atomic on its own;
/// the two rules that must hold across nodes sharing one database are enforced here and not by the caller: a
/// clan name is unique, and a player has at most one member row.
/// </summary>
public interface IClanDirectory
{
    /// <summary>
    /// Creates a clan with its founder as master. Returns null, creating nothing, when the name is taken or the
    /// founder already has a member row.
    /// </summary>
    /// <param name="name">The clan name.</param>
    /// <param name="introduction">The introduction.</param>
    /// <param name="capacity">The member capacity.</param>
    /// <param name="founderUserNum">The founder's player id.</param>
    /// <param name="founderNickname">The founder's nickname.</param>
    /// <param name="founderLevel">The founder's level.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanRecord?> CreateAsync(string name, string introduction, int capacity, long founderUserNum,
        string founderNickname, int founderLevel, CancellationToken ct);

    /// <summary>Returns the clan, or null when the id is not stored.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanRecord?> FindAsync(long clanId, CancellationToken ct);

    /// <summary>Returns the clan with exactly this name, or null.</summary>
    /// <param name="name">The clan name.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanRecord?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Returns the clans whose name contains the text (every clan for an empty text), ignoring case and matching
    /// the text literally, one slice of them in the given order.
    /// </summary>
    /// <param name="text">The text a name must contain; empty matches every clan.</param>
    /// <param name="order">The order of the matches.</param>
    /// <param name="recruitingOnly">
    /// Whether only clans with a recruitment word other than 0 match: the client shows its recruiting label for
    /// every other value (kr-1256 sub_57CFE0).
    /// </param>
    /// <param name="skip">How many matches to skip.</param>
    /// <param name="take">How many matches to return, positive.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanSearchPage> SearchAsync(string text, ClanSearchOrder order, bool recruitingOnly, int skip, int take, CancellationToken ct);

    /// <summary>Writes the clan's changeable columns: everything but its id, name, creation time and member count.</summary>
    /// <param name="clan">The clan with its new values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the clan no longer exists.</returns>
    ValueTask<bool> UpdateAsync(ClanRecord clan, CancellationToken ct);

    /// <summary>Deletes the clan with its member rows and posts.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the clan did not exist.</returns>
    ValueTask<bool> DeleteAsync(long clanId, CancellationToken ct);

    /// <summary>Returns the player's member or applicant row, or null.</summary>
    /// <param name="userNum">The player id.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanMemberRecord?> FindMemberAsync(long userNum, CancellationToken ct);

    /// <summary>Returns every member and applicant row of the clan, ordered by join time and player id.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<ClanMemberRecord>> ListMembersAsync(long clanId, CancellationToken ct);

    /// <summary>
    /// Adds a row for a player who has none. Returns false, adding nothing, when the player already has a row or the
    /// clan is gone. Capacity is not checked here: add the row as <see cref="ClanRole.Applicant"/> and seat it with
    /// <see cref="SeatAsync"/>, which is the only place a seat is counted.
    /// </summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="userNum">The player id.</param>
    /// <param name="nickname">The player's nickname.</param>
    /// <param name="level">The player's level.</param>
    /// <param name="role">The role word.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> AddMemberAsync(long clanId, long userNum, string nickname, int level, int role, CancellationToken ct);

    /// <summary>
    /// Turns the player's application into a seat with the given role, only while the clan holds fewer seated rows
    /// than its capacity. The count and the change are one atomic step, so two approvals racing on different nodes
    /// cannot overfill the clan.
    /// </summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="userNum">The applicant's player id.</param>
    /// <param name="role">The seated role to give, one of <see cref="ClanRole.Member"/> or <see cref="ClanRole.Deputy"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the player has no application in that clan or the clan is full.</returns>
    ValueTask<bool> SeatAsync(long clanId, long userNum, int role, CancellationToken ct);

    /// <summary>
    /// Hands the clan to another seated member in one atomic step: the old master's row becomes
    /// <see cref="ClanRole.Member"/>, the new master's row becomes <see cref="ClanRole.Master"/> and the clan's
    /// master id follows. Nothing changes unless all three can.
    /// </summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="fromUserNum">The current master's player id.</param>
    /// <param name="toUserNum">The seated member who becomes master.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the first player is not the master or the second holds no seat in that clan.</returns>
    ValueTask<bool> TransferMasterAsync(long clanId, long fromUserNum, long toUserNum, CancellationToken ct);

    /// <summary>
    /// Changes the role of the player's row in that clan without counting seats: for moves between seated roles
    /// (member and deputy). Seating an applicant is <see cref="SeatAsync"/>; the master role moves only through
    /// <see cref="TransferMasterAsync"/>.
    /// </summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="userNum">The player id.</param>
    /// <param name="role">The new role word.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the player has no row in that clan.</returns>
    ValueTask<bool> SetRoleAsync(long clanId, long userNum, int role, CancellationToken ct);

    /// <summary>Removes the player's row from that clan.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="userNum">The player id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the player had no row in that clan.</returns>
    ValueTask<bool> RemoveMemberAsync(long clanId, long userNum, CancellationToken ct);

    /// <summary>
    /// Deletes every clan whose disband date is at or before <paramref name="now"/>, with its rows and posts, in one
    /// step: two nodes running this at once delete each clan once, and a cancel that came first keeps its clan.
    /// </summary>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The member and applicant rows the deleted clans held, so their players can be told.</returns>
    ValueTask<IReadOnlyList<ClanMemberRecord>> DeleteDueAsync(DateTime now, CancellationToken ct);

    /// <summary>Refreshes the nickname, level and last-seen time of the player's row, when there is one.</summary>
    /// <param name="userNum">The player id.</param>
    /// <param name="nickname">The player's current nickname.</param>
    /// <param name="level">The player's current level.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RefreshMemberAsync(long userNum, string nickname, int level, CancellationToken ct);

    /// <summary>Adds a board post and returns it.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="userNum">The author's player id.</param>
    /// <param name="nickname">The author's nickname.</param>
    /// <param name="level">The author's level.</param>
    /// <param name="body">The post text.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanPostRecord> AddPostAsync(long clanId, long userNum, string nickname, int level, string body, CancellationToken ct);

    /// <summary>Returns the clan's posts newest first: those with an id below <paramref name="beforePostId"/>, or the newest when it is 0.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="beforePostId">The id the page ends before; 0 for the newest page.</param>
    /// <param name="take">How many posts to return, positive.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAsync(long clanId, long beforePostId, int take, CancellationToken ct);

    /// <summary>Returns the clan's posts with an id above <paramref name="afterPostId"/>, oldest first: the page that follows a newer-page button.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="afterPostId">The id the page starts after; 0 for the oldest posts.</param>
    /// <param name="take">How many posts to return, positive.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAfterAsync(long clanId, long afterPostId, int take, CancellationToken ct);

    /// <summary>Returns one post of the clan's board, or null.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="postId">The post id.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanPostRecord?> FindPostAsync(long clanId, long postId, CancellationToken ct);

    /// <summary>Deletes one post of the clan's board.</summary>
    /// <param name="clanId">The clan id.</param>
    /// <param name="postId">The post id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the board had no such post.</returns>
    ValueTask<bool> DeletePostAsync(long clanId, long postId, CancellationToken ct);
}
