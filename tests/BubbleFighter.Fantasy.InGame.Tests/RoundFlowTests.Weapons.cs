using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private static RoundWeaponDropRequested WeaponDrop() => new(1, -2, 3, 0.5f, 2, -1, 2101, 0x01020304, -1, true);

    /// <summary>The first owned pickup wins; foreign character reports cannot consume another player's object.</summary>
    [Fact]
    public async Task WeaponObjectsReachTheRoundAndPickupRequiresTheReportersOwnCharacter()
    {
        var rig = new Rig();
        _ = await StartedRoundAsync(rig);
        try
        {
            var drop = WeaponDrop();
            await rig.Round.HandleWeaponDropAsync(rig.Host, drop, default);
            var created = Assert.Single(rig.WeaponCreated.To(rig.Host));
            Assert.Equal(created, Assert.Single(rig.WeaponCreated.To(rig.Guest)));
            Assert.Equal(drop, created.Drop);
            Assert.Equal(0x64000001, created.ObjectId);
            await rig.Round.HandleWeaponPickupAsync(rig.Guest, new(created.ObjectId, 1000000, 0), default);
            Assert.Empty(rig.WeaponPicked.To(rig.Host));
            await rig.Round.HandleWeaponPickupAsync(rig.Guest, new(created.ObjectId, 1100000, 1), default);
            var picked = Assert.Single(rig.WeaponPicked.To(rig.Host));
            Assert.Equal(new RoundWeaponPicked(created.ObjectId, 1100000), picked);
            Assert.Equal(picked, Assert.Single(rig.WeaponPicked.To(rig.Guest)));
            await rig.Round.HandleWeaponPickupAsync(rig.Host, new(created.ObjectId, 1000000, 0), default);
            Assert.Single(rig.WeaponPicked.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Picking an object does not reset the per-round allocation budget or recycle its identity.</summary>
    [Fact]
    public async Task WeaponObjectBudgetIsBoundedAcrossPickupsAndClearedAtRoundReset()
    {
        var rig = new Rig(configure: options => options.MaxWeaponObjectsPerRound = 1);
        var room = await StartedRoundAsync(rig);
        try
        {
            await rig.Round.HandleWeaponDropAsync(rig.Host, WeaponDrop(), default);
            var created = Assert.Single(rig.WeaponCreated.To(rig.Host));
            await rig.Round.HandleWeaponPickupAsync(rig.Host, new(created.ObjectId, 1000000, 0), default);
            await rig.Round.HandleWeaponDropAsync(rig.Guest, WeaponDrop(), default);
            Assert.Single(rig.WeaponCreated.To(rig.Host));
            room.ResetToWaiting();
            Assert.Null(room.Weapons);
            await rig.Round.HandleWeaponDropAsync(rig.Host, WeaponDrop(), default);
            Assert.Single(rig.WeaponCreated.To(rig.Host));
            lock (room.Gate)
            {
                room.MarkStarting();
                room.MarkPlaying();
            }
            await rig.Round.HandleWeaponDropAsync(rig.Host, WeaponDrop(), default);
            Assert.Equal(2, rig.WeaponCreated.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Nonmembers and malformed typed drops cannot allocate an object in a live room.</summary>
    [Fact]
    public async Task WeaponObjectsRejectInvalidCoordinatesAndIgnoreNonmembers()
    {
        var rig = new Rig();
        var room = await StartedRoundAsync(rig);
        try
        {
            var outsider = new RecordingSession(99);
            await rig.Round.HandleWeaponDropAsync(outsider, WeaponDrop(), default);
            Assert.Null(room.Weapons);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rig.Round.HandleWeaponDropAsync(rig.Host, WeaponDrop() with { VelocityZ = float.NaN }, default).AsTask());
            Assert.Null(room.Weapons);
            await rig.Round.HandleWeaponPickupAsync(rig.Host, new(0x64000001, 1000000, 0), default);
            Assert.Empty(rig.WeaponPicked.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
