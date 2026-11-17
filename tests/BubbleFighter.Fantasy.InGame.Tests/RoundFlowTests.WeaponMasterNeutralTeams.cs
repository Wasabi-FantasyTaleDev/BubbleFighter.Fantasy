using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Actual mode26 entry keeps neutral teams while victim-owned native evidence advances the attacker's weapon once.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeaponMasterNeutralTeamsNativeKillAdvancesOnce(bool directDeath)
    {
        var (rig, context, weapons) = NeutralWeaponMasterRig();
        try
        {
            var (room, host, guest) = await StartNeutralWeaponMasterAsync(rig);
            var victim = RoomRegistry.SceneEntity(guest.Slot);
            var attacker = RoomRegistry.SceneEntity(host.Slot);
            if (!directDeath)
                await rig.Round.HandleLifeReportedAsync(rig.Guest,
                    new(RoundLifeEvent.Bubbled, victim) { Initiator = attacker }, default);
            var killed = new RoundLifeReported(directDeath ? RoundLifeEvent.Died : RoundLifeEvent.Finished, victim)
                { Initiator = attacker };
            var reporter = directDeath ? rig.Guest : rig.Host;
            await rig.Round.HandleLifeReportedAsync(reporter, killed, default);
            await rig.Round.HandleLifeReportedAsync(reporter, killed, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host,
                new(host.Slot) { Victim = guest.Slot, HasVictimReference = true }, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host,
                new(host.Slot) { Victim = guest.Slot, HasVictimReference = true }, default);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(directDeath ? 0 : 1, room.Scoring.Stats(host.Slot).Knockdowns);
            Assert.Equal(100, room.WeaponMaster!.Progress(host.Slot).Experience);
            Assert.Equal(2, room.WeaponMaster.Level(host.Slot));
            Assert.Equal([(host.Slot, 0, 200, 2)], Frames(weapons.To(rig.Host).Skip(2)));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A legacy finishing confirmation and the victim's death report share one life credit in either arrival order.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeaponMasterNeutralTeamsDeathAndScoreOrderShareCredit(bool nativeFirst)
    {
        var (rig, context, weapons) = NeutralWeaponMasterRig();
        try
        {
            var (room, host, guest) = await StartNeutralWeaponMasterAsync(rig);
            var death = new RoundLifeReported(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot))
                { Initiator = RoomRegistry.SceneEntity(host.Slot) };
            var score = new RoundFinishingBlowReported(host.Slot) { Victim = guest.Slot, HasVictimReference = true };
            if (nativeFirst) await rig.Round.HandleLifeReportedAsync(rig.Guest, death, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, score, default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, death, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, score, default);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(guest.Slot).Deaths);
            Assert.Equal(100, room.WeaponMaster!.Progress(host.Slot).Experience);
            Assert.Equal(3, weapons.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Legacy knockdown reports require the shared bubble and cannot duplicate its configured experience grant.</summary>
    [Fact]
    public async Task WeaponMasterNeutralTeamsKnockdownCreditRequiresOneNativeBubble()
    {
        var (rig, context, weapons) = NeutralWeaponMasterRig(knockdownExperience: true);
        try
        {
            var (room, host, guest) = await StartNeutralWeaponMasterAsync(rig);
            var score = new RoundKillCreditReported(host.Slot, guest.Slot, 1);
            await rig.Round.HandleKillCreditReportedAsync(rig.Host, score, default);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Knockdowns);
            Assert.Equal(0, room.WeaponMaster!.Progress(host.Slot).Experience);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Bubbled, RoomRegistry.SceneEntity(guest.Slot))
                { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default);
            await rig.Round.HandleKillCreditReportedAsync(rig.Host, score, default);
            await rig.Round.HandleKillCreditReportedAsync(rig.Host, score, default);
            Assert.Equal(1, room.Scoring.Stats(host.Slot).Knockdowns);
            Assert.Equal(100, room.WeaponMaster.Progress(host.Slot).Experience);
            Assert.Equal(3, weapons.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Self, forged-reporter and absent-victim confirmations cannot invent a WeaponMaster kill or level.</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("foreign-reporter")]
    [InlineData("missing-victim")]
    [InlineData("unknown-victim")]
    public async Task WeaponMasterNeutralTeamsRejectInvalidScoreConfirmations(string kind)
    {
        var (rig, context, weapons) = NeutralWeaponMasterRig();
        try
        {
            var (room, host, guest) = await StartNeutralWeaponMasterAsync(rig);
            var report = kind switch
            {
                "self" => new RoundFinishingBlowReported(host.Slot) { Victim = host.Slot, HasVictimReference = true },
                "foreign-reporter" => new RoundFinishingBlowReported(guest.Slot) { Victim = guest.Slot, HasVictimReference = true },
                "missing-victim" => new RoundFinishingBlowReported(host.Slot),
                "unknown-victim" => new RoundFinishingBlowReported(host.Slot) { Victim = 31, HasVictimReference = true },
                _ => throw new InvalidOperationException("Unknown score-confirmation test case."),
            };
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, report, default);
            Assert.All(room.Seats, seat =>
            {
                Assert.Equal(SeatLife.Alive, seat.Life.State);
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Kills);
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Deaths);
                Assert.Equal(0, room.WeaponMaster!.Progress(seat.Slot).Experience);
            });
            Assert.Equal(2, weapons.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Neither a self-attributed native death nor a forged knockdown scorer can earn offensive experience.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeaponMasterNeutralTeamsRejectSelfAndForgedKnockdown(bool forged)
    {
        var (rig, context, weapons) = NeutralWeaponMasterRig(knockdownExperience: true);
        try
        {
            var (room, host, guest) = await StartNeutralWeaponMasterAsync(rig);
            var actor = RoomRegistry.SceneEntity(host.Slot);
            if (forged)
            {
                await rig.Round.HandleLifeReportedAsync(rig.Guest,
                    new(RoundLifeEvent.Bubbled, RoomRegistry.SceneEntity(guest.Slot)), default);
                await rig.Round.HandleKillCreditReportedAsync(rig.Host, new(guest.Slot, guest.Slot, 1), default);
            }
            else
            {
                await rig.Round.HandleLifeReportedAsync(rig.Host,
                    new(RoundLifeEvent.Bubbled, actor) { Initiator = actor }, default);
                await rig.Round.HandleKillCreditReportedAsync(rig.Host, new(host.Slot, host.Slot, 1), default);
            }
            await rig.Round.HandleLifeReportedAsync(rig.Host,
                new(RoundLifeEvent.Died, actor) { Initiator = actor }, default);
            Assert.All(room.Seats, seat =>
            {
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Kills);
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Knockdowns);
                Assert.Equal(0, room.WeaponMaster!.Progress(seat.Slot).Experience);
            });
            Assert.Equal(2, weapons.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>The neutral-team exception requires both mounted WeaponMaster rules and mode26.</summary>
    [Theory]
    [InlineData(26, false)]
    [InlineData(4, true)]
    public async Task WeaponMasterNeutralTeamsRemainDisabledOutsideMountedMode(int mode, bool mounted)
    {
        var (rig, context, weapons) = NeutralWeaponMasterRig(mounted: mounted);
        try
        {
            var (room, host, guest) = await StartNeutralWeaponMasterAsync(rig, mode);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Bubbled, RoomRegistry.SceneEntity(guest.Slot))
                { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default);
            await rig.Round.HandleLifeReportedAsync(rig.Host,
                new(RoundLifeEvent.Finished, RoomRegistry.SceneEntity(guest.Slot))
                { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Knockdowns);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Kills);
            Assert.Equal(SeatLife.Bubble, guest.Life.State);
            Assert.Null(room.WeaponMaster);
            Assert.Empty(weapons.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Allowing neutral offensive opponents does not newly authorize neutral-team rescue credit.</summary>
    [Fact]
    public async Task WeaponMasterNeutralTeamsDoNotExpandRescueAffinity()
    {
        var (rig, context, weapons) = NeutralWeaponMasterRig();
        try
        {
            var (room, host, guest) = await StartNeutralWeaponMasterAsync(rig);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, actor), default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Released, actor) { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default);
            await rig.Round.HandleKillCreditReportedAsync(rig.Host,
                new(host.Slot, guest.Slot, 1) { IsRescue = true }, default);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Rescues);
            Assert.Equal(0, room.WeaponMaster!.Progress(host.Slot).Experience);
            Assert.Equal(2, weapons.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    private static async Task<(PlayRoom Room, RoomSeat Host, RoomSeat Guest)> StartNeutralWeaponMasterAsync(Rig rig, int mode = 26)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new("weapon master", "", mode, "map"), default);
        var room = rig.Registry.TryGet(rig.Host.Id)!;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new(room.Id, ""), default);
        await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
        Assert.Equal(new byte[] { 0, 0 }, room.Seats.Select(seat => seat.Team));
        await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
        Assert.Equal(new[] { 0, 0 }, Assert.Single(rig.Starting.To(rig.Host)).Room.Seats.Select(seat => seat.Team));
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        return (room, room.FindByGame(rig.Host.Id)!, room.FindByGame(rig.Guest.Id)!);
    }

    private static (Rig Rig, PluginContext Context, Sink<RoundEquipmentApplied> Weapons) NeutralWeaponMasterRig(
        bool mounted = true, bool knockdownExperience = false)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "weapon-master-neutral", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options => options.NativeLifeEveryMode = true);
        var weapons = new Sink<RoundEquipmentApplied>();
        if (mounted)
        {
            var rules = knockdownExperience
                ? new WeaponMasterRules([[new(100, 1)], [new(200, 1)], [new(300, 1)]], [0, 100, 200],
                    new Dictionary<WeaponMasterEvent, int> { [WeaponMasterEvent.Knockdown] = 100 }, 2)
                : WeaponMaster(levels: 3, noScoreDeathCount: 2);
            rig.Round.ConfigureWeaponMaster(rules, weapons, static () => new Random(1));
        }
        return (rig, context, weapons);
    }
}
