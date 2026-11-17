using System.Buffers.Binary;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// The clan connection's frames fit the client's clan packet object: 65536 bytes on kr-812 and its 64 KB descendants,
/// 8192 on the id-400 family, id-1809 and the 2019 builds (packets/builds.yaml <c>clan_packet_bytes</c>). A board page
/// and an applicant list carry the entries that fit and count only those.
/// </summary>
public sealed class ClanPacketSizeTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    [Theory]
    [InlineData("kr-812", 65536)]
    [InlineData("kr-1256", 65536)]
    [InlineData("cn-717", 65536)]
    [InlineData("cn-742", 65536)]
    [InlineData("id-400", 8192)]
    [InlineData("cn-404", 8192)]
    [InlineData("cn-408", 8192)]
    [InlineData("cn-706", 8192)]
    [InlineData("id-1809", 8192)]
    [InlineData("cn-774", 8192)]
    [InlineData("tw-454", 8192)]
    public void EachClanBuildStatesItsClanPacketSize(string build, int bytes) =>
        Assert.Equal(bytes, ClanDirectoryBinding.PacketBytes(Tables, build));

    [Fact]
    public void ABoardPageOnAn8KBuildHoldsThePostsThatFitAndCountsThem()
    {
        var at = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var posts = Enumerable.Range(1, 100)
            .Select(i => new ClanPostRecord(1000 + i, 1000, i, "Twelve-chars", 1, new string('x', 60), at)).ToArray();
        var board = new ClanBoardSent(posts[0].PostId, posts[^1].PostId, posts);
        var maxBody = ClanDirectoryBinding.PacketBytes(Tables, "cn-774") - 4;

        var body = ClanDirectoryBinding.WriteBoard(Tables, "cn-774", board, maxBody);

        Assert.InRange(body.Length, 1, maxBody);
        var count = BinaryPrimitives.ReadInt32LittleEndian(body);
        Assert.InRange(count, 1, 99);
        // Each of these posts is 4 + 26 + 4 + 4 + 2 + 8 + 122 = 170 bytes after the 12-byte header.
        Assert.Equal(12 + count * 170, body.Length);
        Assert.True(body.Length + 170 > maxBody);
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(ClanDirectoryBinding.WriteBoard(Tables, "kr-1256", board)));
    }

    [Fact]
    public void AnApplicantListOnAn8KBuildHoldsTheOldestApplicantsThatFit()
    {
        var rows = Enumerable.Range(1, 200).Select(i => new ClanApplicantRow(i, "Twelve-chars", ClanRole.Applicant)).ToArray();
        var maxBody = ClanDirectoryBinding.PacketBytes(Tables, "tw-454") - 4;

        var body = ClanDirectoryBinding.WriteApplicants(Tables, "tw-454", new ClanApplicantsSent(rows), maxBody);

        Assert.InRange(body.Length, 1, maxBody);
        var count = BinaryPrimitives.ReadInt32LittleEndian(body);
        Assert.InRange(count, 1, 199);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)));
        Assert.True(ClanDirectoryBinding.WriteApplicants(Tables, "tw-454", new ClanApplicantsSent(rows.Take(count + 1).ToArray())).Length > maxBody);
    }
}
