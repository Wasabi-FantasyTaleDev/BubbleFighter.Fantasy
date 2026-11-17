using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>The clan board on the mounted rows, with the clan row's default page of ten posts.</summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// A seated member posts twelve times; the newest page holds ten posts newest first with the board's newest and
    /// oldest ids in the header; the older page from the last row holds the remaining two and the newer page from
    /// there returns to the ten. An empty and an over-long post are refused. The author deletes an own post, a
    /// plain member cannot delete someone else's, and every deletion is answered. A player without a seat sees an
    /// empty board and cannot post.
    /// </summary>
    [Fact]
    public async Task BoardPostsPageAndDelete()
    {
        await using var rig = await Rig.StartAsync();
        var directory = rig.Capture.Clans;
        var clan = await LoginClanAsync(rig);
        Assert.Equal([(rig.ClanTable.S2C("ClanBoard"), Word(0) + Word(0) + Word(0))], await SendAsync(rig, clan, "ClanBoardRequest", Word(1) + Word(0)));
        Assert.Equal([(rig.ClanTable.S2C("ClanBoardWriteResult"), Word(0))], await SendAsync(rig, clan, "ClanBoardWrite", Word(5) + Wc("hi")));

        var bears = (await directory.CreateAsync("Bears", "", 30, 101, "Bruno", 1, CancellationToken.None))!;
        Assert.True(await directory.AddMemberAsync(bears.ClanId, UserNum, "clanner", 1, ClanRole.Applicant, CancellationToken.None));
        Assert.True(await directory.SeatAsync(bears.ClanId, UserNum, ClanRole.Member, CancellationToken.None));
        for (var i = 1; i <= 12; i++)
            Assert.Equal([(rig.ClanTable.S2C("ClanBoardWriteResult"), Word(1))], await SendAsync(rig, clan, "ClanBoardWrite", Word(5) + Wc($"post {i}")));
        Assert.Equal([(rig.ClanTable.S2C("ClanBoardWriteResult"), Word(0))], await SendAsync(rig, clan, "ClanBoardWrite", Word(5) + Wc("")));
        Assert.Equal([(rig.ClanTable.S2C("ClanBoardWriteResult"), Word(0))], await SendAsync(rig, clan, "ClanBoardWrite", Word(5) + Wc(new string('x', 61))));
        var ids = (await directory.ListPostsAsync(bears.ClanId, 0, 20, CancellationToken.None)).Select(post => (int)post.PostId).ToArray();
        Assert.Equal(12, ids.Length);

        var newest = Board(await SendAsync(rig, clan, "ClanBoardRequest", Word(1) + Word(0)));
        Assert.Equal((ids[0], ids[^1]), (newest.Newest, newest.Oldest));
        Assert.Equal(ids.Take(10), newest.Ids);
        Assert.Equal(("clanner", "post 12"), (newest.FirstAuthor, newest.FirstBody));
        Assert.Equal(ids.Skip(10), Board(await SendAsync(rig, clan, "ClanBoardRequest", Word(2) + Word(newest.Ids[^1]))).Ids);
        Assert.Equal(ids.Take(10), Board(await SendAsync(rig, clan, "ClanBoardRequest", Word(3) + Word(ids[10]))).Ids);

        var other = await directory.AddPostAsync(bears.ClanId, 101, "Bruno", 1, "mine", CancellationToken.None);
        Assert.Equal([(rig.ClanTable.S2C("ClanBoardDeleted"), "")], await SendAsync(rig, clan, "ClanBoardDelete", Word((int)UserNum) + Word((int)other.PostId)));
        Assert.NotNull(await directory.FindPostAsync(bears.ClanId, other.PostId, CancellationToken.None));
        Assert.Equal([(rig.ClanTable.S2C("ClanBoardDeleted"), "")], await SendAsync(rig, clan, "ClanBoardDelete", Word((int)UserNum) + Word(ids[0])));
        Assert.Null(await directory.FindPostAsync(bears.ClanId, ids[0], CancellationToken.None));
    }

    // The header and the per-post fields the test checks, read in ClanServerBoardPost order.
    private static (int Newest, int Oldest, int[] Ids, string FirstAuthor, string FirstBody) Board((ushort Opcode, string Hex)[] frames)
    {
        var frame = Assert.Single(frames);
        var reader = new PacketReader(Convert.FromHexString(frame.Hex));
        var count = (int)reader.ReadUInt32();
        var (newest, oldest) = ((int)reader.ReadUInt32(), (int)reader.ReadUInt32());
        var ids = new int[count];
        var (author, body) = ("", "");
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0u, reader.ReadUInt32());
            var a = reader.ReadWideCString();
            Assert.Equal(0u, reader.ReadUInt32());
            ids[i] = (int)reader.ReadUInt32();
            Assert.Equal("0100", Convert.ToHexString(reader.ReadBytes(2)));
            _ = reader.ReadBytes(8);
            var b = reader.ReadWideCString();
            if (i == 0)
                (author, body) = (a, b);
        }
        Assert.Equal(0, reader.Remaining);
        return (newest, oldest, ids, author, body);
    }
}
