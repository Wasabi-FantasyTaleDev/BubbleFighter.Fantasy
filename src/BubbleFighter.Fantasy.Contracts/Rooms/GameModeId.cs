namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>
/// The game modes by the id every client build sends in a room's settings. The names are the clients' own
/// codenames without their <c>wgt</c> prefix: the final Korean client turns each codename into its id in
/// kr-1256 sub_1134540 and back in kr-1256 sub_1134B50, and its <c>StringTable/mode.yaml</c>
/// (<c>mode/showname</c>) holds the display names summarised on each member. Older builds use the same ids
/// for the modes they have (kr-812 sub_8E8900 reads <c>DefaultGameType</c> with the same codenames and ids)
/// and simply lack the later ones. Ids 2, 4, 22 and 104 have no codename in kr-1256 and are not listed: cn-279
/// still handles 2 (cn-279 sub_718B90 draws a questobject.png marker in it, its name unknown), a mode the
/// later builds dropped.
/// </summary>
public enum GameModeId
{
    /// <summary><c>wgtPvp</c>: the no-item battle.</summary>
    Pvp = 1,

    /// <summary><c>wgtItem</c>: the item battle.</summary>
    Item = 3,

    /// <summary><c>wgtPvpStar</c>: star collecting, every player for themselves.</summary>
    PvpStar = 5,

    /// <summary><c>wgtPvpStarTeam</c>: star collecting by team.</summary>
    PvpStarTeam = 6,

    /// <summary><c>wgtEventLucci</c>: the jackpot Lucci bonus event.</summary>
    EventLucci = 7,

    /// <summary><c>wgtZombie</c>: the zombie battle.</summary>
    Zombie = 8,

    /// <summary><c>wgtNPC</c>: the NPC mode.</summary>
    Npc = 9,

    /// <summary><c>wgtConquerTeam</c>: no display name in kr-1256's string table.</summary>
    ConquerTeam = 10,

    /// <summary><c>wgtEventSoccer</c>: the top-view soccer battle.</summary>
    EventSoccer = 11,

    /// <summary><c>wgtSniper</c>: the sniper battle.</summary>
    Sniper = 12,

    /// <summary><c>wgtSinglePvp</c>: survival, every player for themselves.</summary>
    SinglePvp = 13,

    /// <summary><c>wgtTeamSurvival</c>: survival by team.</summary>
    TeamSurvival = 14,

    /// <summary><c>wgtSinglePlay</c>: playing alone, the tutorial room's stages.</summary>
    SinglePlay = 15,

    /// <summary><c>wgtZombiePVE</c>: the zombie raid boss.</summary>
    ZombiePve = 16,

    /// <summary><c>wgtEventMole</c>: the mole event.</summary>
    EventMole = 17,

    /// <summary><c>wgtSniperTeamSurvival</c>: survival snipers by team.</summary>
    SniperTeamSurvival = 18,

    /// <summary><c>wgtBomb</c>: the bomb mission.</summary>
    Bomb = 19,

    /// <summary><c>wgtCaptain</c>: the captain battle.</summary>
    Captain = 20,

    /// <summary><c>wgtBFRunner</c>: crazy racing.</summary>
    BfRunner = 21,

    /// <summary><c>wgtArea32</c>: survival mode, every player for themselves.</summary>
    Area32 = 23,

    /// <summary><c>wgtExtremeRacing</c>: extreme racing.</summary>
    ExtremeRacing = 24,

    /// <summary><c>wgtSuddenSurvival</c>: survival mode by team.</summary>
    SuddenSurvival = 25,

    /// <summary><c>wgtWeaponMaster</c>: weapon master.</summary>
    WeaponMaster = 26,

    /// <summary><c>wgtSnowfight</c>: the snow bomb fight (renamed each season).</summary>
    Snowfight = 27,

    /// <summary><c>wgtZombieGhost</c>: the ghost zombie battle.</summary>
    ZombieGhost = 28,

    /// <summary><c>wgtControlPoint</c>: the occupation battle.</summary>
    ControlPoint = 29,

    /// <summary><c>wgtFinalZone</c>: survival mode, squads.</summary>
    FinalZone = 30,

    /// <summary><c>wgtShotgun</c>: the shotgun battle.</summary>
    Shotgun = 31,

    /// <summary><c>wgtZombieEscape</c>: the zombie escape.</summary>
    ZombieEscape = 32,

    /// <summary><c>wgtAllPang</c>: the all-pang battle.</summary>
    AllPang = 33,

    /// <summary><c>wgtNoItemPractice</c>: no-item battle practice.</summary>
    NoItemPractice = 34,

    /// <summary><c>wgtJumpJump</c>: the zero-gravity jet battle.</summary>
    JumpJump = 35,

    /// <summary><c>wgtNoItemTutorial</c>: the no-item battle against AI.</summary>
    NoItemTutorial = 36,

    /// <summary><c>wgtPsionic</c>: the top-view psionic battle.</summary>
    Psionic = 37,

    /// <summary><c>wgtNoItemCustomAiWar</c>: the custom no-item battle against AI.</summary>
    NoItemCustomAiWar = 38,

    /// <summary><c>wgtTeamSurvivalCustomAiWar</c>: the custom team survival against AI.</summary>
    TeamSurvivalCustomAiWar = 39,

    /// <summary><c>wgtTrainingRoom</c>: the training room.</summary>
    TrainingRoom = 40,

    /// <summary><c>wgtBnB</c>: the bubble-and-bomb arcade mode.</summary>
    BnB = 41,

    /// <summary><c>wgtTopViewSurvival</c>: top-view survival mode.</summary>
    TopViewSurvival = 42,

    /// <summary><c>wgtZombieOrigin</c>: the zombie battle G.</summary>
    ZombieOrigin = 43,

    /// <summary><c>wgtSimpleSuddenSurvival</c>: compact survival mode.</summary>
    SimpleSuddenSurvival = 44,

    /// <summary><c>wgtZombieSurvival</c>: the zombie survival game.</summary>
    ZombieSurvival = 45,

    /// <summary><c>wgtHorde</c>: the pirate battle (kr-1256 also shows it as <c>wgtHordeOrigin</c>, pirate invasion).</summary>
    Horde = 101,

    /// <summary><c>wgtHordeZHE</c>: the pirate battle's zombie Halloween event.</summary>
    HordeZhe = 102,

    /// <summary><c>wgtHordeTutor</c>: playing alone, the pirate-wave tutorial.</summary>
    HordeTutor = 103,

    /// <summary><c>wgtZombiePVEPrologue</c>: the zombie raid's prologue.</summary>
    ZombiePvePrologue = 105,

    /// <summary><c>wgtPirateAssaultSeason3</c>: the great pirate assault.</summary>
    PirateAssaultSeason3 = 106,
}
