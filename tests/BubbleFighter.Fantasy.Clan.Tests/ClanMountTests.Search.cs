using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// The clan search on the mounted rows: each of the dialog's five modes answers five rows a page in its own
/// order and filter, with the mode, the page and the text echoed in the header.
/// </summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// Six recruiting clans and one closed clan with the most points: the refresh list pages them by id, the
    /// recruiting list leaves the closed one out, the points list starts with it, the name search ignores case and
    /// a page below 1 is answered as page 1. A row carries the stored clan and its master's name.
    /// </summary>
    [Fact]
    public async Task SearchAnswersOnePageInTheModesOrderWithTheHeaderEchoed()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        for (var i = 1; i <= 6; i++)
            await directory.CreateAsync($"Wolf{i}", $"intro{i}", 30, 100 + i, $"Master{i}", 1, CancellationToken.None);
        var bears = (await directory.CreateAsync("Bears", "", 30, 107, "Bruno", 1, CancellationToken.None))!;
        Assert.True(await directory.UpdateAsync(bears with { Points = 50, Recruitment = 0, Mark = 3 }, CancellationToken.None));
        var clan = await LoginClanAsync(rig);

        var all = await SearchAsync(rig, clan, ClanSearchMode.All, 1, "");
        Assert.Equal((ClanSearchMode.All, 1, ""), (all.Mode, all.Page, all.Text));
        Assert.Equal(["Wolf1", "Wolf2", "Wolf3", "Wolf4", "Wolf5"], all.Rows.Select(row => row.Name));
        Assert.Equal((1000u, 101u, "Master1", 1u, 0u, 1u, 1u, 0u, "intro1", NoDisband, 0u),
            (all.Rows[0].Id, all.Rows[0].MasterId, all.Rows[0].MasterName, all.Rows[0].Level, all.Rows[0].Points,
             all.Rows[0].Recruitment, all.Rows[0].Members, all.Rows[0].Skipped, all.Rows[0].Introduction, all.Rows[0].Disband, all.Rows[0].Mark));
        Assert.Equal(["Wolf6", "Bears"], (await SearchAsync(rig, clan, ClanSearchMode.All, 2, "")).Rows.Select(row => row.Name));
        Assert.Equal(["Wolf6"], (await SearchAsync(rig, clan, ClanSearchMode.AllRecruiting, 2, "")).Rows.Select(row => row.Name));

        var points = await SearchAsync(rig, clan, ClanSearchMode.Points, 1, "");
        Assert.Equal(("Bears", 50u, 3u, 0u), (points.Rows[0].Name, points.Rows[0].Points, points.Rows[0].Mark, points.Rows[0].Recruitment));
        Assert.Equal("Wolf1", (await SearchAsync(rig, clan, ClanSearchMode.PointsRecruiting, 1, "")).Rows[0].Name);

        var named = await SearchAsync(rig, clan, ClanSearchMode.Name, 0, "WOLF");
        Assert.Equal((ClanSearchMode.Name, 1, "WOLF", 5), (named.Mode, named.Page, named.Text, named.Rows.Count));
        // Only the name search filters by text; the dialog clears it for the other modes.
        Assert.Equal(5, (await SearchAsync(rig, clan, ClanSearchMode.All, 1, "Bears")).Rows.Count);
        Assert.Empty((await SearchAsync(rig, clan, ClanSearchMode.Name, 1, "cats")).Rows);
    }

    /// <summary>A search on a connection that has not logged in is an error, not an answer.</summary>
    [Fact]
    public async Task SearchBeforeLoginFails()
    {
        await using var rig = await Rig.StartAsync();
        var clan = new RecordingSession(loggedIn: false, connectionId: 9);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanSearch"), SearchBody(ClanSearchMode.All, 1, "")));
        Assert.Empty(clan.Frames);
    }

    /// <summary>Sends one search and parses its single answer as kr-1256 sub_5290A0 reads it.</summary>
    private static async Task<SearchPage> SearchAsync(Rig rig, RecordingSession clan, int mode, int page, string text)
    {
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanSearch"), SearchBody(mode, page, text));
        var frame = Assert.Single(clan.Frames);
        Assert.Equal(rig.ClanTable.S2C("ClanSearchResult"), frame.Opcode);
        var reader = new PacketReader(frame.Payload);
        var count = reader.ReadUInt32();
        var result = new SearchPage((int)reader.ReadUInt32(), (int)reader.ReadUInt32(), reader.ReadWideCString(), []);
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadUInt32();
            var name = reader.ReadWideCString();
            var masterId = reader.ReadUInt32();
            var masterName = reader.ReadWideCString();
            var level = reader.ReadUInt32();
            var points = reader.ReadUInt32();
            _ = reader.ReadBytes(8);
            Assert.Equal(new string('0', 16), Convert.ToHexString(reader.ReadBytes(8)));
            var recruitment = reader.ReadUInt32();
            var members = reader.ReadUInt32();
            var skipped = reader.ReadUInt32();
            var introduction = reader.ReadWideCString();
            var disband = Convert.ToHexString(reader.ReadBytes(8));
            _ = reader.ReadBytes(8);
            result.Rows.Add(new SearchRow(id, name, masterId, masterName, level, points, recruitment, members, skipped,
                introduction, disband, reader.ReadUInt32()));
        }
        Assert.Equal(0, reader.Remaining);
        return result;
    }

    /// <summary>The search request as kr-1256 sub_526020 writes it.</summary>
    private static byte[] SearchBody(int mode, int page, string text)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteUInt32((uint)mode);
        writer.WriteUInt32((uint)page);
        writer.WriteWideCString(text);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed record SearchPage(int Mode, int Page, string Text, List<SearchRow> Rows);

    private sealed record SearchRow(uint Id, string Name, uint MasterId, string MasterName, uint Level, uint Points,
        uint Recruitment, uint Members, uint Skipped, string Introduction, string Disband, uint Mark);
}
