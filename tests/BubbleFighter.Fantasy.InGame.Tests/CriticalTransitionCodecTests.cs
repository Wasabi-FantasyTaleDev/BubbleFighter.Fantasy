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

/// <summary>Pins the audited live critical inputs without changing the peer payload or enabling local-roll descendants.</summary>
public sealed class CriticalTransitionCodecTests
{
    /// <summary>All five seeded clients share the audited sixteen-byte weapon report and twenty-two-byte effect trigger.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    public void NativeWidthsFieldsAndOpaqueTimingBitsArePinned(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var weapon = Convert.FromHexString("1F0C00000100000040420F00785634FE");
        Assert.Equal(16, weapon.Length);
        var decoded = RoundFeedBinding.ReadCriticalTransition(tables, build, 32800, weapon, true);
        Assert.Equal(new RoundCriticalWeaponState(1000000, 1, 3103), decoded.CriticalWeaponState);
        Assert.Equal(new[] { 1000000 }, decoded.ReportedActors);
        Assert.Equal(weapon, decoded.Payload.ToArray());
        Assert.Equal(unchecked((int)0xFE345678), PacketCodec.ReadScalars(tables, build, "CriticalWeaponState", weapon)["unknown_16"]);
        foreach (var first in new byte[] { 0, 1, 0xE7 })
        foreach (var last in new byte[] { 0, 1, 0xE7 })
        {
            var trigger = Trigger(first, last);
            Assert.Equal(22, trigger.Length);
            var effect = RoundFeedBinding.ReadCriticalTransition(tables, build, 32795, trigger, false);
            Assert.Equal(first != 0 && last != 0 ? 1100000 : (int?)null, effect.CriticalEffectActor);
            Assert.Equal(new[] { 1000000 }, effect.ReportedActors);
            Assert.Equal(trigger, effect.Payload.ToArray());
        }
        foreach (var (body, isWeapon) in new[] { (weapon, true), (Trigger(1, 1), false) })
        {
            Assert.Throws<PacketFormatException>(() => RoundFeedBinding.ReadCriticalTransition(tables, build, 1, body[..^1], isWeapon));
            Assert.Throws<PacketFormatException>(() => RoundFeedBinding.ReadCriticalTransition(tables, build, 1, [.. body, 0], isWeapon));
        }
        foreach (var slot in new[] { -1, 5 })
        {
            BitConverter.GetBytes(slot).CopyTo(weapon, 4);
            Assert.Throws<PacketFormatException>(() => RoundFeedBinding.ReadCriticalTransition(tables, build, 1, weapon, true));
        }
        foreach (var offset in new[] { 9, 13, 17 })
        {
            var trigger = Trigger(1, 1);
            BitConverter.GetBytes(float.NaN).CopyTo(trigger, offset);
            Assert.Throws<PacketFormatException>(() => RoundFeedBinding.ReadCriticalTransition(tables, build, 1, trigger, false));
        }
    }

    /// <summary>The named game and relay handlers preserve origin and payload, and unload removes both registrations.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    public async Task ActualProfileRoutesBothNativeInputsAndOwnsTheirLifetime(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var opcodes = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var game = opcodes.ForConnection(OpcodeConnection.Game);
        var relay = opcodes.ForConnection(OpcodeConnection.Relay);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("feed");
        var feeds = new List<RoundFeedSent>();
        var session = new RecordingSession(7, opcodes.Profile);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            infrastructure.Effect(services.Get<IMessageRouter>().For(infrastructure).On<RoundFeedSent>(build + "/game", (_, feed, _) =>
            {
                feeds.Add(feed);
                return ValueTask.CompletedTask;
            }));
            new RoundFeedBinding(build + "/game", build + "/relay", game, relay, tables, build)
                { AcceptRelayFrames = true }.Mount(binding);
            foreach (var onRelay in new[] { false, true })
            foreach (var (name, body) in new[]
            {
                ("gppUpdateWeaponInfo", Convert.FromHexString("1F0C00000100000040420F00785634FE")),
                ("gppKicked", Trigger(1, 1)),
            })
            {
                var protocol = build + (onRelay ? "/relay" : "/game");
                var opcode = (onRelay ? relay : game).C2S(name);
                var count = feeds.Count;
                await services.Get<IPacketRouter>().DispatchAsync(protocol, session, new(opcode, body), default);
                Assert.Equal(count + 1, feeds.Count);
                Assert.Equal(onRelay, feeds[^1].OnRelay);
                Assert.Equal(relay.S2C(name), feeds[^1].Code);
                Assert.Equal(body, feeds[^1].Payload.ToArray());
            }
            await binding.UnwindAsync();
            foreach (var onRelay in new[] { false, true })
                await services.Get<IPacketRouter>().DispatchAsync(build + (onRelay ? "/relay" : "/game"), session,
                    new((onRelay ? relay : game).C2S("gppKicked"), Trigger(1, 1)), default);
            Assert.Equal(4, feeds.Count);
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
        }
        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = id, Source = "test" }, services, events, NullLogger.Instance);
    }

    /// <summary>Layout inheritance never grants a server-side critical bonus to a build that already rolls locally.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-717")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void LocallyRollingBuildIsNotAnEnabledCriticalTransitionMember(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var members = tables.ValuesOf(build).GetValueOrDefault("weapon_critical_seed_builds") as string[] ?? [];
        Assert.DoesNotContain(build, members);
    }

    private static byte[] Trigger(byte first, byte last)
    {
        var body = Convert.FromHexString("0140420F00E0C810000000A03F000020C00000704001");
        body[0] = first;
        body[^1] = last;
        return body;
    }
}
