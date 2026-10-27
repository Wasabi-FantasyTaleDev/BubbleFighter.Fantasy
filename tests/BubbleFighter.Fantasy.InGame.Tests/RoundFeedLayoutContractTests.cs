using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Rejects table edits that would make the relay interpret a non-identity field as an actor.</summary>
public sealed class RoundFeedLayoutContractTests
{
    /// <summary>A reporting actor must remain a full integer word before any handler can be registered.</summary>
    [Theory]
    [InlineData("u16")]
    [InlineData("f32")]
    public void WrongActorTypeFailsAtMount(string type)
    {
        var temporary = Directory.CreateTempSubdirectory("round-feed-contract-");
        var directory = temporary.FullName;
        File.WriteAllText(Path.Combine(directory, "builds.yaml"), "version: 1\nbuilds:\n  kr-1256:\n    round_feed:\n      Dash: { opcode: Unknown32779, structure: Dash, actors: [actor] }\n");
        File.WriteAllText(Path.Combine(directory, "feed.yaml"),
            $"version: 1\nstructs:\n  RoundResetConfirmation:\n    - actor: u32\n  Dash:\n    - actor: {type}\n      offset: 0\n");
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "feed", Plugin = "feed", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var table = OpcodeTableLoader.Load(Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
            var binding = new RoundFeedBinding("kr-1256/game", "kr-1256/relay", table, table.ForConnection(OpcodeConnection.Relay),
                PacketTables.Load(directory), "kr-1256");
            var error = Assert.Throws<ConfigException>(() => binding.Mount(context));
            Assert.Contains("Dash.actor as u32", error.Message);
            Assert.Contains($"table states {type}", error.Message);
        }
        finally
        {
            context.Unwind();
            temporary.Delete(recursive: true);
        }
    }
}
