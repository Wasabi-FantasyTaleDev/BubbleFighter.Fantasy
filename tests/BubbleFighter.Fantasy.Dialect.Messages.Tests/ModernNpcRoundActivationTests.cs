using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Activates the modern room plugin to check that ordinary NPC rounds mount without the tutorial.</summary>
public sealed class ModernNpcRoundActivationTests
{
    /// <summary>
    /// <c>npc_rounds</c> alone publishes the shared NPC routes and sender; the tutorial's teaching-only
    /// binding stays unmounted, and everything unloads with the row.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", true)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", true)]
    [InlineData("tw-454", false)]
    public async Task NpcRoundsMountTheSharedBindingIndependentlyOfTheTutorial(string build, bool npcRounds)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["relay_protocol"] = build + "/relay", ["npc_rounds"] = npcRounds,
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var game = table.ForConnection(OpcodeConnection.Game);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            foreach (var request in new[] { "gcpReqStartNewWave", "gcpReqWaveAllSpawnMobs", "gppCreateMobsFromSpawnGroup" })
                Assert.Equal(npcRounds, fixture.Packets.Handlers.ContainsKey((build + "/game", game.C2S(request))));
            {
                var relay = table.ForConnection(OpcodeConnection.Relay);
                foreach (var request in new[] { "gppCreateMobsFromSpawnGroup", "gppCreateSpawnGroupMemberMob",
                    "gppCreateAIObject", "gppHordeMobReplay", "gppWaveSetMobSpawnGroup" })
                    Assert.Equal(npcRounds, fixture.Packets.Handlers.ContainsKey((build + "/relay", relay.C2S(request))));
                Assert.Null(relay.Find(PacketDirection.ClientToServer, "gcpReqWaveAllSpawnMobs"));
                Assert.Null(relay.Find(PacketDirection.ServerToClient, "gspResetWaveMobs_"));
            }
            Assert.Equal(npcRounds ? 1 : 0, fixture.Services.GetAll<IMessageSender<RoundNpcAcknowledged>>().Count);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<InitialTutorialNpcAcknowledged>>());
            if (game.Find(PacketDirection.ClientToServer, "gcpFailTutorialWaves_") is { } tutorialFailure)
                Assert.False(fixture.Packets.Handlers.ContainsKey((build + "/game", tutorialFailure)));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
    }

    /// <summary>Shipped profiles enable only the audited ordinary NPC exchanges without enabling automatic teaching.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public void ShippedModernProfilesKeepNpcActivationExplicit(string build, bool enabled)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var composer = new ProfileComposer(Path.Combine(root, "profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        Assert.Equal(enabled, options.NpcRounds);
        Assert.True(options.ModernRooms);
        Assert.False(options.WaitingRoomsOnly);
        Assert.False(options.InitialTutorial);
    }
}
