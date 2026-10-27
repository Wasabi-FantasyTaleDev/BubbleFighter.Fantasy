using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the kr-1256 S2C 585 words per recipient: the configured relay words normally, and both relay selections
/// cleared for a seat that plays over its game connection (kr-1256 sub_9E1EB0 case 0x249, sub_95B910).
/// </summary>
public sealed class ModernRoundTransportTests
{
    [Theory]
    [InlineData(false, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 })]
    [InlineData(true, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 })]
    public async Task LoadingWordsFollowTheSeatsMatchLink(bool overGame, byte[] expected)
    {
        var root = FindRoot();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        PluginContext Context(string id) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "test" }, services, events, NullLogger.Instance);
        var router = Context("router");
        var messages = Context("messages");
        var binding = Context("binding");
        try
        {
            await new PacketRouterPlugin().ActivateAsync(router, default);
            await new MessageRouterPlugin().ActivateAsync(messages, default);
            var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
            new RoundStartBinding("kr-1256/game", table, PacketTables.Load(Path.Combine(root, "packets")), "kr-1256",
                [1, 0, 1], NullLogger.Instance).Mount(binding);
            var session = new RecordingSession(7);
            await services.GetAll<IMessageSender<RoundCountdownStarted>>().Single()
                .SendAsync(session, new RoundCountdownStarted(1, 7000) { MatchOverGame = overGame }, default);
            Assert.Equal(expected, session.Body(table.S2C("gspGameEverybodyLoadingComplete")));
        }
        finally
        {
            await binding.UnwindAsync();
            await messages.UnwindAsync();
            await router.UnwindAsync();
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
