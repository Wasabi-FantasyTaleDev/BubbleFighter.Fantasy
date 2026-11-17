using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoomCommandsBindingTests
{
    /// <summary>The real create request retains only KR's admitted hammer selection in both installed settings frames.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(7, 0)]
    [InlineData(10, 0)]
    [InlineData(-1, 0)]
    public async Task CreateRetainsOnlyTheAdmittedHammerWord(int selected, int expected)
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateWithMiniSelection(selected));
        Assert.Equal(new ushort[] { 117, 501, 201, 806, 503 }, harness.Host.Opcodes);
        AssertMiniSelectionInInstall(harness.Host, expected);
        AssertRetainedMiniSelection(harness, selected);
    }

    /// <summary>A joining peer receives the same admitted native selection without reinstalling the creator's room.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(7, 0)]
    [InlineData(10, 0)]
    [InlineData(-1, 0)]
    public async Task JoinRetainsOnlyTheAdmittedHammerWord(int selected, int expected)
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateWithMiniSelection(selected));
        var hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest",
            new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        Assert.Equal(new ushort[] { 503, 806, 118 }, harness.Host.Opcodes.Skip(hostCount));
        AssertMiniSelectionInInstall(harness.Guest, expected);
        AssertRetainedMiniSelection(harness, selected);
    }

    /// <summary>A committed map change retains the admitted hammer selection and sends only the settings notification.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(7, 0)]
    [InlineData(10, 0)]
    [InlineData(-1, 0)]
    public async Task MapChangeRetainsOnlyTheAdmittedHammerWord(int selected, int expected)
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateWithMiniSelection(selected));
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest",
            new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var original = harness.Room.BuildSettings!.Bytes.ToArray();
        var hostCount = harness.Host.Opcodes.Count();
        var guestCount = harness.Guest.Opcodes.Count();
        await harness.Dispatch(harness.Host, 513, Request("RoomGameSettingChangeRequest", new()
        {
            ["unknown_0"] = "riverside", ["unknown_24"] = "rel_Village07_Easy02", ["unknown_48"] = "",
            ["mode"] = 3,
        }));
        Assert.Equal(new ushort[] { 505 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new ushort[] { 505 }, harness.Guest.Opcodes.Skip(guestCount));
        Assert.Equal(harness.Host.Body(505), harness.Guest.Body(505));
        var changed = PacketCodec.Read(Tables, "kr-1256", "GameSetting", harness.Guest.Body(505));
        AssertMiniSelection(changed, expected);
        Assert.Equal("rel_Village07_Easy02", harness.Room.MapKey);
        Assert.Equal("rel_Village07_Easy02", changed["unknown_68"]);
        Assert.Equal(original, harness.Room.BuildSettings!.Bytes.ToArray());
        AssertRetainedMiniSelection(harness, selected);
    }

    private static byte[] CreateWithMiniSelection(int selected) => Raw(writer =>
    {
        var source = Source();
        (source with { Map = source.Map with { Unknown20 = selected } }).Write(writer);
        writer.WriteWideStringPrefixed("pw");
        writer.WriteByte(0xBA);
    });

    private static void AssertMiniSelectionInInstall(RecordingSession recipient, int expected)
    {
        var install = PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", recipient.Body(117));
        var setting = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(install["session_setting"]);
        AssertMiniSelection(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(setting["game_setting"]), expected);
        var repeated = PacketCodec.Read(Tables, "kr-1256", "SessionSetting", recipient.Body(501));
        AssertMiniSelection(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(repeated["game_setting"]), expected);
    }

    private static void AssertMiniSelection(IReadOnlyDictionary<string, object?> setting, int expected)
    {
        Assert.Equal(expected, setting["unknown_20"]);
        Assert.Equal(0, setting["unknown_0"]);
        Assert.Equal(false, setting["is_user_host"]);
    }

    private static void AssertRetainedMiniSelection(Harness harness, int selected)
    {
        var source = harness.Room.BuildSettings!;
        var retained = PacketCodec.Read(Tables, "kr-1256", "GameSetting", source.Bytes.Span);
        Assert.Equal(selected, retained["unknown_20"]);
        Assert.Equal(23, retained["unknown_0"]);
        Assert.Equal(true, retained["is_user_host"]);
    }
}
