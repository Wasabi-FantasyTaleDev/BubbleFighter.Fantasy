using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises multilingual room-mode tags, the shared selector catalog, and the actual room creation flow.</summary>
public sealed class RoomModeNameTagTests
{
    private static string CatalogPath() => Path.Combine(AppContext.BaseDirectory, "data", "room-modes.json");

    private static RoomModeNames Shipped() => RoomModeNames.Load(CatalogPath())!;

    /// <summary>Every known client mode remains selectable in all seven languages, including native-dialog modes and unnamed codenames.</summary>
    [Fact]
    public void EverySelectorTokenResolvesToItsOwnKnownMode()
    {
        var names = Shipped();
        using var document = JsonDocument.Parse(File.ReadAllText(CatalogPath()));
        var modes = document.RootElement.GetProperty("modes").EnumerateArray().ToArray();
        Assert.Equal(Enum.GetValues<GameModeId>().Select(value => (int)value).Order(), modes.Select(mode => mode.GetProperty("id").GetInt32()).Order());
        string[] languages = ["en", "id", "ko", "th", "vi", "zh-CN", "zh-TW"];
        foreach (var mode in modes)
        {
            var id = mode.GetProperty("id").GetInt32();
            var tokens = mode.GetProperty("tokens").EnumerateObject().ToArray();
            Assert.Equal(languages, tokens.Select(token => token.Name).Order(StringComparer.Ordinal));
            foreach (var token in tokens)
            {
                var text = token.Value.GetString()!;
                Assert.DoesNotContain(text, char.IsWhiteSpace);
                Assert.Equal(new RoomModeTag.Result("Original title", id, null), RoomModeTag.Extract($"#{text}#Original title", names));
            }
        }
    }

    /// <summary>Native and translated names all reach the same typed room creation path and retain the original room name.</summary>
    [Theory]
    [InlineData("爆破模式")]
    [InlineData("爆炸模式")]
    [InlineData("폭파미션")]
    [InlineData("BombMission")]
    [InlineData("Bombmission")]
    [InlineData("Đặtbom")]
    [InlineData("โหมดวางระเบิด")]
    public async Task LocalizedTagChangesTheCreatedModeAndTheReturnedBuildSettings(string token)
    {
        var rig = new RoomFlowTests.Rig();
        rig.Flow.ModeNames = Shipped();
        var host = RoomFlowTests.Rig.Player(11, "host");
        try
        {
            await rig.Flow.HandleCreateAsync(host, new($"#{token}#Original room", "", 1, "rel_Village07_Easy02",
                new RoomBuildSettings("test", new byte[] { 1, 2, 3 })), default);
            var room = rig.Registry.TryGet(host.Id)!;
            Assert.Equal(19, room.ModeId);
            Assert.Equal("Original room", room.RoomName);
            Assert.Equal(19, room.BuildSettings!.Mode);
            var entered = Assert.Single(rig.Entered.To(host)).Room;
            Assert.Equal(19, entered.ModeId);
            Assert.Equal("Original room", entered.Name);
            Assert.Single(rig.Notices.To(host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A late native title update applies the same named tag to the host's existing waiting room.</summary>
    [Fact]
    public async Task LateRoomTitleUpdateUsesLocalizedNames()
    {
        var rig = new RoomFlowTests.Rig();
        rig.Flow.ModeNames = Shipped();
        var host = RoomFlowTests.Rig.Player(11, "host");
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("", "", 1, "rel_Village07_Easy02") { UpdateExistingTitle = true }, default);
            await rig.Flow.HandleCreateAsync(host, new("#爆破模式#Late title", "", 1, "rel_Village07_Easy02") { UpdateExistingTitle = true }, default);
            var room = rig.Registry.TryGet(host.Id)!;
            Assert.Equal(19, room.ModeId);
            Assert.Equal("Late title", room.RoomName);
            Assert.Equal(19, rig.Entered.To(host)[^1].Room.ModeId);
            Assert.Single(rig.Notices.To(host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Unrelated hashtags, internal whitespace and genuinely ambiguous native names remain ordinary title text.</summary>
    [Theory]
    [InlineData("Room #friends#")]
    [InlineData("#Bomb Mission#Room")]
    [InlineData("#Bomb\tMission#Room")]
    [InlineData("#生存模式#Room")]
    [InlineData("#SinglePlay#Room")]
    [InlineData("#NoItemAI#Room")]
    [InlineData("#爆破模式")]
    public void UnrecognizedNameDoesNotMutateTheTitle(string title) =>
        Assert.Equal(new RoomModeTag.Result(title, null, null), RoomModeTag.Extract(title, Shipped()));

    /// <summary>Unrecognized text does not mask an existing numeric tag, and the first recognized tag keeps later tags intact.</summary>
    [Theory]
    [InlineData("#friends# #77#Room", "#friends# Room", 77)]
    [InlineData("#friends#77#", "#friends", 77)]
    [InlineData("#BombMission#Room #8#", "Room #8#", 19)]
    [InlineData("#8#Room #BombMission#", "Room #BombMission#", 8)]
    [InlineData("A #폭파미션# B", "A B", 19)]
    public void MixedTagsPreserveNumericAndFirstTagBehavior(string input, string shown, int mode) =>
        Assert.Equal(new RoomModeTag.Result(shown, mode, null), RoomModeTag.Extract(input, Shipped()));

    /// <summary>An invalid first numeric tag still prevents a later tag from changing the room.</summary>
    [Fact]
    public void InvalidNumericPrecedenceRemainsUnchanged() =>
        Assert.Equal(new RoomModeTag.Result("#0##爆破模式#Room", null, "#0#"), RoomModeTag.Extract("#0##爆破模式#Room", Shipped()));

    /// <summary>Native Unicode names and ordinal Latin casing resolve under invariant globalization.</summary>
    [Fact]
    public void UnicodeNamesAndOrdinalCaseHaveStableIdentity()
    {
        var names = Shipped();
        Assert.Equal(19, RoomModeTag.Extract("#Đặtbom#Room", names).ModeId);
        Assert.Equal(19, RoomModeTag.Extract("#bombmission#Room", names).ModeId);
    }

    /// <summary>Without a configured name table, old numeric behavior is retained and names remain room text.</summary>
    [Fact]
    public void NameSupportRequiresAnExplicitTable()
    {
        Assert.Null(RoomModeNames.Load(""));
        Assert.Equal(new RoomModeTag.Result("#爆破模式#Room", null, null), RoomModeTag.Extract("#爆破模式#Room"));
        Assert.Equal(new RoomModeTag.Result("Room", 19, null), RoomModeTag.Extract("#19#Room"));
    }

    /// <summary>Configuration rejects names whose whitespace removal or ordinal casing would identify two modes.</summary>
    [Theory]
    [InlineData("Bomb Mission", "bombmission")]
    [InlineData("Đặt bom", "Đặtbom")]
    public void ConflictingNamesFailBeforeActivation(string first, string second)
    {
        static object Row(int id, string name) => new
        {
            id, key = "mode" + id,
            names = new { en = name },
            tokens = new { en = string.Concat(name.Where(character => !char.IsWhiteSpace(character))) },
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { format = 1, modes = new[] { Row(1, first), Row(3, second) } }));
        var error = Assert.Throws<ConfigException>(() => RoomModeNames.Parse(document.RootElement, "test-table"));
        Assert.Contains("ambiguous", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Malformed tables cannot replace numeric tags, inject delimiters or conceal duplicate JSON properties.</summary>
    [Theory]
    [InlineData("{\"format\":1,\"modes\":[]}")]
    [InlineData("{\"format\":1,\"format\":1,\"modes\":[]}")]
    [InlineData("{\"format\":2,\"modes\":[]}")]
    [InlineData("{\"format\":1,\"modes\":[{\"id\":1,\"key\":\"one\",\"names\":{\"en\":\"123\"},\"tokens\":{\"en\":\"123\"}}]}")]
    [InlineData("{\"format\":1,\"modes\":[{\"id\":1,\"key\":\"one\",\"names\":{\"en\":\"A#B\"},\"tokens\":{\"en\":\"A#B\"}}]}")]
    [InlineData("{\"format\":1,\"modes\":[{\"id\":1,\"key\":\"one\",\"names\":{\"en\":\"A B\"},\"tokens\":{\"en\":\"A B\"}}]}")]
    public void InvalidTablesFailLoud(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ConfigException>(() => RoomModeNames.Parse(document.RootElement, "test-table"));
    }

    /// <summary>A configured table that is missing is never silently disabled.</summary>
    [Fact]
    public void MissingFilesFailLoud()
    {
        Assert.Throws<FileNotFoundException>(() => RoomModeNames.Load(CatalogPath() + ".missing"));
    }
}
