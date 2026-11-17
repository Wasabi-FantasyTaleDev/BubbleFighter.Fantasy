using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The clan search list. The client's dialog asks for one page at a time in one of five modes and shows what it
/// is answered; any logged-in player may search, with or without a clan.
/// </summary>
internal sealed class ClanSearchHandlers(IClanDirectory clans, IMessageSender<ClanSearchSent> results, ILogger logger)
{
    /// <summary>
    /// Rows per page: kr-1256 sub_57CFE0 fills five rows and disables the next button when fewer arrive, and
    /// kr-1256 sub_57B910 clears five.
    /// </summary>
    internal const int PageSize = 5;

    /// <summary>Answers the page the dialog asked for.</summary>
    public async ValueTask SearchAsync(ISession session, ClanSearchRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        (string Text, ClanSearchOrder Order, bool RecruitingOnly)? query = request.Mode switch
        {
            ClanSearchMode.Name => (request.Text, ClanSearchOrder.ClanId, false),
            ClanSearchMode.Points => ("", ClanSearchOrder.Points, false),
            ClanSearchMode.All => ("", ClanSearchOrder.ClanId, false),
            ClanSearchMode.AllRecruiting => ("", ClanSearchOrder.ClanId, true),
            ClanSearchMode.PointsRecruiting => ("", ClanSearchOrder.Points, true),
            _ => null,
        };
        if (query is null)
        {
            // The dialog only stores the five modes; another word is answered as the refresh list.
            logger.LogDebug("Clan connection {Id} user={User} searched in mode {Mode}; answering the full list.", session.Id, caller.UserNum, request.Mode);
            query = ("", ClanSearchOrder.ClanId, false);
        }
        var (text, order, recruitingOnly) = query.Value;
        // The dialog counts pages from 1 and disables its previous button there; a lower page is answered as page 1.
        var page = Math.Max(request.Page, 1);
        var skip = (long)(page - 1) * PageSize;
        var rows = new List<ClanSearchRow>(PageSize);
        if (skip <= int.MaxValue)
        {
            var found = await clans.SearchAsync(text, order, recruitingOnly, (int)skip, PageSize, ct).ConfigureAwait(false);
            foreach (var clan in found.Clans)
            {
                var members = await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false);
                var seated = members.Where(row => ClanRole.IsMember(row.Role)).ToArray();
                var master = seated.FirstOrDefault(row => row.UserNum == clan.MasterUserNum)?.Nickname ?? "";
                var lastLogin = seated.Length == 0 ? clan.CreatedAt : seated.Max(row => row.SeenAt);
                rows.Add(new ClanSearchRow(clan, master, lastLogin));
            }
        }
        await results.SendAsync(session, new ClanSearchSent(request.Mode, page, request.Text, rows), ct).ConfigureAwait(false);
    }
}
