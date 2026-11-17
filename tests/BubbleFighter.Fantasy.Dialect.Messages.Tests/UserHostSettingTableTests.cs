using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the host-setting frames: the prize-setting request (<c>action</c>, <c>prize</c>: writers
/// kr-812 sub_809110, kr-1256 sub_9DFC50, cn-774 sub_8E8E60, tw-454 sub_A69500), the setting update
/// (<c>action</c>, <c>prize</c>, <c>min_prize</c>, <c>max_prize</c>: readers kr-812 sub_80A400,
/// kr-1256 sub_9CBF50, cn-774 sub_BC77E0, tw-454 sub_AA6440), the default prize bounds
/// (<c>min_prize</c>, <c>max_prize</c>: kr-812 sub_6DB1B0, kr-1256 0x9D542B), and the start reply
/// (<c>status</c>: kr-812 sub_80A910, kr-1256 sub_9CB9C0, cn-774 sub_BC70C0, tw-454 sub_AA5D20).
/// </summary>
public sealed class UserHostSettingTableTests
{
    private const string TwoWords = "0100000005000000";
    private const string FourWords = "01000000050000000A00000000000000";

    /// <summary>Each host-setting body round-trips at its client-established byte length across enabled builds.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void HostSettingBodiesRoundTripAtTheirPinnedLengths(string build)
    {
        var request = Convert.FromHexString(TwoWords);
        var update = Convert.FromHexString(FourWords);
        var defaults = Convert.FromHexString(TwoWords);
        var startReply = Convert.FromHexString("FCFFFFFF");
        Assert.Equal(8, request.Length);
        Assert.Equal(16, update.Length);
        Assert.Equal(8, defaults.Length);
        Assert.Equal(4, startReply.Length);

        foreach (var (structure, body, length) in new[]
                 {
                     ("UserHostSettingRequest", request, 8),
                     ("UserHostSettingUpdate", update, 16),
                     ("UserHostDefaultSetting", defaults, 8),
                     ("UserHostStartReply", startReply, 4),
                 })
        {
            var decoded = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
            Assert.Equal(length, body.Length);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, decoded));
        }
    }

    /// <summary>Field names preserve the proven meanings and wire widths.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void FieldsExposeProvenRolesAndWidths(string build)
    {
        Assert.Equal("action:u32,prize:u32", Fields(build, "UserHostSettingRequest"));
        Assert.Equal("action:u32,prize:u32,min_prize:u32,max_prize:u32", Fields(build, "UserHostSettingUpdate"));
        Assert.Equal("min_prize:u32,max_prize:u32", Fields(build, "UserHostDefaultSetting"));
        Assert.Equal("status:u32", Fields(build, "UserHostStartReply"));
    }

    /// <summary>Selected truncated and overlong host-setting bodies are rejected.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void HostSettingBodiesRejectEveryOtherLength(string build)
    {
        foreach (var (structure, length) in new[]
                 {
                     ("UserHostSettingRequest", 0), ("UserHostSettingRequest", 4),
                     ("UserHostSettingRequest", 7), ("UserHostSettingRequest", 9),
                     ("UserHostSettingRequest", 12),
                     ("UserHostSettingUpdate", 0), ("UserHostSettingUpdate", 12),
                     ("UserHostSettingUpdate", 15), ("UserHostSettingUpdate", 17),
                     ("UserHostSettingUpdate", 20),
                     ("UserHostDefaultSetting", 0), ("UserHostDefaultSetting", 4),
                     ("UserHostDefaultSetting", 9),
                     ("UserHostStartReply", 0), ("UserHostStartReply", 3),
                     ("UserHostStartReply", 5),
                 })
        {
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, structure, new byte[length]));
        }
    }

    private static string Fields(string build, string structure) =>
        string.Join(",", TestBuilds.Packets.Resolve(build, structure)
            .Select(field => field.Name + ":" + field.Type));
}
