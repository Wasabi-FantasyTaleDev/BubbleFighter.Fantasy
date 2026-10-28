using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Byte-length pins for legacy S2C 201 <c>gspUpdateParty</c> solo idle snapshot.
/// The same wire body is proven for cn-279, cn-324, id-400, kr-812, and cn-717.
/// </summary>
public sealed class UpdatePartyWireTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void SoloEmptyNicknameIsThirtyTwoBytes(string build)
    {
        // i32 state + u32 count + (i32 + wstr"" + i32×3) + u32 equipped0 = 4+4+4+4+12+4 = 32.
        var body = UpdatePartyCodec.WriteSolo(TestBuilds.All.Tables, build, new MainMenuRoomOpened(1, "", 300001, []));
        Assert.Equal(32, body.Length);
        Assert.Equal(UpdatePartyCodec.IdleState, BitConverter.ToInt32(body, 0));
        Assert.Equal(1, BitConverter.ToInt32(body, 4));
        Assert.Equal(1, BitConverter.ToInt32(body, 8));
        Assert.Equal(0, BitConverter.ToInt32(body, 12));
        Assert.Equal(0, BitConverter.ToInt32(body, 16));
        Assert.Equal(0, BitConverter.ToInt32(body, 20));
        Assert.Equal(1, BitConverter.ToInt32(body, 24));
        Assert.Equal(0, BitConverter.ToInt32(body, 28));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void SoloNicknamePinsPrefixedWideStringAndSavedRank(string build)
    {
        var body = UpdatePartyCodec.WriteSolo(TestBuilds.All.Tables, build, new MainMenuRoomOpened(42, "ab", 300001, [], 17));
        // 4+4+4 + (4+4) + 12 + 4 = 36.
        Assert.Equal(36, body.Length);
        Assert.Equal(42, BitConverter.ToInt32(body, 8));
        Assert.Equal(2, BitConverter.ToInt32(body, 12));
        Assert.Equal((byte)'a', body[16]);
        Assert.Equal((byte)'b', body[18]);
        Assert.Equal(0, BitConverter.ToInt32(body, 32));
        Assert.Equal(Convert.FromHexString("02000000010000002A000000020000006100620000000000000000001100000000000000"), body);
    }

    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("id-400")]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void PlazaCapableSchemasNeedIdleParty(string profile)
    {
        var schema = TestBuilds.All.Schemas.Require([profile]).Value;
        Assert.True(UpdatePartyCodec.NeedsIdleParty(schema));
    }

    [Theory]
    [InlineData("cn-735", "cn-717", "kr-812")]
    [InlineData("cn-738", "cn-735", "cn-717", "kr-812")]
    [InlineData("cn-742", "cn-738", "cn-735", "cn-717", "kr-812")]
    public void MidCnSchemasDoNotNeedIdleParty(params string[] lineage)
    {
        var choice = TestBuilds.All.Schemas.Require(lineage);
        Assert.Equal("cn-735", choice.Profile);
        Assert.Equal(lineage[0] == "cn-735" ? VariantMatch.Exact : VariantMatch.Inherited, choice.Match);
        Assert.False(UpdatePartyCodec.NeedsIdleParty(choice.Value));
    }
}
