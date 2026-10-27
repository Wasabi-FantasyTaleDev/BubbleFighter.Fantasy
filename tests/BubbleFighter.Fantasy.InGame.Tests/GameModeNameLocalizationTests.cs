using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The shipped game-message file gives each region (and, where a client spells it differently, each build) its own mode names.</summary>
public sealed class GameModeNameLocalizationTests
{
    private static string ResourcesPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "data", "locales", "game-messages.json");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("data/locales/game-messages.json was not found above the test directory.");
    }

    private static ClientProfile Profile(string id) => new(id, id[..2], 1, 0, 0, id);

    private static async Task<(IGameText Text, PluginContext Context)> ActivateAsync(string? profileLanguage = null)
    {
        var profiles = new List<object>
        {
            new Dictionary<string, object?> { ["profile"] = "cn-774", ["language"] = "zh-Hans:cn-774" },
            new Dictionary<string, object?> { ["profile"] = "id-400", ["language"] = "id:id-400" },
        };
        if (profileLanguage is not null)
            profiles.Add(new Dictionary<string, object?> { ["profile"] = "tw-454", ["language"] = profileLanguage });
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow
        {
            Id = "localization",
            Plugin = "localization",
            Source = "test",
            Config = new Dictionary<string, object?>
            {
                ["resources_path"] = ResourcesPath(),
                ["default_language"] = "en",
                ["launcher_languages"] = new List<object>
                {
                    new Dictionary<string, object?> { ["launcher_language"] = "zh-CN", ["language"] = "zh-Hans" },
                    new Dictionary<string, object?> { ["launcher_language"] = "zh-TW", ["language"] = "zh-Hant" },
                },
                ["region_languages"] = new List<object>
                {
                    new Dictionary<string, object?> { ["region"] = "cn", ["language"] = "zh-Hans" },
                    new Dictionary<string, object?> { ["region"] = "tw", ["language"] = "zh-Hant" },
                    new Dictionary<string, object?> { ["region"] = "kr", ["language"] = "ko" },
                    new Dictionary<string, object?> { ["region"] = "id", ["language"] = "id" },
                },
                ["profile_languages"] = profiles,
            },
        }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        await new LocalizationPlugin().ActivateAsync(context, default);
        return (services.Get<IGameText>(), context);
    }

    private static string Name(IGameText text, string profile, int mode)
    {
        Assert.True(text.TryFormat(Profile(profile), "mode." + mode, out var name), $"{profile} mode {mode}");
        return name;
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("en", "en")]
    [InlineData("ko", "ko")]
    [InlineData("id", "id")]
    [InlineData("vi", "en")]
    [InlineData(null, "en")]
    public async Task KoreanClientUsesLauncherLanguageOrDeploymentDefault(string? language, string resourceLanguage)
    {
        var (text, context) = await ActivateAsync();
        try
        {
            var session = new RecordingSession(701);
            using var registration = language is null ? null : session.RegisterFeature<ISessionLanguage>(new UserLanguage(language));
            var resources = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(ResourcesPath()))!;
            const string key = "command.start.host_only";
            Assert.Equal(resources[resourceLanguage][key], text.Format(session, key));
            Assert.False(text.TryFormat(session, "missing.key", out _));
            var sink = new RoomFlowTests.Sink<ServerNotice>();
            await new RoomNotices(sink, text).SendAsync(session, key, null, default);
            Assert.Equal(resources[resourceLanguage][key], Assert.Single(sink.To(session)).Text);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task RoomModeBroadcastFormatsSeparatelyForUsersOfTheSameClientBuild()
    {
        var (text, context) = await ActivateAsync();
        var rig = new RoomFlowTests.Rig();
        try
        {
            var host = RoomFlowTests.Rig.Player(711, "host");
            var guest = RoomFlowTests.Rig.Player(712, "guest");
            using var hostLanguage = host.RegisterFeature<ISessionLanguage>(new UserLanguage("zh-CN"));
            using var guestLanguage = guest.RegisterFeature<ISessionLanguage>(new UserLanguage("en"));
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            var sink = new RoomFlowTests.Sink<ServerNotice>();
            await new RoomNotices(sink, text).AnnounceModeAsync(rig.Registry.TryGet(host.Id)!, 8, default);
            foreach (var session in new[] { host, guest })
                Assert.Equal(text.Format(session, "room.mode.changed", new Dictionary<string, string> { ["mode"] = text.Format(session, "mode.8") }),
                    Assert.Single(sink.To(session)).Text);
            Assert.NotEqual(sink.To(host)[0].Text, sink.To(guest)[0].Text);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    private sealed record UserLanguage(string LauncherLanguage) : ISessionLanguage;

    [Fact]
    public async Task AutomaticStartNoticeUsesTheReceivingUsersLauncherLanguage()
    {
        var (text, context) = await ActivateAsync();
        var rig = new RoomFlowTests.Rig(options: new InGameOptions
        {
            AutoStartModeIds = [16], AutoStartTimer = true, AutoStartDelayMs = 65000,
        }, texts: text);
        try
        {
            var host = RoomFlowTests.Rig.Player(721, "host");
            using var language = host.RegisterFeature<ISessionLanguage>(new UserLanguage("zh-CN"));
            await rig.Flow.HandleCreateAsync(host, new("boss", "", 16, "rel_ZombiePVE_01"), default);
            var arguments = new Dictionary<string, string> { ["seconds"] = "60", ["minutes"] = "1" };
            Assert.Equal(text.Format(host, "room.auto_start.created", arguments), Assert.Single(rig.Notices.To(host)).Text);
            Assert.NotEqual(text.Format(host.Profile, "room.auto_start.created", arguments), rig.Notices.To(host)[0].Text);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Every language of the shipped file parses and every mode key is a numeric id.</summary>
    [Fact]
    public void ShippedFileParsesWithNumericModeKeys()
    {
        var languages = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(ResourcesPath()))!;
        Assert.Contains("zh-Hans:cn-774", languages.Keys);
        foreach (var (language, messages) in languages)
            foreach (var key in messages.Keys.Where(key => key.StartsWith("mode.", StringComparison.Ordinal) && key != "mode.unnamed"))
                Assert.True(int.TryParse(key.AsSpan(5), out _), $"{language}: {key}");
    }

    /// <summary>Each region reads the names its own clients show, including the renamed survival modes.</summary>
    [Fact]
    public async Task RegionsUseTheirOfficialNames()
    {
        var (text, context) = await ActivateAsync();
        try
        {
            Assert.Equal("热血个人模式", Name(text, "cn-717", 13));
            Assert.Equal("终极战区", Name(text, "cn-717", 30));
            Assert.Equal("生存模式", Name(text, "cn-774", 23));
            Assert.Equal("熱血模式(個人)", Name(text, "tw-454", 13));
            Assert.Equal("最終區域", Name(text, "tw-454", 30));
            Assert.Equal("서바이벌개인전", Name(text, "kr-812", 13));
            Assert.Equal("생존모드", Name(text, "kr-1256", 30));
            Assert.Equal("혼자놀기", Name(text, "kr-812", 15));
            Assert.Equal("Single Play", Name(text, "id-1809", 15));
            Assert.Equal("Final Zone", text.Format("us", "mode.30"));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A build language holds only the differing wording and the rest comes from the region.</summary>
    [Fact]
    public async Task BuildLanguageOverridesOnlyWhatDiffers()
    {
        var (text, context) = await ActivateAsync();
        try
        {
            Assert.Equal("星星争夺战", Name(text, "cn-774", 6));
            Assert.Equal("夺星团队战", Name(text, "cn-717", 6));
            Assert.Equal("夺星个人战", Name(text, "cn-774", 5));
            Assert.Equal("Main Sendiri", Name(text, "id-400", 15));
            Assert.Equal("Single Play", Name(text, "id-1809", 15));
            Assert.Equal("Sniper Mode", Name(text, "id-400", 12));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A mode with no name in the client's language falls back to English; a mode with no name anywhere is reported missing.</summary>
    [Fact]
    public async Task MissingNamesFallBackToEnglishThenReportMissing()
    {
        var (text, context) = await ActivateAsync();
        try
        {
            Assert.Equal("Psychic Top View", Name(text, "tw-454", 37));
            Assert.Equal("Zombie Battle G", Name(text, "cn-774", 43));
            Assert.False(text.TryFormat(Profile("kr-812"), "mode.10", out _));
            Assert.False(text.TryFormat("kr", "mode.2", out _));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A profile language that the resource file does not define is a configuration error.</summary>
    [Fact]
    public async Task UnknownProfileLanguageIsRejected() =>
        await Assert.ThrowsAsync<ConfigException>(async () => await ActivateAsync("missing-language"));
}
