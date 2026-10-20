using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// The option-set family a game mode resolves to. The client resolver <c>sub_711210</c> at
/// <c>0x711210</c> maps a mode id to one config option key; the family names below are the option-key
/// identities proven in the client resolver. Which <c>*ModeMgr</c> each family constructs is not walked yet, so
/// no manager is bound here.
/// </summary>
public enum GameModeFamily
{
    /// <summary>Base / default option set (mode 1 is the base mode; 2 and any unlisted id fall here).</summary>
    Default = 0,

    /// <summary>The <c>off_1588878</c> option set (modes 3, 21, 29, 33, 38); the option-key text is unverified.</summary>
    GroupTwo = 1,

    /// <summary>Zombie PvE option set (modes 6, 11, 16, 23, 25, 27, 30, 34, 36, 37, 41, 42, 44, 45).</summary>
    ZombiePve = 2,

    /// <summary>Zombie option set (modes 8, 28, 32, 43).</summary>
    Zombie = 3,

    /// <summary>Single PvP option set (modes 13, 24, 26).</summary>
    SinglePvp = 4,

    /// <summary>Captain option set (mode 20).</summary>
    Captain = 5,

    /// <summary>Horde option set (modes 101, 106).</summary>
    Horde = 6,

    /// <summary>Single-key option entries (modes 12, 14, 39, 18, 19, 31); their option-key texts are unverified.</summary>
    Other = 7,
}

/// <summary>
/// One resolved mode: its id, option family, and the verified create-time rule
/// override. Evidence: the mode-to-option table is <c>verified</c>
/// (<c>sub_711210</c>); the override is <c>verified</c>
/// (<c>sub_9A0880</c> lines 103-116: modes 8 / 28 / 43 write 180000 and mode
/// 32 writes 600000 into the rule record's TimeAttackTime slot). The wire
/// offset that carries the mode id is NOT pinned: the client reads it from the
/// session game-spec object at <c>dword_18E55AC+152</c>, not from the C2S 503
/// create request (whose rule record carries rule values only).
///
/// </summary>
/// <param name="ModeId">The mode id.</param>
/// <param name="Family">The option-key family.</param>
/// <param name="TimeAttackTimeOverrideMs">
/// The TimeAttackTime the create path forces for this mode, or 0 when the
/// client keeps the requested value.
/// </param>
public sealed record GameMode(int ModeId, GameModeFamily Family, int TimeAttackTimeOverrideMs)
{
    /// <summary>
    /// Resolves a mode id through the verified <c>sub_711210</c> table. Unknown
    /// ids resolve to <see cref="GameModeFamily.Default"/> with no override,
    /// exactly like the client's default branch.
    ///
    /// </summary>
    /// <param name="modeId">The mode id.</param>
    public static GameMode Resolve(int modeId) => (GameModeId)modeId switch
    {
        GameModeId.Pvp or GameModeId.JumpJump => new GameMode(modeId, GameModeFamily.Default, 0),
        GameModeId.Item or GameModeId.BfRunner or GameModeId.ControlPoint or GameModeId.AllPang
            or GameModeId.NoItemCustomAiWar => new GameMode(modeId, GameModeFamily.GroupTwo, 0),
        GameModeId.Zombie or GameModeId.ZombieGhost or GameModeId.ZombieOrigin =>
            new GameMode(modeId, GameModeFamily.Zombie, 180_000),
        GameModeId.ZombieEscape => new GameMode(modeId, GameModeFamily.Zombie, 600_000),
        GameModeId.PvpStarTeam or GameModeId.EventSoccer or GameModeId.ZombiePve or GameModeId.Area32
            or GameModeId.SuddenSurvival or GameModeId.Snowfight or GameModeId.FinalZone
            or GameModeId.NoItemPractice or GameModeId.NoItemTutorial or GameModeId.Psionic or GameModeId.BnB
            or GameModeId.TopViewSurvival or GameModeId.SimpleSuddenSurvival or GameModeId.ZombieSurvival =>
            new GameMode(modeId, GameModeFamily.ZombiePve, 0),
        GameModeId.SinglePvp or GameModeId.ExtremeRacing or GameModeId.WeaponMaster =>
            new GameMode(modeId, GameModeFamily.SinglePvp, 0),
        GameModeId.Captain => new GameMode(modeId, GameModeFamily.Captain, 0),
        GameModeId.Horde or GameModeId.PirateAssaultSeason3 => new GameMode(modeId, GameModeFamily.Horde, 0),
        GameModeId.Sniper or GameModeId.TeamSurvival or GameModeId.TeamSurvivalCustomAiWar
            or GameModeId.SniperTeamSurvival or GameModeId.Bomb or GameModeId.Shotgun =>
            new GameMode(modeId, GameModeFamily.Other, 0),
        _ => new GameMode(modeId, GameModeFamily.Default, 0),
    };
}
