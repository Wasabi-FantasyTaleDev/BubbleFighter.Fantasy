using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoomCommandsBindingTests
{
    [Fact]
    public async Task AiModifiersSurviveInstallRefreshStartAndControllerMigration()
    {
        uint[] words = [4294965296, 0x3F800000, 0, 7];
        object[] attributes = [new Dictionary<string, object?>
        {
            ["item_id"] = 203108, ["kind"] = 7u, ["values"] = words,
            ["unknown_28"] = uint.MaxValue, ["unknown_32"] = "material",
        }];
        using var harness = await Harness.CreateAsync(ai: true, itemAttributes: attributes, roundsBound: true);
        await harness.Dispatch(harness.Host, 402, CreateBody());
        harness.Room.ModeId = 38;
        await harness.Dispatch(harness.Host, 2076, Request("RoomAiAddRequest", new()
        {
            ["team"] = (byte)0xBA, ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 2,
        }));
        AssertModifiers(harness.Host.Body(503), "RoomSeatUpdate");
        var bot = Assert.Single(harness.Room.Bots).Appearance;
        Assert.Equal(words, Assert.Single(bot.ItemAttributes).Values);
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new()
        {
            ["room_id"] = harness.Room.Id, ["password"] = "pw",
        }));
        AssertModifiers(harness.Guest.Body(117), "gspGotoRoom");
        await harness.Dispatch(harness.Host, 517, Request("RoomPingRequest", new() { ["unknown_n0"] = 0 }));
        AssertModifiers(harness.Host.Body(117), "gspGotoRoom");
        harness.MountRoundNames();
        var room = new RoomView(harness.Room.Id, "room", 38, "map", 4, 0, true, [],
            [bot], harness.Room.BuildSettings);
        await harness.Sender<RoomMatchStarting>().SendAsync(harness.Host, new(room, "map"), default);
        AssertModifiers(harness.Host.Body(503), "RoomSeatUpdate");
        await harness.Dispatch(harness.Host, 508, Request("RoomMasterChangeRequest", new() { ["nickname"] = "guest" }));
        var migrated = Assert.Single(harness.Room.Bots).Appearance;
        Assert.Equal(22, migrated.Ai!.ControllerUserNum);
        Assert.Equal(words, Assert.Single(migrated.ItemAttributes).Values);
        AssertModifiers(harness.Guest.Body(503), "RoomSeatUpdate");

        static void AssertModifiers(byte[] body, string structure)
        {
            var packet = PacketCodec.Read(Tables, "kr-1256", structure, body);
            var profiles = Assert.IsAssignableFrom<IReadOnlyList<object?>>(packet["profile_entries"]);
            var profile = profiles.Cast<IReadOnlyDictionary<string, object?>>()
                .Single(row => (int)row["user_number"]! == 1);
            var rows = Assert.IsAssignableFrom<IReadOnlyList<object?>>(profile["unknown_n6"]);
            var attribute = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(rows));
            Assert.Equal(203108, attribute["unknown_4"]);
            Assert.Equal(7, attribute["unknown_8"]);
            Assert.Equal(new object?[] { -2000, 0x3F800000, 0, 7 }, attribute["unknown_12"]);
            Assert.Equal(-1, attribute["unknown_28"]);
            Assert.Equal("material", attribute["unknown_32"]);
        }
    }

    [Fact]
    public async Task InvalidModifierTargetsAndMissingVectorsFailAtActivation()
    {
        foreach (var row in new Dictionary<string, object?>[]
        {
            new() { ["item_id"] = 203109, ["kind"] = 7, ["values"] = new[] { 1 } },
            new() { ["item_id"] = 203108, ["kind"] = 7, ["values"] = null },
            new() { ["item_id"] = 203108, ["kind"] = 7, ["unknown_32"] = null },
        })
            await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(ai: true, itemAttributes: [row]));
    }

    [Fact]
    public void CombinedModifierPayloadCannotExceedTheNativeFrame()
    {
        var options = new InGameOptions
        {
            AiModeIds = [38],
            AiPresets = [new()
            {
                Difficulty = 1, Nickname = "bot", CharacterItemId = 300025, EquippedItemIds = [203108],
                ItemAttributes = [.. Enumerable.Range(0, 3).Select(_ => new RoomAiItemAttributeOptions
                {
                    ItemId = 203108, Values = new uint[6000],
                })],
            }],
        };
        Assert.Contains(options.Validate(), result => result.ErrorMessage!.Contains("native packet body limit", StringComparison.Ordinal));
    }

    [Fact]
    public void PackagedPresetsBindAllModifiersAndHaveEveryRecipientLanguage()
    {
        var profiles = Path.Combine(Root, "profiles");
        var rows = new ProfileComposer(profiles, name => name == "BFF_MYSQL_PASSWORD" ? "unused" : null)
            .Compose("kr-1256", [Path.Combine(profiles, "kr-1256", "ai-rooms.patch.yaml")]);
        var options = new PluginConfig(rows.Single(row => row.Id == "ingame")).Bind<InGameOptions>();
        Assert.Equal(22, options.AiPresets.Length);
        Assert.Equal(new[] { 6, 4, 6, 6 }, options.AiPresets.GroupBy(preset => preset.Difficulty).OrderBy(group => group.Key).Select(group => group.Count()));
        Assert.All(options.AiPresets, preset => Assert.Equal(5, preset.Level));
        Assert.Equal(82, options.AiPresets.Sum(preset => preset.ItemAttributes.Length));
        Assert.Contains(options.AiPresets.SelectMany(preset => preset.ItemAttributes).SelectMany(attribute => attribute.Values), word => word > int.MaxValue);
        using var locales = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "data", "locales", "game-messages.json")));
        foreach (var language in new[] { "en", "zh-Hans", "zh-Hant", "ko", "id" })
            foreach (var preset in options.AiPresets)
            {
                var name = locales.RootElement.GetProperty(language).GetProperty(preset.NicknameKey).GetString();
                Assert.False(string.IsNullOrWhiteSpace(name));
                if (language == "ko") Assert.Equal(preset.Nickname, name);
            }
    }
}
