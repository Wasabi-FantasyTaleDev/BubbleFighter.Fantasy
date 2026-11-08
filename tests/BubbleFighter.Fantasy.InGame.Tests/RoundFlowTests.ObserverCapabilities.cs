using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Every denied mode must be supported; a supported first entry cannot hide an unsupported later entry.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(20, false)]
    [InlineData(1, true)]
    [InlineData(20, true)]
    public void RespawnDenialRejectsUnsupportedObservationMode(int mode, bool alsoBomb)
    {
        var error = Assert.Throws<ConfigException>(() => new Rig(observation: true, observationModes: [19],
            configure: options => options.RespawnDisabledModeIds = alsoBomb ? [19, mode] : [mode]));
        Assert.Contains("respawn_disabled_mode_ids", error.Message, StringComparison.Ordinal);
        Assert.Contains($"mode {mode},", error.Message, StringComparison.Ordinal);
        Assert.Contains("test/game", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Capability alone changes no rule, while explicit supported exclusions remain generic across modes.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(19)]
    public async Task ObservationCapabilitiesDoNotChooseRespawnPolicy(int mode)
    {
        var rig = new Rig(observation: true, observationModes: mode == 0 ? [] : [mode],
            configure: options => options.RespawnDisabledModeIds = mode == 0 ? [] : [mode]);
        await rig.Registry.StopAllAsync();
    }

    /// <summary>Selection is protocol-scoped, refuses duplicate audited senders and ignores plain message senders.</summary>
    [Fact]
    public async Task ObservationResolutionRequiresOneMatchingAuditedSender()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "observer-test", Plugin = "ingame", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            context.Effect(services.RegisterMany<IMessageSender<RoundPlayerObserved>>(new Sink<RoundPlayerObserved>()));
            context.Effect(services.RegisterMany<IRoundObservationSender>(new ObservationSink([19], "other/game")));
            Assert.Null(RoundSenders.ResolveObservation(context, "test/game"));
            var matching = new ObservationSink([4]);
            context.Effect(services.RegisterMany<IRoundObservationSender>(matching));
            Assert.Same(matching, RoundSenders.ResolveObservation(context, "test/game"));
            context.Effect(services.RegisterMany<IRoundObservationSender>(new ObservationSink([19])));
            var error = Assert.Throws<ConfigException>(() => RoundSenders.ResolveObservation(context, "test/game"));
            Assert.Contains("2 round observation senders", error.Message, StringComparison.Ordinal);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(services.GetAll<IRoundObservationSender>());
    }
}
