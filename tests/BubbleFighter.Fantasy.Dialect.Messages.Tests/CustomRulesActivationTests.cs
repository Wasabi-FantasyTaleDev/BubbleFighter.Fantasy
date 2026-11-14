using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks the complete room plugin rejects unsupported custom-rule activations before publishing any room routes.</summary>
public sealed class CustomRulesActivationTests
{
    /// <summary>A packet layout alone cannot activate custom options without the audited native feature and workbook.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("kr-812")]
    public async Task UnavailableNativeFeatureFailsBeforeAnyRoomRegistration(string build)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["waiting_rooms_only"] = true, ["custom_room_rules"] = true,
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("custom room", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomEntered>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Plaza, menu and browser-only rows cannot silently accept an option requiring a waiting room.</summary>
    [Theory]
    [InlineData("plaza_only")]
    [InlineData("menu_only")]
    [InlineData("browser_only")]
    public async Task IncompatibleRoomSurfaceFailsBeforeMount(string option)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes("kr-1256"));
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["custom_room_rules"] = true, [option] = true,
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("custom_room_rules", error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Preflight validates both symbolic directions before unrelated modern room routes can be installed.</summary>
    [Theory]
    [InlineData("gcpCustomGameModeSetting_", true)]
    [InlineData("gspCustomGameModeSetting", false)]
    public async Task IncompleteOpcodeEvidenceFailsBeforeMount(string name, bool remove)
    {
        var full = TestBuilds.Opcodes("kr-1256");
        var table = new OpcodeTable(full.Profile, full.All.Where(row => !remove || row.Name != name)
            .Select(row => row.Name == name ? row with { Evidence = Evidence.Inferred } : row));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["waiting_rooms_only"] = true, ["custom_room_rules"] = true,
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>());
        }
        finally { await context.UnwindAsync(); }
    }
}
