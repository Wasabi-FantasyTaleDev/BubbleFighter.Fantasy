using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the host-setting frames: the request (two words, writer kr-812 sub_809110 and
/// kr-1256 sub_9DFC50), the setting update (four words, reader kr-812 sub_80A400 and
/// kr-1256 sub_9CBF50) and the default setting (two words, codec kr-812 sub_6DB1B0 and the
/// inline case kr-1256 0x9D542B). The modern builds read the same bodies into locals, so the
/// fields are stated at wire offsets. The words' purposes are unproven and stay unknown_<offset>.
/// </summary>
public sealed class UserHostSettingTableTests
{
    private const string TwoWords = "0100000005000000";
    private const string FourWords = "01000000050000000A00000000000000";

    /// <summary>Each host-setting body round-trips at its client-established byte length.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void HostSettingBodiesRoundTripAtTheirPinnedLengths(string build)
    {
        var request = Convert.FromHexString(TwoWords);
        var update = Convert.FromHexString(FourWords);
        var defaults = Convert.FromHexString(TwoWords);
        Assert.Equal(8, request.Length);
        Assert.Equal(16, update.Length);
        Assert.Equal(8, defaults.Length);

        foreach (var (structure, body, length) in new[]
                 {
                     ("UserHostSettingRequest", request, 8),
                     ("UserHostSettingUpdate", update, 16),
                     ("UserHostDefaultSetting", defaults, 8),
                 })
        {
            var decoded = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
            Assert.Equal(length, body.Length);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, decoded));
        }
    }

    /// <summary>Unknown field names preserve the documented positions and widths.</summary>
    [Theory]
    [InlineData("kr-812", "unknown_4:u32,unknown_8:u32",
        "unknown_4:u32,unknown_8:u32,unknown_12:u32,unknown_16:u32", "unknown_4:u32,unknown_8:u32")]
    [InlineData("kr-1256", "unknown_0:u32,unknown_4:u32",
        "unknown_0:u32,unknown_4:u32,unknown_8:u32,unknown_12:u32", "unknown_0:u32,unknown_4:u32")]
    public void FieldsRetainUnknownNamesAndProvenWidths(string build, string request, string update, string defaults)
    {
        Assert.Equal(request, Fields(build, "UserHostSettingRequest"));
        Assert.Equal(update, Fields(build, "UserHostSettingUpdate"));
        Assert.Equal(defaults, Fields(build, "UserHostDefaultSetting"));
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
