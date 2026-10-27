using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Localization;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoomCommandsBindingTests
{
    [Theory]
    [InlineData("zh-CN", "test-simplified")]
    [InlineData("zh-TW", "test-traditional")]
    [InlineData("ko", "test-korean")]
    [InlineData("en", "test-default")]
    [InlineData("id", "test-indonesian")]
    [InlineData("unknown", "test-default")]
    [InlineData(null, "test-default")]
    public async Task AiNamesFollowEachRecipientAcrossRoomAndRoundSurfaces(string? language, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "bff-ai-names-" + Guid.NewGuid().ToString("N") + ".json");
        var resources = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() { ["test.ai.name"] = "test-default" },
            ["zh-Hans"] = new() { ["test.ai.name"] = "test-simplified" },
            ["zh-Hant"] = new() { ["test.ai.name"] = "test-traditional" },
            ["ko"] = new() { ["test.ai.name"] = "test-korean" },
            ["id"] = new() { ["test.ai.name"] = "test-indonesian" },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(resources));
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow
        {
            Id = "localization", Plugin = "localization", Source = "test",
            Config = new Dictionary<string, object?>
            {
                ["resources_path"] = path, ["default_language"] = "en",
                ["launcher_languages"] = new object[]
                {
                    new Dictionary<string, object?> { ["launcher_language"] = "zh-CN", ["language"] = "zh-Hans" },
                    new Dictionary<string, object?> { ["launcher_language"] = "zh-TW", ["language"] = "zh-Hant" },
                },
            },
        }, services, events, NullLogger.Instance);
        try
        {
            await new LocalizationPlugin().ActivateAsync(context, default);
            var texts = services.Get<IGameText>();
            using var harness = await Harness.CreateAsync(ai: true, texts: texts, nicknameKey: "test.ai.name", level: 57);
            using var hostLanguage = language is null ? null : harness.Host.RegisterFeature<ISessionLanguage>(new AiLanguage(language));
            using var guestLanguage = harness.Guest.RegisterFeature<ISessionLanguage>(new AiLanguage("ko"));
            await harness.Dispatch(harness.Host, 402, CreateBody());
            harness.Room.ModeId = 38;
            await harness.Dispatch(harness.Host, 2076, Request("RoomAiAddRequest", new()
            {
                ["team"] = (byte)0xBA, ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 2,
            }));
            AssertSeat(harness.Host.Body(503), expected);
            await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
            var install = PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", harness.Guest.Body(117));
            var users = Assert.IsAssignableFrom<IReadOnlyList<object?>>(install["user_slots"]);
            Assert.Equal("host", ((IReadOnlyDictionary<string, object?>)users[0]!)["nickname"]);
            Assert.Equal("test-korean", ((IReadOnlyDictionary<string, object?>)users[1]!)["nickname"]);
            var bot = Assert.Single(harness.Room.Bots).Appearance;
            Assert.Equal("test bot", bot.Nickname);
            Assert.Equal(57, bot.Level);
            var humans = harness.Room.Seats.Select(seat => new RoomMemberView(seat.Slot, seat.UserNum, seat.Nickname,
                seat.Level, seat.Team, seat.Ready, seat.CharacterId, seat.EquippedItemIds ?? [])).ToArray();
            var room = new RoomView(harness.Room.Id, "room", 38, "map", 4, 0, true, [],
                [.. humans, bot], harness.Room.BuildSettings);
            await harness.Sender<RoomSeatUpdated>().SendAsync(harness.Host, new(room.RoomId, bot) { Room = room }, default);
            AssertSeat(harness.Host.Body(503), expected);
            await harness.Dispatch(harness.Host, 517, Request("RoomPingRequest", new() { ["unknown_n0"] = 0 }));
            install = PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", harness.Host.Body(117));
            users = Assert.IsAssignableFrom<IReadOnlyList<object?>>(install["user_slots"]);
            Assert.Equal(expected, ((IReadOnlyDictionary<string, object?>)users[1]!)["nickname"]);
            var waiting = PacketCodec.Read(Tables, "kr-1256", "gspRepWaitUserList", harness.Host.Body(118));
            var waitingNames = Assert.IsAssignableFrom<IReadOnlyList<object?>>(waiting["users"])
                .Select(row => ((IReadOnlyDictionary<string, object?>)row!)["nickname"]).ToArray();
            Assert.Contains(expected, waitingNames);
            Assert.Contains("host", waitingNames);
            Assert.DoesNotContain("test bot", waitingNames);

            harness.MountRoundNames();
            foreach (var (session, name) in new[] { (harness.Host, expected), (harness.Guest, "test-korean") })
            {
                await harness.Sender<RoomMatchStarting>().SendAsync(session, new(room, "map"), default);
                AssertSeat(session.Body(503), name);
                var scoreboard = new RoundScoreboard(room.RoomId, 0, [0, 0],
                [
                    new(0, "host", 1, 1, new(0, 0, 0, 0, 0), null),
                    new(1, bot.Nickname, 2, bot.Level, new(1, 0, 0, 0, 0), null) { NicknameKey = bot.Ai!.NicknameKey },
                ]) { ModeId = 38 };
                await harness.Sender<RoundScoreboard>().SendAsync(session, scoreboard, default);
                var wire = Encoding.Unicode.GetString(session.Body(614));
                Assert.Contains(name, wire, StringComparison.Ordinal);
                Assert.Contains("host", wire, StringComparison.Ordinal);
                Assert.DoesNotContain("test bot", wire, StringComparison.Ordinal);
            }
            await harness.Dispatch(harness.Host, 508, Request("RoomMasterChangeRequest", new() { ["nickname"] = "guest" }));
            Assert.Equal("test.ai.name", Assert.Single(harness.Room.Bots).Appearance.Ai!.NicknameKey);
            Assert.Equal("test bot", Assert.Single(harness.Room.Bots).Appearance.Nickname);
            Assert.Equal("host", harness.Room.Seats.Single(seat => seat.UserNum == 11).Nickname);
        }
        finally
        {
            await context.UnwindAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LocalizedPresetsRejectMissingLocalizationBeforeRoomMutation()
    {
        await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(ai: true, nicknameKey: "missing.ai.name"));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, " ")]
    [InlineData(1, null)]
    public async Task InvalidAiDisplayConfigurationFailsAtActivation(int level, string? key)
    {
        await Assert.ThrowsAsync<ConfigException>(() => Harness.CreateAsync(ai: true, nicknameKey: key!, level: level));
    }

    private static void AssertSeat(byte[] body, string name)
    {
        var update = PacketCodec.Read(Tables, "kr-1256", "RoomSeatUpdate", body);
        var user = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(update["user"]);
        Assert.Equal(name, user["nickname"]);
        Assert.Equal(57, user["level"]);
    }

    private sealed record AiLanguage(string LauncherLanguage) : ISessionLanguage;
}
