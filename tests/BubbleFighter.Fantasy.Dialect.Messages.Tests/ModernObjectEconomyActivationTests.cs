using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Activates the modern room plugin to check optional shared economy routes and their unload ownership.</summary>
public sealed class ModernObjectEconomyActivationTests
{
    /// <summary>Audited Lucci and star options publish real routes through the complete modern plugin.</summary>
    [Theory]
    [InlineData("kr-1256", "lucci_drops", "gcpEatLucci")]
    [InlineData("kr-1256", "star_items", "gcpEatStar")]
    public async Task EnabledAuditedOptionMountsAndUnloads(string build, string option, string request)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["relay_protocol"] = build + "/relay", [option] = true,
            ["result_scoreboard_modes"] = new[] { 1, 5, 6 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Contains((build + "/game", table.ForConnection(OpcodeConnection.Game).C2S(request)), fixture.Packets.Handlers.Keys);
            if (option == "lucci_drops")
                Assert.Single(fixture.Services.GetAll<IMessageSender<RoundLucciCreated>>());
            else
            {
                Assert.Single(fixture.Services.GetAll<IMessageSender<RoundStarCreated>>());
                Assert.Single(fixture.Services.GetAll<IMessageSender<RoundStarFeverStarted>>());
                Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundStarReported>>());
                Assert.Contains((build + "/game", table.ForConnection(OpcodeConnection.Game).C2S("gcpGameUpdateScoreOptimze")),
                    fixture.Packets.Handlers.Keys);
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLucciCreated>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundStarCreated>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundStarFeverStarted>>());
    }

    /// <summary>Star items refuse a modern result configuration that would end star rounds without a table.</summary>
    [Fact]
    public async Task StarItemsRequireTheStarScoreboardModes()
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256"));
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["relay_protocol"] = "kr-1256/relay", ["star_items"] = true,
            ["result_scoreboard_modes"] = new[] { 1, 5 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var failure = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("result_scoreboard_modes", failure.Message);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundStarCreated>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Default modern profiles do not publish optional object creation senders.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public async Task DefaultOptionsKeepEconomyOff(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        using var fixture = new MatchMessagesMountTests.Fixture(OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["relay_protocol"] = build + "/relay",
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundLucciCreated>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundStarCreated>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundSupplyCreated>>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }
}
