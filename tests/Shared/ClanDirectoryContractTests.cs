using BubbleFighter.Fantasy.Contracts.Clans;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

/// <summary>
/// What every <see cref="IClanDirectory"/> backend must do. A backend's test project links this file and derives
/// one class that supplies an empty directory.
/// </summary>
public abstract class ClanDirectoryContractTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Returns a directory that stores no clan.</summary>
    protected abstract ValueTask<IClanDirectory> CreateEmptyAsync();

    [Fact]
    public async Task Create_seats_the_founder_as_master_and_counts_one_member()
    {
        var clans = await CreateEmptyAsync();
        var clan = await clans.CreateAsync("Alpha", "hello", 30, 7, "Seven", 12, Ct);
        Assert.NotNull(clan);
        // The client keeps clan id 99 for its training clan (kr-1256 sub_51A3A0), so stored ids start above it.
        Assert.True(clan.ClanId >= 1000);
        Assert.Equal(("Alpha", 7L, "hello", "", 1, 30, 1), (clan.Name, clan.MasterUserNum, clan.Introduction, clan.Notice, clan.Level, clan.Capacity, clan.MemberCount));
        Assert.Null(clan.DisbandAt);
        var founder = await clans.FindMemberAsync(7, Ct);
        Assert.Equal((clan.ClanId, "Seven", 12, ClanRole.Master), (founder!.ClanId, founder.Nickname, founder.Level, founder.Role));
    }

    [Fact]
    public async Task Create_refuses_a_taken_name_and_a_founder_who_has_a_row_and_stores_nothing()
    {
        var clans = await CreateEmptyAsync();
        var first = await clans.CreateAsync("Alpha", "", 30, 7, "Seven", 1, Ct);
        Assert.Null(await clans.CreateAsync("ALPHA", "", 30, 8, "Eight", 1, Ct));
        Assert.Null(await clans.FindMemberAsync(8, Ct));
        Assert.Null(await clans.CreateAsync("Beta", "", 30, 7, "Seven", 1, Ct));
        Assert.Null(await clans.FindByNameAsync("Beta", Ct));
        Assert.Equal(first!.ClanId, (await clans.FindByNameAsync("alpha", Ct))!.ClanId);
    }

    [Fact]
    public async Task A_player_has_at_most_one_row()
    {
        var clans = await CreateEmptyAsync();
        var a = await clans.CreateAsync("Alpha", "", 30, 1, "One", 1, Ct);
        var b = await clans.CreateAsync("Beta", "", 30, 2, "Two", 1, Ct);
        Assert.True(await clans.AddMemberAsync(a!.ClanId, 3, "Three", 5, ClanRole.Applicant, Ct));
        Assert.False(await clans.AddMemberAsync(b!.ClanId, 3, "Three", 5, ClanRole.Applicant, Ct));
        Assert.False(await clans.AddMemberAsync(a.ClanId + 500, 4, "Four", 5, ClanRole.Applicant, Ct));
        Assert.Equal(a.ClanId, (await clans.FindMemberAsync(3, Ct))!.ClanId);
    }

    [Fact]
    public async Task Applicants_are_not_counted_and_seating_stops_at_capacity()
    {
        var clans = await CreateEmptyAsync();
        var clan = await clans.CreateAsync("Alpha", "", 2, 1, "One", 1, Ct);
        Assert.True(await clans.AddMemberAsync(clan!.ClanId, 2, "Two", 1, ClanRole.Applicant, Ct));
        Assert.True(await clans.AddMemberAsync(clan.ClanId, 3, "Three", 1, ClanRole.Applicant, Ct));
        Assert.Equal(1, (await clans.FindAsync(clan.ClanId, Ct))!.MemberCount);
        Assert.True(await clans.SeatAsync(clan.ClanId, 2, ClanRole.Member, Ct));
        Assert.False(await clans.SeatAsync(clan.ClanId, 3, ClanRole.Member, Ct));
        Assert.Equal(ClanRole.Applicant, (await clans.FindMemberAsync(3, Ct))!.Role);
        // A seated row is not an application: seating it again changes nothing.
        Assert.False(await clans.SeatAsync(clan.ClanId, 2, ClanRole.Deputy, Ct));
        Assert.Equal(2, (await clans.FindAsync(clan.ClanId, Ct))!.MemberCount);
    }

    [Fact]
    public async Task Transfer_moves_the_master_role_and_the_clan_master_together_or_not_at_all()
    {
        var clans = await CreateEmptyAsync();
        var clan = await clans.CreateAsync("Alpha", "", 30, 1, "One", 1, Ct);
        await clans.AddMemberAsync(clan!.ClanId, 2, "Two", 1, ClanRole.Applicant, Ct);
        // An applicant holds no seat and cannot take the clan.
        Assert.False(await clans.TransferMasterAsync(clan.ClanId, 1, 2, Ct));
        Assert.Equal(ClanRole.Master, (await clans.FindMemberAsync(1, Ct))!.Role);
        Assert.True(await clans.SeatAsync(clan.ClanId, 2, ClanRole.Member, Ct));
        Assert.False(await clans.TransferMasterAsync(clan.ClanId, 2, 1, Ct));
        Assert.False(await clans.TransferMasterAsync(clan.ClanId, 1, 1, Ct));
        Assert.True(await clans.TransferMasterAsync(clan.ClanId, 1, 2, Ct));
        Assert.Equal(ClanRole.Member, (await clans.FindMemberAsync(1, Ct))!.Role);
        Assert.Equal(ClanRole.Master, (await clans.FindMemberAsync(2, Ct))!.Role);
        Assert.Equal(2, (await clans.FindAsync(clan.ClanId, Ct))!.MasterUserNum);
    }

    [Fact]
    public async Task Update_writes_the_changeable_columns_and_keeps_the_name()
    {
        var clans = await CreateEmptyAsync();
        var clan = await clans.CreateAsync("Alpha", "", 30, 1, "One", 1, Ct);
        var disband = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.True(await clans.UpdateAsync(clan! with { Name = "Other", Introduction = "intro", Notice = "notice", Level = 3, Points = 40, Mark = 5, Capacity = 50, Recruitment = 2, DisbandAt = disband }, Ct));
        var stored = await clans.FindAsync(clan.ClanId, Ct);
        Assert.Equal(("Alpha", "intro", "notice", 3, 40, 5, 50, 2, disband),
            (stored!.Name, stored.Introduction, stored.Notice, stored.Level, stored.Points, stored.Mark, stored.Capacity, stored.Recruitment, stored.DisbandAt!.Value));
        // Writing the same values again still reports the clan as present.
        Assert.True(await clans.UpdateAsync(stored, Ct));
        Assert.False(await clans.UpdateAsync(stored with { ClanId = stored.ClanId + 500 }, Ct));
    }

    [Fact]
    public async Task Delete_removes_the_clan_with_its_rows_and_posts_and_frees_its_players()
    {
        var clans = await CreateEmptyAsync();
        var clan = await clans.CreateAsync("Alpha", "", 30, 1, "One", 1, Ct);
        await clans.AddMemberAsync(clan!.ClanId, 2, "Two", 1, ClanRole.Applicant, Ct);
        await clans.AddPostAsync(clan.ClanId, 1, "One", 1, "first", Ct);
        Assert.True(await clans.DeleteAsync(clan.ClanId, Ct));
        Assert.False(await clans.DeleteAsync(clan.ClanId, Ct));
        Assert.Null(await clans.FindMemberAsync(1, Ct));
        Assert.Null(await clans.FindMemberAsync(2, Ct));
        Assert.Empty(await clans.ListPostsAsync(clan.ClanId, 0, 10, Ct));
        Assert.NotNull(await clans.CreateAsync("Alpha", "", 30, 1, "One", 1, Ct));
    }

    [Fact]
    public async Task Due_disbands_delete_only_clans_whose_date_has_passed_and_return_their_rows()
    {
        var clans = await CreateEmptyAsync();
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var due = (await clans.CreateAsync("Due", "", 30, 7, "Seven", 1, Ct))!;
        var later = (await clans.CreateAsync("Later", "", 30, 8, "Eight", 1, Ct))!;
        var kept = (await clans.CreateAsync("Kept", "", 30, 9, "Nine", 1, Ct))!;
        Assert.True(await clans.AddMemberAsync(due.ClanId, 10, "Ten", 1, ClanRole.Applicant, Ct));
        await clans.AddPostAsync(due.ClanId, 7, "Seven", 1, "bye", Ct);
        Assert.True(await clans.UpdateAsync(due with { DisbandAt = now.AddMinutes(-1) }, Ct));
        Assert.True(await clans.UpdateAsync(later with { DisbandAt = now.AddMinutes(1) }, Ct));

        var rows = await clans.DeleteDueAsync(now, Ct);
        Assert.Equal([(due.ClanId, 7L), (due.ClanId, 10L)], rows.Select(row => (row.ClanId, row.UserNum)).OrderBy(pair => pair.UserNum));
        Assert.Null(await clans.FindAsync(due.ClanId, Ct));
        Assert.Null(await clans.FindMemberAsync(7, Ct));
        Assert.Null(await clans.FindMemberAsync(10, Ct));
        Assert.Empty(await clans.ListPostsAsync(due.ClanId, long.MaxValue, 10, Ct));
        Assert.NotNull(await clans.FindAsync(later.ClanId, Ct));
        Assert.NotNull(await clans.FindAsync(kept.ClanId, Ct));
        Assert.Empty(await clans.DeleteDueAsync(now, Ct));
        Assert.Equal(8L, Assert.Single(await clans.DeleteDueAsync(now.AddMinutes(1), Ct)).UserNum);
    }

    [Fact]
    public async Task Members_leave_only_the_clan_they_are_in_and_role_changes_stay_inside_it()
    {
        var clans = await CreateEmptyAsync();
        var a = await clans.CreateAsync("Alpha", "", 30, 1, "One", 1, Ct);
        var b = await clans.CreateAsync("Beta", "", 30, 2, "Two", 1, Ct);
        await clans.AddMemberAsync(a!.ClanId, 3, "Three", 1, ClanRole.Applicant, Ct);
        await clans.SeatAsync(a.ClanId, 3, ClanRole.Member, Ct);
        Assert.False(await clans.SetRoleAsync(b!.ClanId, 3, ClanRole.Deputy, Ct));
        Assert.True(await clans.SetRoleAsync(a.ClanId, 3, ClanRole.Deputy, Ct));
        Assert.False(await clans.RemoveMemberAsync(b.ClanId, 3, Ct));
        Assert.True(await clans.RemoveMemberAsync(a.ClanId, 3, Ct));
        Assert.False(await clans.RemoveMemberAsync(a.ClanId, 3, Ct));
        await clans.RefreshMemberAsync(1, "Renamed", 44, Ct);
        var members = await clans.ListMembersAsync(a.ClanId, Ct);
        Assert.Equal(("Renamed", 44), (Assert.Single(members).Nickname, members[0].Level));
    }

    [Fact]
    public async Task Search_matches_a_part_of_the_name_in_id_order_and_pages()
    {
        var clans = await CreateEmptyAsync();
        for (var i = 1; i <= 5; i++)
            await clans.CreateAsync($"Wolf{i}", "", 30, i, $"P{i}", 1, Ct);
        await clans.CreateAsync("100%_Cats", "", 30, 9, "P9", 1, Ct);
        var all = await clans.SearchAsync("", ClanSearchOrder.ClanId, false, 0, 10, Ct);
        Assert.Equal((6, 6), (all.Total, all.Clans.Count));
        var page = await clans.SearchAsync("wolf", ClanSearchOrder.ClanId, false, 2, 2, Ct);
        Assert.Equal(5, page.Total);
        Assert.Equal(["Wolf3", "Wolf4"], page.Clans.Select(clan => clan.Name));
        // The text is matched as written: a pattern character in it matches only itself.
        Assert.Equal(1, (await clans.SearchAsync("%_", ClanSearchOrder.ClanId, false, 0, 10, Ct)).Total);
        Assert.Equal(0, (await clans.SearchAsync("_o", ClanSearchOrder.ClanId, false, 0, 10, Ct)).Total);
    }

    [Fact]
    public async Task Search_orders_by_points_and_can_keep_only_recruiting_clans()
    {
        var clans = await CreateEmptyAsync();
        var points = new[] { 5, 20, 5, 0 };
        for (var i = 0; i < points.Length; i++)
        {
            var clan = (await clans.CreateAsync($"Clan{i}", "", 30, i + 1, $"P{i}", 1, Ct))!;
            // The third clan stops recruiting: a recruitment word of 0.
            Assert.True(await clans.UpdateAsync(clan with { Points = points[i], Recruitment = i == 2 ? 0 : 1 }, Ct));
        }
        var byPoints = await clans.SearchAsync("", ClanSearchOrder.Points, false, 0, 10, Ct);
        Assert.Equal(["Clan1", "Clan0", "Clan2", "Clan3"], byPoints.Clans.Select(clan => clan.Name));
        var recruiting = await clans.SearchAsync("", ClanSearchOrder.ClanId, true, 0, 10, Ct);
        Assert.Equal(["Clan0", "Clan1", "Clan3"], recruiting.Clans.Select(clan => clan.Name));
        var recruitingByPoints = await clans.SearchAsync("clan", ClanSearchOrder.Points, true, 1, 1, Ct);
        Assert.Equal((3, "Clan0"), (recruitingByPoints.Total, Assert.Single(recruitingByPoints.Clans).Name));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await clans.SearchAsync("", (ClanSearchOrder)9, false, 0, 1, Ct));
    }

    [Fact]
    public async Task Posts_page_newest_first_and_belong_to_one_clan()
    {
        var clans = await CreateEmptyAsync();
        var a = await clans.CreateAsync("Alpha", "", 30, 1, "One", 1, Ct);
        var b = await clans.CreateAsync("Beta", "", 30, 2, "Two", 1, Ct);
        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
            ids.Add((await clans.AddPostAsync(a!.ClanId, 1, "One", 1, $"post {i}", Ct)).PostId);
        Assert.Equal(ids.OrderBy(id => id), ids);
        var newest = await clans.ListPostsAsync(a!.ClanId, 0, 2, Ct);
        Assert.Equal([ids[3], ids[2]], newest.Select(post => post.PostId));
        var older = await clans.ListPostsAsync(a.ClanId, ids[2], 5, Ct);
        Assert.Equal([ids[1], ids[0]], older.Select(post => post.PostId));
        Assert.Equal([ids[2], ids[3]], (await clans.ListPostsAfterAsync(a.ClanId, ids[1], 5, Ct)).Select(post => post.PostId));
        Assert.Equal([ids[0]], (await clans.ListPostsAfterAsync(a.ClanId, 0, 1, Ct)).Select(post => post.PostId));
        Assert.Empty(await clans.ListPostsAfterAsync(b!.ClanId, 0, 5, Ct));
        Assert.Null(await clans.FindPostAsync(b!.ClanId, ids[0], Ct));
        Assert.False(await clans.DeletePostAsync(b.ClanId, ids[0], Ct));
        Assert.True(await clans.DeletePostAsync(a.ClanId, ids[0], Ct));
        Assert.Null(await clans.FindPostAsync(a.ClanId, ids[0], Ct));
    }
}
