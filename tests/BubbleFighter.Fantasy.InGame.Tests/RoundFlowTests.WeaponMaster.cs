using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private const int WeaponMasterMode = 26;

    /// <summary>
    /// The round hands every seat its first weapon when it starts and raises a level on the configured kill; the
    /// victim gets a new weapon only when its client asks after enough deaths, and no seat at the last level means no end.
    /// </summary>
    [Fact]
    public async Task WeaponMasterAssignsStartLevelUpAndRequestedRevivalWeapons()
    {
        var rig = new Rig(configure: options => options.NativeLifeEveryMode = true);
        var sent = new Sink<RoundEquipmentApplied>();
        var rules = WeaponMaster(levels: 3, noScoreDeathCount: 1);
        rig.Round.ConfigureWeaponMaster(rules, sent, static () => new Random(1));
        try
        {
            var (room, host, guest) = await StartWeaponMasterAsync(rig);
            foreach (var member in new ISession[] { rig.Host, rig.Guest })
                Assert.Equal([(host.Slot, 0, 100, 1), (guest.Slot, 0, 100, 1)], Frames(sent.To(member)));

            await KillAsync(rig, host, guest);
            foreach (var member in new ISession[] { rig.Host, rig.Guest })
                Assert.Equal([(host.Slot, 0, 200, 2)], Frames(sent.To(member).Skip(2)));

            await rig.Round.HandleWeaponMasterRevivalAsync(rig.Host, new(), default);
            Assert.Equal(3, sent.To(rig.Host).Length);
            await rig.Round.HandleWeaponMasterRevivalAsync(rig.Guest, new(), default);
            foreach (var member in new ISession[] { rig.Host, rig.Guest })
                Assert.Equal([(guest.Slot, 0, 100, 3)], Frames(sent.To(member).Skip(3)));
            await rig.Round.HandleWeaponMasterRevivalAsync(rig.Guest, new(), default);
            Assert.Equal(4, sent.To(rig.Host).Length);
            lock (room.Gate)
            {
                Assert.Equal(RoomPhase.Playing, room.Phase);
                Assert.Equal(2, room.WeaponMaster!.Level(host.Slot));
                Assert.Equal(1, room.WeaponMaster.Level(guest.Slot));
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A death no kill names still counts, once, however many reports describe it.</summary>
    [Fact]
    public async Task WeaponMasterCountsAnUnattributedDeathOncePerLife()
    {
        var rig = new Rig(configure: options => options.NativeLifeEveryMode = true);
        var sent = new Sink<RoundEquipmentApplied>();
        var rules = WeaponMaster(levels: 1, noScoreDeathCount: 2);
        rig.Round.ConfigureWeaponMaster(rules, sent, static () => new Random(1));
        try
        {
            var (room, _, guest) = await StartWeaponMasterAsync(rig);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            Assert.Equal(2, sent.To(rig.Host).Length);

            for (var life = 0; life < 2; life++)
            {
                await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default);
                await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default);
                await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
                if (life == 0)
                {
                    await rig.Round.HandleWeaponMasterRevivalAsync(rig.Guest, new(), default);
                    Assert.Equal(2, sent.To(rig.Host).Length);
                    lock (room.Gate) guest.Life.Revive();
                }
            }
            Assert.Equal(2, sent.To(rig.Host).Length);
            await rig.Round.HandleWeaponMasterRevivalAsync(rig.Guest, new(), default);
            Assert.Equal((guest.Slot, 0, 100, 3), Frames(sent.To(rig.Host).Skip(2)).Single());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The first seat to reach the last level wins at once, without waiting for the clock.</summary>
    [Fact]
    public async Task WeaponMasterTheLastLevelWinsAndEndsTheRound()
    {
        var rig = new Rig(configure: options => options.NativeLifeEveryMode = true);
        var sent = new Sink<RoundEquipmentApplied>();
        rig.Round.ConfigureWeaponMaster(WeaponMaster(levels: 2, noScoreDeathCount: 2), sent, static () => new Random(1));
        try
        {
            var (_, host, guest) = await StartWeaponMasterAsync(rig);
            await KillAsync(rig, host, guest);
            Assert.Equal((host.Slot, 0, 200, 2), Frames(sent.To(rig.Host)).Last());
            var ended = await WaitEndedAsync(rig, rig.Host);
            Assert.Equal(RoundEndReason.WinCondition, ended.Reason);
            Assert.Equal(RoundOutcome.Won, ended.Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>When the clock runs out first, the seat at the highest level wins.</summary>
    [Fact]
    public async Task WeaponMasterHighestLevelWinsAtTheClock()
    {
        var rig = new Rig(timeLimitMs: 400, configure: options => options.NativeLifeEveryMode = true);
        var rules = WeaponMaster(levels: 3, noScoreDeathCount: 2);
        rig.Round.ConfigureWeaponMaster(rules, new Sink<RoundEquipmentApplied>(), static () => new Random(1));
        try
        {
            var (_, host, guest) = await StartWeaponMasterAsync(rig);
            await KillAsync(rig, host, guest);
            var ended = await WaitEndedAsync(rig, rig.Host);
            Assert.Equal(RoundEndReason.TimeLimit, ended.Reason);
            Assert.Equal(RoundOutcome.Won, ended.Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Seats that end the round at the same level draw.</summary>
    [Fact]
    public async Task WeaponMasterEqualLevelsDrawAtTheClock()
    {
        var rig = new Rig(timeLimitMs: 300, configure: options => options.NativeLifeEveryMode = true);
        var rules = WeaponMaster(levels: 1, noScoreDeathCount: 2);
        rig.Round.ConfigureWeaponMaster(rules, new Sink<RoundEquipmentApplied>(), static () => new Random(1));
        try
        {
            await StartWeaponMasterAsync(rig);
            Assert.Equal(RoundOutcome.Draw, (await WaitEndedAsync(rig, rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Draw, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Another mode, and a row without the weapon logic, hand out nothing.</summary>
    [Fact]
    public async Task WeaponMasterStaysOutOfOtherModes()
    {
        var rig = new Rig(configure: options => options.NativeLifeEveryMode = true);
        var sent = new Sink<RoundEquipmentApplied>();
        rig.Round.ConfigureWeaponMaster(WeaponMaster(levels: 1, noScoreDeathCount: 2), sent);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.ModeId = 4;
            await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
            await rig.WaitStartedAsync(rig.Guest);
            await rig.Round.HandleWeaponMasterRevivalAsync(rig.Guest, new(), default);
            Assert.Empty(sent.To(rig.Host));
            lock (room.Gate) Assert.Null(room.WeaponMaster);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static async Task<(PlayRoom Room, RoomSeat Host, RoomSeat Guest)> StartWeaponMasterAsync(Rig rig)
    {
        await rig.StartAsync();
        var room = rig.Registry.Find(rig.RoomId)!;
        lock (room.Gate) room.ModeId = WeaponMasterMode;
        await rig.Round.HandleLoadReportedAsync(rig.Host, new RoundLoadReported(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new RoundLoadReported(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Guest);
        var host = room.FindByGame(rig.Host.Id)!;
        var guest = room.FindByGame(rig.Guest.Id)!;
        lock (room.Gate)
        {
            host.Team = 1;
            guest.Team = 2;
        }
        return (room, host, guest);
    }

    private static async Task KillAsync(Rig rig, RoomSeat killer, RoomSeat victim)
    {
        await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, RoomRegistry.SceneEntity(victim.Slot))
            { Initiator = RoomRegistry.SceneEntity(killer.Slot) }, default);
        await rig.Round.HandleLifeReportedAsync(rig.Host, new(RoundLifeEvent.Finished, RoomRegistry.SceneEntity(victim.Slot))
            { Initiator = RoomRegistry.SceneEntity(killer.Slot) }, default);
    }

    private static (int Seat, int Slot, int Item, int Reason)[] Frames(IEnumerable<RoundEquipmentApplied> frames) =>
        [.. frames.Select(frame => (frame.Seat, Assert.Single(frame.Slots), Assert.Single(frame.Items), frame.Reason))];

    // Levels 100 experience apart with weapon 100, 200, ...; a kill gives 100.
    private static WeaponMasterRules WeaponMaster(int levels, int noScoreDeathCount) =>
        new([.. Enumerable.Range(1, levels).Select(level => (IReadOnlyList<WeaponMasterWeapon>)[new(level * 100, 1)])],
            [.. Enumerable.Range(0, levels).Select(level => level * 100)],
            new Dictionary<WeaponMasterEvent, int> { [WeaponMasterEvent.Kill] = 100 }, noScoreDeathCount);
}
