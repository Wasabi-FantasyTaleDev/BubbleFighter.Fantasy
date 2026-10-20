using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the verified mode resolution table (client resolver <c>sub_711210</c>)
/// and the create-time TimeAttackTime overrides (<c>sub_9A0880</c>). Unknown
/// ids must fall to the default family like the client's default branch.
/// </summary>
public sealed class GameModeCatalogTests
{
    [Fact]
    public void BaseModesResolveToDefault()
    {
        Assert.Equal(GameModeFamily.Default, GameMode.Resolve(1).Family);
        Assert.Equal(GameModeFamily.Default, GameMode.Resolve(35).Family);
        Assert.Equal(GameModeFamily.Default, GameMode.Resolve(2).Family);
    }

    [Fact]
    public void UnknownModesFallToDefaultWithNoOverride()
    {
        var mode = GameMode.Resolve(7777);
        Assert.Equal(GameModeFamily.Default, mode.Family);
        Assert.Equal(0, mode.TimeAttackTimeOverrideMs);
    }

    [Fact]
    public void ZombieFamilySpansTheVerifiedIds()
    {
        foreach (var id in new[] { 8, 28, 43 })
        {
            var mode = GameMode.Resolve(id);
            Assert.Equal(GameModeFamily.Zombie, mode.Family);
            Assert.Equal(180_000, mode.TimeAttackTimeOverrideMs);
        }

        var thirtyTwo = GameMode.Resolve(32);
        Assert.Equal(GameModeFamily.Zombie, thirtyTwo.Family);
        Assert.Equal(600_000, thirtyTwo.TimeAttackTimeOverrideMs);
    }

    [Fact]
    public void ZombiePveFamilySpansTheVerifiedIds()
    {
        foreach (var id in new[]
        {
            6, 11, 16, 23, 25, 27, 30, 34, 36, 37, 41, 42, 44, 45,
        })
        {
            Assert.Equal(GameModeFamily.ZombiePve, GameMode.Resolve(id).Family);
        }
    }

    [Fact]
    public void RemainingFamiliesSpanTheVerifiedIds()
    {
        foreach (var id in new[] { 3, 21, 29, 33, 38 })
            Assert.Equal(GameModeFamily.GroupTwo, GameMode.Resolve(id).Family);
        foreach (var id in new[] { 13, 24, 26 })
            Assert.Equal(GameModeFamily.SinglePvp, GameMode.Resolve(id).Family);
        Assert.Equal(GameModeFamily.Captain, GameMode.Resolve(20).Family);
        foreach (var id in new[] { 101, 106 })
            Assert.Equal(GameModeFamily.Horde, GameMode.Resolve(id).Family);
        foreach (var id in new[] { 12, 14, 39, 18, 19, 31 })
            Assert.Equal(GameModeFamily.Other, GameMode.Resolve(id).Family);
    }

    [Fact]
    public void ModeIdsAreTheClientsCodenameIds()
    {
        // kr-1256 sub_1134540 and sub_1134B50; kr-812 sub_8E8900 agrees on the modes it names.
        Assert.Equal(1, (int)GameModeId.Pvp);
        Assert.Equal(3, (int)GameModeId.Item);
        Assert.Equal(8, (int)GameModeId.Zombie);
        Assert.Equal(16, (int)GameModeId.ZombiePve);
        Assert.Equal(18, (int)GameModeId.SniperTeamSurvival);
        Assert.Equal(20, (int)GameModeId.Captain);
        Assert.Equal(43, (int)GameModeId.ZombieOrigin);
        Assert.Equal(45, (int)GameModeId.ZombieSurvival);
        Assert.Equal(102, (int)GameModeId.HordeZhe);
        Assert.Equal(106, (int)GameModeId.PirateAssaultSeason3);
        Assert.Equal(47, Enum.GetValues<GameModeId>().Length);
        foreach (var id in new[] { 2, 4, 22, 104 })
            Assert.False(Enum.IsDefined((GameModeId)id));
    }

    [Fact]
    public void NoOverrideOutsideTheVerifiedModes()
    {
        Assert.Equal(0, GameMode.Resolve(1).TimeAttackTimeOverrideMs);
        Assert.Equal(0, GameMode.Resolve(20).TimeAttackTimeOverrideMs);
        Assert.Equal(0, GameMode.Resolve(6).TimeAttackTimeOverrideMs);
    }
}
