using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The clan board of seated members: pages by post id in the client's three modes, posts, and deletion by the
/// author, the master or the deputy. After a post or a deletion the client asks for the board itself
/// (kr-1256 sub_51B560), so the answers carry no page.
/// </summary>
internal sealed class ClanBoardHandlers(
    IClanDirectory clans,
    IMessageSender<ClanBoardSent> pages,
    IMessageSender<ClanBoardWriteAnswered> written,
    IMessageSender<ClanBoardDeleted> deleted,
    int pageSize,
    ILogger logger)
{
    /// <summary>The longest post the client's editor sends (kr-1256 sub_51B600).</summary>
    internal const int MaxPostLength = 60;

    /// <summary>Answers one page, newest first; a player without a seat gets an empty board.</summary>
    public async ValueTask ListAsync(ISession session, ClanBoardRequested request, CancellationToken ct)
    {
        var clanId = await SeatedClanAsync(ClanCaller.Require(session), ct).ConfigureAwait(false);
        if (clanId is not { } id)
        {
            await pages.SendAsync(session, new ClanBoardSent(0, 0, []), ct).ConfigureAwait(false);
            return;
        }
        var cursor = Math.Max(request.Cursor, 0);
        IReadOnlyList<ClanPostRecord> posts = request.Mode switch
        {
            ClanBoardRequested.Older when cursor > 0 => await clans.ListPostsAsync(id, cursor, pageSize, ct).ConfigureAwait(false),
            // The newer page is read oldest first from the cursor and shown newest first like every page.
            ClanBoardRequested.Newer when cursor > 0 => (await clans.ListPostsAfterAsync(id, cursor, pageSize, ct).ConfigureAwait(false)).Reverse().ToArray(),
            _ => await clans.ListPostsAsync(id, 0, pageSize, ct).ConfigureAwait(false),
        };
        var newest = (await clans.ListPostsAsync(id, 0, 1, ct).ConfigureAwait(false)).FirstOrDefault()?.PostId ?? 0;
        var oldest = (await clans.ListPostsAfterAsync(id, 0, 1, ct).ConfigureAwait(false)).FirstOrDefault()?.PostId ?? 0;
        await pages.SendAsync(session, new ClanBoardSent(newest, oldest, posts), ct).ConfigureAwait(false);
    }

    /// <summary>Stores a post of a seated member.</summary>
    public async ValueTask WriteAsync(ISession session, ClanBoardWriteRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var clanId = await SeatedClanAsync(caller, ct).ConfigureAwait(false);
        if (clanId is not { } id || request.Body.Length is 0 or > MaxPostLength)
        {
            logger.LogInformation("Clan connection {Id} user={User} could not post {Length} characters.", session.Id, caller.UserNum, request.Body.Length);
            await written.SendAsync(session, new ClanBoardWriteAnswered(ClanBoardWriteAnswered.Failed), ct).ConfigureAwait(false);
            return;
        }
        await clans.AddPostAsync(id, caller.UserNum, caller.Nickname, caller.Level, request.Body, ct).ConfigureAwait(false);
        await written.SendAsync(session, new ClanBoardWriteAnswered(ClanBoardWriteAnswered.Posted), ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a post of the sender's clan when the sender wrote it or is its master or deputy; answered either way.</summary>
    public async ValueTask DeleteAsync(ISession session, ClanBoardDeleteRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var row = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var post = row is not null && ClanRole.IsMember(row.Role) ? await clans.FindPostAsync(row.ClanId, request.PostId, ct).ConfigureAwait(false) : null;
        var allowed = post is not null && (post.UserNum == caller.UserNum || row!.Role is ClanRole.Master or ClanRole.Deputy);
        var removed = allowed && await clans.DeletePostAsync(row!.ClanId, post!.PostId, ct).ConfigureAwait(false);
        logger.LogInformation("Clan connection {Id} user={User} deleted post {Post}: {Result}.", session.Id, caller.UserNum, request.PostId,
            removed ? "deleted" : "refused");
        await deleted.SendAsync(session, new ClanBoardDeleted(), ct).ConfigureAwait(false);
    }

    private async ValueTask<long?> SeatedClanAsync(ClanCaller caller, CancellationToken ct) =>
        await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false) is { } row && ClanRole.IsMember(row.Role) ? row.ClanId : null;
}
