using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.RoomCore;
using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.InGame;

/// <summary>
/// Configuration for the <see cref="InGamePlugin"/> row. Protocol names are supplied by the profile;
/// this plugin does not hard-code a client region in its id.
///
/// </summary>
public sealed partial class InGameOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether room, plaza and command chat share a per-connection rate budget.</summary>
    public bool ChatRateLimitEnabled { get; set; } = true;

    /// <summary>Gets or sets the accepted chat requests per window; this is server policy, not a client protocol limit.</summary>
    [Range(1, 10000)]
    public int ChatRateLimitMessages { get; set; } = 6;

    /// <summary>Gets or sets the monotonic chat accounting window in milliseconds.</summary>
    [Range(1, 3600000)]
    public int ChatRateLimitWindowMs { get; set; } = 3000;

    /// <summary>Gets or sets the maximum live connections retained by this row's chat policy; closed connections release their entry.</summary>
    [Range(1, 65536)]
    public int ChatRateLimitMaxSessions { get; set; } = 1024;

    /// <summary>
    /// Gets or sets whether a player's first clear of a tutorial-room stage grants the gift its MapMeta
    /// <c>IsItemAward</c> names (a Stock or StockSet of the build's shop workbook). Requires client data, stage
    /// storage and the shop row's gift service. YAML key <c>tutorial_gifts</c>.
    ///
    /// </summary>
    public bool TutorialGifts { get; set; }

    /// <summary>Gets or sets the native modes that admit independently reported direct kills without knock-down pairing.</summary>
    [MaxLength(32)]
    public int[] DirectKillModes { get; set; } = [];

    /// <summary>
    /// Gets or sets the modes whose characters live and die by the clients' own life reports (bubble, pop, death,
    /// reset): the round tracks each seat's life from them, a pop of an enemy's bubble scores for the popper's team,
    /// and a dead player's reset request is granted once its death presentation is over (kr-1256 mode 1, the timed
    /// score mode). Needs a dialect that publishes the life reports and the reset grant.
    ///
    /// </summary>
    [MaxLength(32)]
    public int[] NativeLifeModeIds { get; set; } = [];

    /// <summary>Gets or sets the mode with counted cooperative item slots; zero disables them.</summary>
    [Range(0, ushort.MaxValue)]
    public int CountedItemModeId { get; set; }

    /// <summary>Gets or sets the client item kinds in native slot order for the counted-item mode.</summary>
    [MaxLength(16)]
    public int[] CountedItemKinds { get; set; } = [];

    /// <summary>Gets or sets pickup kinds that apply immediately only in the counted-item mode.</summary>
    [MaxLength(64)]
    public int[] CountedImmediateItemKinds { get; set; } = [];

    /// <summary>Gets or sets the number of native cooperative item slots that the configured client UI queries.</summary>
    [Range(1, 16)]
    public int CountedItemSlotCount { get; set; } = 5;

    /// <summary>Gets or sets the maximum retained pickup count for one player's counted item kind.</summary>
    [Range(1, 100000)]
    public int MaxCountedItemsPerKind { get; set; } = 1000;
    /// <summary>Gets or sets the cooperative mode with native human-to-zombie allegiance conversion; zero disables it.</summary>
    [Range(0, ushort.MaxValue)]
    public int ZombieConversionModeId { get; set; }

    /// <summary>Gets or sets the configured cooperative zombie round duration used by the native start and server timeout.</summary>
    [Range(1, 3600000)]
    public int ZombieRoundTimeLimitMs { get; set; } = 600000;

    /// <summary>
    /// Gets or sets the zombie battle modes (humans against a zombie king and the zombinis it makes); empty
    /// disables them. The client keeps no rules of its own for them: the server picks the king, converts the
    /// dead and decides the winner.
    ///
    /// </summary>
    [MaxLength(16)]
    public int[] ZombiePvpModeIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the item kind the zombie king's ultra asks for, or 0 when the build's king has none. Once the
    /// client's own gauge is full the king's SHIFT sends the item request with no object and this kind
    /// (cn-324 sub_667CC0 -> cn-324 sub_4672B0(0x68, -1), kind 104 feverDrink); the round answers the king
    /// alone with the direct item effect, as for an immediate supply item.
    ///
    /// </summary>
    [Range(0, 65535)]
    public int ZombieKingUltraItemKind { get; set; }

    /// <summary>Gets or sets how long after a zombie battle starts the server turns a random human into the zombie king.</summary>
    [Range(0, 600000)]
    public int ZombieKingDelayMs { get; set; } = 5000;

    /// <summary>Gets or sets the zombie battle's time limit; the humans win when it runs out.</summary>
    [Range(1, 3600000)]
    public int ZombiePvpRoundTimeLimitMs { get; set; } = 180000;

    /// <summary>
    /// Gets or sets the star modes played every player for themselves (client mode 5): the seats holding the
    /// most stars when the round ends win. Empty turns the mode off.
    ///
    /// </summary>
    [MaxLength(16)]
    public int[] StarModeIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the star modes played by team (client mode 6): the team whose seats hold more stars when
    /// the round ends wins. Empty turns the mode off.
    ///
    /// </summary>
    [MaxLength(16)]
    public int[] StarTeamModeIds { get; set; } = [];

    /// <summary>Gets or sets the star modes' time limit; the KR2015 maps' StarModeTimeLimit is mostly 240 seconds.</summary>
    [Range(1, 3600000)]
    public int StarRoundTimeLimitMs { get; set; } = 240000;

    /// <summary>
    /// Gets or sets how many stars a big star is worth. The client turns ten held stars into one big star
    /// when it drops them (kr-812 sub_520A20, sub_5632F0), so ten.
    ///
    /// </summary>
    [Range(1, 1000)]
    public int BigStarValue { get; set; } = 10;

    /// <summary>Gets or sets the most stars one seat may hold; reports beyond it are clamped.</summary>
    [Range(1, 100000)]
    public int MaxRoundStars { get; set; } = 999;

    /// <summary>Gets or sets how many dropped stars may lie unconsumed in one round; further drops are ignored.</summary>
    [Range(1, 4096)]
    public int MaxDroppedStars { get; set; } = 512;

    /// <summary>
    /// Gets or sets the object id range reserved for stars; created stars use the values after it. Kept apart from
    /// the supply and Lucci ranges and from the clients' own character ids (1000000 + 100000 per seat).
    ///
    /// </summary>
    [Range(1, int.MaxValue - 65536)]
    public int StarObjectIdBase { get; set; } = 0x64000000;

    /// <summary>Gets or sets cooperative modes whose native stage result replaces team-score comparison.</summary>
    [MaxLength(64)]
    public int[] CooperativeStageResultModes { get; set; } = [];

    /// <summary>Gets or sets cooperative modes whose result UI derives objective victory from team totals.</summary>
    [MaxLength(64)]
    public int[] CooperativeScoreOutcomeModes { get; set; } = [];
    /// <summary>Gets or sets how long native quick matching searches before creating a room when no suitable room exists.</summary>
    [Range(0, 60000)]
    public int QuickMatchWaitMs { get; set; } = 3000;

    /// <summary>Gets or sets the interval between room searches during the native quick-match window.</summary>
    [Range(1, 1000)]
    public int QuickMatchPollMs { get; set; } = 100;

    /// <summary>Gets or sets the maximum number of pending quick-match requests for this row.</summary>
    [Range(1, 4096)]
    public int MaxPendingQuickMatches { get; set; } = 64;

    /// <summary>Gets or sets the maximum number of accepted scene moves, including those waiting for party members.</summary>
    [Range(1, 4096)]
    public int MaxPendingSceneTransitions { get; set; } = 128;

    /// <summary>Gets or sets the initial map marker used when quick matching must create a room.</summary>
    [Required]
    public string QuickMatchDefaultMap { get; set; } = "Random";

    /// <summary>Gets or sets client item kinds accepted when a random box resolves at collision time.</summary>
    [MaxLength(256)]
    public int[] RandomSupplyItemKinds { get; set; } = [];

    /// <summary>
    /// Gets or sets the modes that are item modes (client mode 3, <c>GameModeId.Item</c>), where the box kinds the server
    /// accepts are limited to <see cref="ItemModeKinds"/> and a box a team owns is eaten only by that team; empty disables
    /// the policy. The clients keep the rules of the mode (kr-812 builds a plain ModeMgr for it, sub_53AB30), so this does not
    /// schedule boxes: the host client reports the points and the server relays them.
    ///
    /// </summary>
    [MaxLength(16)]
    public int[] ItemModeIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the box kinds an item mode accepts, from the build's own item sheet: the enabled rows of
    /// <c>ItemSpecItem</c> where the build selects a sheet per mode (kr-812 sub_63C720), the rows with a weight in the
    /// grade sheets of <c>ItemSpec.xml</c> where it does not (cn-279 sub_885A70). A point, a thrown box or a pickup of
    /// any other kind in an item mode is refused; a random box (kind 0) is accepted and resolved at pickup.
    ///
    /// </summary>
    [MaxLength(64)]
    public int[] ItemModeKinds { get; set; } = [];

    /// <summary>Gets or sets the number of native held-item slots supported by the dialect.</summary>
    [Range(1, 2)]
    public int NativeItemSlotCount { get; set; } = 1;

    /// <summary>Gets or sets confirmed pickup kinds that run a native effect immediately instead of occupying an item slot.</summary>
    [MaxLength(64)]
    public int[] ImmediateSupplyItemKinds { get; set; } = [];

    /// <summary>
    /// Gets or sets the modes whose rooms have no start button and start natively once no seat is left open, every
    /// seat taken or closed by the master; empty disables this policy.
    ///
    /// </summary>
    [MaxLength(16)]
    public int[] AutoStartModeIds { get; set; } = [];

    /// <summary>Gets or sets whether an automatic-start room also counts down from its opening, with notices.</summary>
    public bool AutoStartTimer { get; set; }

    /// <summary>
    /// Gets or sets how long an automatic-start room waits after a round before it may start again; after that it
    /// starts once every seat is taken or closed and every player except the master is ready.
    ///
    /// </summary>
    [Range(0, 600000)]
    public int AutoStartRematchHoldMs { get; set; } = 20000;

    /// <summary>Gets or sets the manual room's total wait, including the native final five seconds.</summary>
    [Range(5000, 3600000)]
    public int AutoStartDelayMs { get; set; } = 65000;

    /// <summary>Gets or sets remaining seconds at which the waiting room receives a localized announcement.</summary>
    [MaxLength(16)]
    public int[] AutoStartNoticeSeconds { get; set; } = [30, 20, 10];

    /// <summary>Gets or sets the maximum number of unconsumed dropped item boxes retained by one round.</summary>
    [Range(1, 4096)]
    public int MaxDroppedSupplies { get; set; } = 512;
    /// <summary>Gets or sets the game-connection protocol name (port 7100 for the 1256 dialect).</summary>
    [Required]
    public string GameProtocol { get; set; } = "kr-1256/game";

    /// <summary>Gets or sets the relay-connection protocol name (port 7101 for the 1256 dialect).</summary>
    [Required]
    public string RelayProtocol { get; set; } = "kr-1256/relay";

    /// <summary>Gets or sets the maximum opaque match capability bytes retained for each room seat.</summary>
    [Range(1, 4096)]
    public int MaxMatchCapabilityBytes { get; set; } = 64;

    /// <summary>
    /// Gets or sets whether loaded game seats receive native match offers and wait for authenticated
    /// relay initialization and game-side confirmation before the countdown. Requires native dialect rows.
    ///
    /// </summary>
    public bool NativeMatchRelay { get; set; }

    /// <summary>
    /// Gets or sets whether a seat whose match connection reports failure plays the round over its game connection
    /// instead of holding the loading barrier. The client reports the failure on the game connection
    /// (kr-1256 sub_960050 -> sub_961C00 writes false) and, once the loading words clear the relay selection, sends
    /// every round packet there (kr-1256 sub_95B910 -> sub_961940); its game dispatcher hands round packets to the
    /// same scene reader the match connection uses (kr-1256 sub_9D2FF0 and sub_9601E0 both call sub_6D55D0).
    /// Requires <see cref="NativeMatchRelay"/>; off by default.
    ///
    /// </summary>
    public bool MatchTransportFallback { get; set; }

    /// <summary>
    /// Gets or sets whether a typed start draws the round's map on the server from the client's map pools
    /// (the clientdata row) instead of playing the key the master's client resolved. The kr-1256 start
    /// request carries the selection, possibly a random marker (kr-1256 sub_9D7570), and the client loads the
    /// map the server names in S2C 510 (kr-1256 sub_9D9930 case 0x1FE).
    ///
    /// </summary>
    public bool ServerMapDraw { get; set; }

    /// <summary>
    /// Gets or sets whether a typed start places the players without a team on the smaller team before the
    /// start roster is sent, for the team modes whose spawn selector needs team 1 or 2 (kr-1256 sub_C80220).
    ///
    /// </summary>
    public bool AssignSpawnTeamsOnStart { get; set; }

    /// <summary>Gets or sets the maximum supply points retained for one map.</summary>
    [Range(32, 1024)]
    public int MaxSupplyPoints { get; set; } = 128;

    /// <summary>Gets or sets the polling interval for active map supply schedules.</summary>
    [Range(10, 10000)]
    public int SupplyPollMs { get; set; } = 250;

    /// <summary>
    /// Gets or sets the shortest regeneration interval applied to the map points of a build whose report states none
    /// (cn-279 sub_8205F0). The later builds fall back to the same pair when their GameOption lacks the keys
    /// (kr-812 sub_50C840: 15000 and 30000).
    ///
    /// </summary>
    [Range(1, 3600000)]
    public int SupplyDefaultMinMs { get; set; } = 15000;

    /// <summary>Gets or sets the longest regeneration interval applied to the map points of a build whose report states none.</summary>
    [Range(1, 3600000)]
    public int SupplyDefaultMaxMs { get; set; } = 30000;

    /// <summary>Gets or sets the largest accepted client-reported supply regeneration interval.</summary>
    [Range(1000, 3600000)]
    public int MaxSupplyIntervalMs { get; set; } = 3600000;

    /// <summary>Gets or sets the supply reference range's first reserved value; generated objects use the next values.</summary>
    [Range(1, int.MaxValue - 1024)]
    public int SupplyObjectIdBase { get; set; } = 0x62000000;

    /// <summary>
    /// Gets or sets the object id range reserved for NPC-kill Lucci items; created items use the
    /// values after it. Kept apart from <see cref="SupplyObjectIdBase"/> so a drop can never take
    /// a supply box's id.
    ///
    /// </summary>
    [Range(1, int.MaxValue - 65536)]
    public int LucciObjectIdBase { get; set; } = 0x63000000;

    /// <summary>
    /// Gets or sets how many dropped Lucci items one round keeps unconsumed. A request past the
    /// bound is refused (no item is created), so a round of ignored drops cannot grow without limit.
    ///
    /// </summary>
    [Range(1, 4096)]
    public int MaxDroppedLucci { get; set; } = 256;

    /// <summary>
    /// Gets or sets the Lucci credited for picking up an item of kind 1 through 5 (Lucci, big Lucci,
    /// horde award 1, 5 and 10), in that order. The defaults are what the client adds to its own
    /// counter for each class (kr-812 sub_638600 adds the item's count, sub_6351F0 adds 100).
    ///
    /// </summary>
    public int[] LucciKindValues { get; set; } = [1, 100, 1, 5, 10];

    /// <summary>
    /// Gets or sets the item kind a horde-award request (C2S <c>gcpReqCreateHordeAward</c>) creates:
    /// 3, 4 or 5, which the client builds as the award worth 1, 5 or 10. The request itself names no kind.
    ///
    /// </summary>
    [Range(3, 5)]
    public int HordeAwardKind { get; set; } = 3;

    /// <summary>Gets or sets the most Lucci one seat can collect from drops in one round; pickups beyond it still remove the item.</summary>
    [Range(1, 10000000)]
    public int MaxRoundLucci { get; set; } = 10000;

    /// <summary>Gets or sets the IPv4 address written into <c>gspConnectToRelayServer</c>.</summary>
    [Required]
    public string RelayAdvertiseIpv4 { get; set; } = "127.0.0.1";

    /// <summary>Gets or sets the TCP port written into <c>gspConnectToRelayServer</c>.</summary>
    [Range(1, 65535)]
    public int RelayAdvertisePort { get; set; } = 7101;

    /// <summary>Gets or sets the maximum number of simultaneous in-memory rooms.</summary>
    [Range(1, 4096)]
    public int MaxMatches { get; set; } = 64;

    /// <summary>Retired experiment; true is rejected because the staged match handshake replaces this switch.</summary>
    public bool SendSceneObjectReply { get; set; }

    /// <summary>Retired experiment; true is rejected because the staged match handshake replaces this switch.</summary>
    public bool SendInSessionSpawn { get; set; }

    /// <summary>Retired experiment; true is rejected because the staged match handshake replaces this switch.</summary>
    public bool SendSceneObjectOnMapNamed { get; set; }

    /// <summary>Retired experiment; true is rejected because the staged match handshake replaces this switch.</summary>
    public bool SendMatchArenaBlock { get; set; }

    /// <summary>
    /// Gets or sets whether the waiting dialog (S2C 128) rides the room
    /// lifecycle: state 0 to every seat when a room is created or joined (the
    /// "waiting for players" wall, client UI resource 1760), state 0x14 when a
    /// start is accepted (dismisses the wall; the host's client then re-sends
    /// C2S 402, the normal answer).
    /// Off by default: it is an A/B experiment for the missing
    /// waiting-for-players phase the operator described.
    ///
    /// </summary>
    public bool SendWaitingDialog { get; set; }

    /// <summary>Retains the former diagnostic event-code setting for configuration compatibility; no bootstrap uses it.</summary>
    [Range(0, 4095)]
    public int InSessionSpawnCode { get; set; } = 21;

    /// <summary>Gets or sets the maximum seats in one room.</summary>
    [Range(1, 32)]
    public int MaxPlayersPerMatch { get; set; } = 16;

    /// <summary>
    /// Gets or sets how many room operations (join, leave, ready, team change, connection-close detach)
    /// may wait in one room's serial executor while another runs. A request that finds the queue full
    /// fails loudly instead of waiting, so this bounds per-room memory and the backlog a burst of
    /// clients can build. YAML key <c>room_queue_capacity</c>.
    ///
    /// </summary>
    [Range(1, 4096)]
    public int RoomQueueCapacity { get; set; } = 64;

    /// <summary>
    /// Gets or sets how many players the plaza shows at once; the next entrant is refused loudly
    /// instead of silently dropped from everyone's view. The recorded client's own plaza seat array
    /// holds 36. YAML key <c>plaza_capacity</c>.
    ///
    /// </summary>
    [Range(1, 255)]
    public int PlazaCapacity { get; set; } = 36;

    /// <summary>
    /// Gets or sets whether a plaza enter that arrives before the login burst has finished is ignored. The
    /// kr-1256 client can send it during login, and its plaza install must not interrupt that burst.
    ///
    /// </summary>
    public bool PlazaEnterRequiresFinishedLogin { get; set; }

    /// <summary>Gets or sets whether an entrant receives stored or initial peer positions after its plaza install.</summary>
    public bool PlazaReplayPositionsOnEnter { get; set; } = true;

    /// <summary>Gets or sets whether plaza position broadcasts also go back to the reporting player.</summary>
    public bool PlazaEchoPositions { get; set; } = true;

    /// <summary>Gets or sets whether menu return first vacates peers on clients whose menu install retains their seats.</summary>
    public bool PlazaVacatePeersOnMenuReturn { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a seated player's first room-stage probe installs the whole room again. The install
    /// rebuilds the room stage, so a client that already holds the room plays its room-entry animation twice; the
    /// probe is still acknowledged and the member list republished either way. YAML key
    /// <c>reinstall_room_on_first_probe</c>.
    /// </summary>
    public bool ReinstallRoomOnFirstProbe { get; set; } = true;

    /// <summary>
    /// Gets or sets how long plugin unload waits, in milliseconds, for every room executor to finish its
    /// running operation and stop. Expiry is reported as an unload error rather than releasing room state
    /// under a still-running operation. YAML key <c>room_stop_timeout_ms</c>.
    ///
    /// </summary>
    [Range(1, 600_000)]
    public int RoomStopTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the milliseconds written as the single <c>i32</c> of S2C 607
    /// (<c>sub_9E7080</c>), and waited before S2C 608 fires. The client releases
    /// the waiting wall by itself once the countdown's remaining time is at or
    /// under 4000 ms (sub_75B870), so a value at or under 4000 opens the
    /// wall on the first pre-round frame - the "wall drops immediately"
    /// symptom. The recorded countdown is 7000: a first S2C 607 of 0, then 7000,
    /// then S2C 608 seven seconds later.
    ///
    /// </summary>
    /// <remarks>
    /// The wall cannot be held to exactly zero: the 4-second release is client
    /// code, and 608's field 0 is the round length, not a wall duration, so
    /// enlarging it would stretch the round instead.
    /// </remarks>
    [Range(0, 600_000)]
    public int StartTimerMs { get; set; } = 7000;

    /// <summary>
    /// Gets or sets the fallback round length the S2C 608 body carries when the
    /// room has no rule record of its own. <c>sub_6E7570</c> passes it to
    /// <c>sub_6FEFC0</c> at <c>sub_9E7570</c>, which stores it as the round
    /// duration and derives the end tick from it (<c>sub_6FEFC0</c>), so a
    /// value of a few seconds ends the round moments after it starts. The
    /// default is the four minutes a normal round lasts (S2C 608 for
    /// mode 1); the training ground ignores it and uses the no-limit encoding.
    ///
    /// </summary>
    [Range(0, int.MaxValue)]
    public int RoundUnknown0 { get; set; } = 240_000;

    /// <summary>
    /// Gets or sets the second <c>i32</c> of S2C 608, stored by the client at
    /// <c>dword_18E7558</c>.
    ///
    /// </summary>
    public int RoundUnknown1 { get; set; }

    /// <summary>Gets or sets S2C 585 field 0 (i32-as-bool, <c>sub_9E1EB0</c> case 0x249).</summary>
    public int StartFlag0 { get; set; } = 1;

    /// <summary>Gets or sets S2C 585 field 1 (i32-as-bool).</summary>
    public int StartFlag1 { get; set; }

    /// <summary>Gets or sets S2C 585 field 2 (i32-as-bool).</summary>
    public int StartFlag2 { get; set; } = 1;

    /// <summary>
    /// Gets or sets whether the server drives the room-to-match start chain
    /// documented in packets/round-start.yaml: S2C 510
    /// (enter the game stage), S2C 585 (start flags), S2C 584 (seat loaded) per
    /// seat, S2C 602 / 603 / 604 / 605 (seat-info records) per seat, S2C 571
    /// (relay connect), S2C 607 (round countdown) and S2C 608 (round
    /// initialize), after which the client writes its own empty C2S 17.
    /// Defaults to <see langword="false"/>: the chain stays off until a live
    /// client test confirms the S2C 510 enter-role inference (that the handler
    /// <c>sub_9E5E70</c> is what creates the in-play session the S2C 607 / 608
    /// handlers gate on) and the unverified seat-record field meanings. When
    /// false a start click is only logged and the administration start fails.
    ///
    /// </summary>
    public bool StartMatches { get; set; }

    /// <summary>Gets or sets the S2C 615 reward-stage flag; only flag zero is supported.</summary>
    [Range(0, 0)]
    public byte ResultClose { get; set; }

    /// <summary>Gets or sets the delay from S2C 612 to the optional S2C 614 scoreboard.</summary>
    [Range(0, 600_000)]
    public int ResultScoreboardDelayMs { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the delay from a legacy round's end on its time limit to the time-over announcement
    /// (3.0 s by default); it is taken out of
    /// <see cref="ResultRewardDelayMs"/>.
    ///
    /// </summary>
    [Range(0, 600_000)]
    public int ResultTimeOverDelayMs { get; set; } = 3_000;

    /// <summary>Gets or sets the delay after the scoreboard stage before S2C 615 enters the reward stage.</summary>
    [Range(0, 600_000)]
    public int ResultRewardDelayMs { get; set; } = 5_000;

    /// <summary>
    /// Gets or sets the longest time, in milliseconds, the round keeps a player dead on a build
    /// whose client waits to be brought back; a longer reported time is cut to this, so a client
    /// cannot park a timer for longer than a round. YAML key <c>respawn_delay_max_ms</c>.
    ///
    /// </summary>
    [Range(0, 600_000)]
    public int RespawnDelayMaxMs { get; set; } = 30_000;

    /// <summary>Gets or sets the inventory entitlement consumed by instant respawn; zero disables consumable revival.</summary>
    [Range(0, int.MaxValue)]
    public int InstantRevivalItemId { get; set; }

    /// <summary>Gets or sets native PvE modes whose dead players may request an immediate spawn offer without inventory consumption.</summary>
    [MaxLength(32)]
    public int[] PveRevivalModes { get; set; } = [];

    /// <summary>
    /// Gets or sets how many milliseconds one taunt takes off a dead player's wait on a build whose
    /// round times the respawn. The original amount was server data that is not in the client;
    /// 0 turns the effect off. YAML key <c>respawn_taunt_reduction_ms</c>.
    ///
    /// </summary>
    [Range(0, 600_000)]
    public int RespawnTauntReductionMs { get; set; } = 2_000;

    /// <summary>Gets or sets the reward-stage duration before S2C 576 and 620 return clients to the room.</summary>
    [Range(0, 600_000)]
    public int ResultCloseGraceMs { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets whether modes without verified team scoring send synthesized S2C 614 identity rows.
    /// Ordinary timed-score mode always sends its team totals. C2S 103 counters are never copied into rows.
    /// This option does not send S2C 613, which the result sequence does not use.
    ///
    /// </summary>
    public bool SendScoreboardRows { get; set; }

    /// <summary>
    /// Gets or sets an optional client mode-selector table path. Empty means C2S 402
    /// <c>MapMeta</c> is the only source. A non-empty path must exist and list at least
    /// one <c>u16</c> id or activation fails.
    ///
    /// </summary>
    public string ModeTablePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the server relays a seat's character position report (C2S 301 in the
    /// plaza, C2S 511 in a room) to the other seats as the 26-byte S2C 301 / S2C 514 body documented in
    /// src/Plugins/BubbleFighter.Fantasy.InGame/Handlers/RoundFlow.Movement.cs. Defaults to <see
    /// langword="false"/>: the relay is inert until a live two-client test confirms that the peer avatar
    /// appears, because the client code proves the apply path; peer visibility still requires native-client
    /// confirmation.
    /// </summary>
    public bool RelayCharacterPositions { get; set; }

    /// <summary>
    /// Gets or sets whether C2S 401 is answered with the S2C 101 room-list snapshot (three <c>i32</c>
    /// headers, a <c>u32</c> count, then one <c>SessionSetting</c> per open room). Defaults to <see
    /// langword="false"/>. The S2C 101 body and its LobbyStage dispatch chain are verified, but the C2S 401
    /// -&gt; S2C 101 pairing is only inferred (401 is the sole room-list request with a plausible reply), so
    /// the reply waits for a live confirmation. No S2C 508 / 509 is sent for it: those are per-seat in-room
    /// broadcasts, not list records.
    /// </summary>
    public bool SendRoomList { get; set; }

    /// <summary>
    /// Gets or sets whether a room chat send (C2S 502, and C2S 520 for the typed builders) is
    /// broadcast to every seat of the room as an S2C 508 per-seat record. Defaults to <see langword="false"/>:
    /// the S2C 508 body and its chat render path are verified, but the C2S 502 -&gt; S2C 508 pairing is
    /// inferred — no read site links C2S 502 to any S2C — so the broadcast waits for a live two-client test.
    ///
    /// </summary>
    public bool BroadcastRoomChat { get; set; }

    /// <summary>Gets or sets whether authenticated game chat invokes shared plugin commands and room-command permissions; disabled until client acceptance.</summary>
    public bool SharedChatCommands { get; set; }

    /// <summary>
    /// Gets or sets whether a seated client's first C2S 511 character position report re-publishes
    /// the room UI (S2C 118 user list plus one S2C 511 seat record per seat). Defaults to <see
    /// langword="false"/>. The S2C 117 handler only stores the room stage factory (MainApp vtable slot 7 =
    /// sub_87C990) and the S2C 118 handler (sub_9ACD00) discards rows that arrive before the live stage casts
    /// to RoomStage8vs8 with the member panel built, so the create/join burst is consumed too early. A C2S 511
    /// from a seated client proves the stage is live, which makes it the re-publish trigger. The ordering is
    /// verified; the choice of this packet as a re-publication trigger is inferred.
    /// </summary>
    public bool RepublishRoomUiOnPosition { get; set; }

    /// <summary>
    /// Gets or sets whether a seated client's repeated C2S 517 join request re-publishes the room UI
    /// (the S2C 118 user list plus one S2C 511 seat record per seat), and whether the first join does the same.
    /// Defaults to <see langword="false"/>. The create-burst S2C 118 is discarded by <c>sub_9ACD00</c>'s
    /// <c>RoomStage8vs8</c> cast and the burst's S2C 511 never reaches the spawner, because the S2C 117 handler
    /// only records the stage-4 factory and the frame loop consumes it later. The live client re-sends C2S 517
    /// while it waits for the room to become ready and never sends a C2S 511 position report (observed
    /// 2026-09-20 22:58Z), so the re-send is the only trigger that provably lands after the stage is live. With
    /// it off the member list stays blank and no character spawns.
    /// </summary>
    public bool RepublishRoomUiOnJoin { get; set; }

    /// <summary>
    /// Gets or sets the state written to session+12 in S2C 117. The kr-1256
    /// install handler destroys the held session before applying the parsed
    /// setting (sub_9AC490, 0x9AC6C2; sub_9D9250). The waiting-room start
    /// click requires state 2 (sub_970590), which is the default.
    ///
    /// </summary>
    [Range(2, 6)]
    public int RoomInstallState { get; set; } = RoomCore.SessionSettingBlob.RoomStateWaiting;

    /// <summary>
    /// Gets or sets whether S2C 501 writes <see cref="RoomInstallState"/>
    /// before S2C 117 installs the room. Defaults to false. This adds a state
    /// write to the currently held session; it is not required by the default
    /// install path, which replaces that session. Validate the complete room
    /// transition before enabling this compatibility ordering.
    ///
    /// </summary>
    public bool PrimeSessionStateBeforeInstall { get; set; }

    /// <summary>
    /// Gets or sets whether training-room creation starts the match immediately
    /// after the room push. Defaults to false, leaving the player in the waiting
    /// room until the ordinary start path runs. Requires <see cref="StartMatches"/>.
    ///
    /// </summary>
    public bool StartTrainingImmediately { get; set; }

    /// <summary>
    /// Gets or sets whether ordinary room creation (C2S 402) or joining through
    /// C2S 517 also sends the S2C 510 stage-enter record. Defaults to false;
    /// training uses its own start path and does not read this option.
    ///
    /// </summary>
    /// <remarks>
    /// The kr-1256 spawner at sub_9064B0 checks dword_18E6844+96 &gt; 2 or an
    /// accumulation flag. The stage setter sub_87C990 is reached through S2C 510.
    /// Enabling this option therefore changes the client's stage, not just the
    /// appearance of the room. Validate the complete room-entry flow before use.
    /// </remarks>
    public bool EnterGameStageOnCreate { get; set; }

    /// <summary>
    /// Gets or sets whether the room push also sends a per-seat S2C 511 record.
    /// Defaults to false: S2C 117 already carries the player vector, while
    /// S2C 806 carries the equipped view. The kr-1256 sub_9DB1E0 handler can
    /// select either destruction (sub_920060) or spawning (sub_9201D0), so this
    /// optional packet requires complete client room-entry validation.
    ///
    /// </summary>
    public bool SendSeatRecords { get; set; }

    /// <summary>Retired experiment; true is rejected because the staged match handshake replaces this switch.</summary>
    public bool SendStageSeatRecords { get; set; }

    /// <summary>Retired experiment; true is rejected because the staged match handshake replaces this switch.</summary>
    public bool SendStageRoomSnapshots { get; set; }

    /// <summary>
    /// Gets or sets whether a C2S 515 team change re-sends the whole room push
    /// (S2C 117 and its companions) so the new label reaches
    /// <c>RoomPlayerInfo.unknown4</c>. Defaults to <see langword="false"/>: the
    /// 117's kind-2 arm calls <c>SetStage</c>, so each click rebuilds the room and
    /// the client visibly leaves and re-enters it. With the option off the team
    /// change publishes only the seatless S2C 118 wait list, which cannot touch
    /// the stage, and the label stays correct in server state for the next real
    /// room install.
    ///
    /// </summary>
    public bool RepublishFullRoomOnTeamChange { get; set; }

    /// <summary>
    /// Gets or sets whether the room push sends the per-seat S2C 201 party
    /// update. Defaults to <see langword="true"/>.
    ///
    /// </summary>
    /// <remarks>
    /// The reference's room-entry burst carries it, and its handler
    /// <c>sub_9064B0</c> is the only packet there that both writes a profile-map
    /// record per member (<c>sub_9064B0</c>) and spawns and dresses a
    /// Character per occupied seat through <c>sub_920940</c>
    /// (<c>sub_9064B0</c>, gated on the stage not being 5) - the same
    /// spawner the login burst uses to render the plaza character. It is what
    /// turns the seat list the 117 installed into visible characters.
    /// </remarks>
    public bool SendPartyUpdate { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the room push sends the S2C 118 waiting-room list.
    /// Defaults to <see langword="false"/>.
    ///
    /// </summary>
    /// <remarks>
    /// S2C 118 is sent once, a four-byte
    /// empty body inside the login burst, and not in the room-entry sequence.
    /// The reference's room burst is 117, 521, 501, 201, 806, 837, 836 with no
    /// 118 at all. Enable it to reproduce the old populated list.
    /// </remarks>
    public bool SendWaitUserList { get; set; }

    /// <summary>
    /// Gets or sets a reserved practice-seat count. It has no effect and is
    /// kept only so a deployment that already set it does not fail validation.
    /// The training ground's dummies are <c>ChrTrainingDummy</c> scene objects
    /// that the <c>NpcGameStage</c> builds from the game configuration -
    /// <c>sub_840F00</c> reads the <c>Dummyposition</c> and <c>Dummyboundary</c>
    /// keys and the class is constructed at <c>sub_47DF90</c> - not room seats,
    /// so the server sends no packet for them. Seating extra players instead
    /// would make the client count them as players, which is why the earlier
    /// seat-based approach was removed.
    ///
    /// </summary>
    public int TrainingPracticeSeats { get; set; }

    /// <summary>
    /// Gets or sets whether a plaza enter (C2S 106) is answered with the S2C
    /// 117 kind-3 plaza snapshot on top of the S2C 116 result reply.
    /// before. See src/Plugins/BubbleFighter.Fantasy.InGame/Sessions/PlazaRoster.cs.
    ///
    /// </summary>
    public bool SendPlazaSnapshot { get; set; }

    /// <summary>
    /// Gets or sets the ids carried in each profile entry's vecB. Only the empty
    /// list is accepted, which is also the reference's shape: the fold that
    /// consumes the entry replays vecB and vecC first and then runs
    /// <c>sub_818190</c>, which truncates <c>equipment+64</c> and clears the
    /// <c>+8/+20/+32</c> containers before vecA is replayed last
    /// (<c>sub_818190</c>, <c>sub_817DF0</c>), so nothing vecB carried
    /// survives to the dresser <c>sub_91F720</c> - which reads only
    /// <c>equipment+64</c> (<c>sub_91F720</c>). A non-empty value therefore
    /// cannot do what it looks like it does, and is refused at load rather than
    /// silently dropped. The field itself stays on the wire because the reader
    /// consumes its count; see
    /// packets/room-modern.yaml section 6.
    ///
    /// </summary>
    public IReadOnlyList<int> ModelItemIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the total experience at which levels 1, 2, 3,... start (the first entry is 0). A
    /// configured CSV takes precedence over this deployment list; an empty list without a CSV turns rewards
    /// off. Uncapped inline tables retain the terminal-step extension.
    /// </summary>
    public IReadOnlyList<long> LevelExpThresholds { get; set; } = [];

    /// <summary>Gets or sets a CSV containing consecutive level numbers and cumulative experience; empty uses inline thresholds.</summary>
    public string LevelExpTablePath { get; set; } = "";

    /// <summary>Gets or sets the highest awarded level; zero uses the CSV's last level or preserves uncapped inline behavior.</summary>
    [Range(0, 10_000)]
    public int MaxLevel { get; set; }

    /// <summary>Gets or sets the experience every seat earns for finishing a round.</summary>
    [Range(0, 1_000_000)]
    public int RewardBaseExp { get; set; } = 150;

    /// <summary>Gets or sets the experience earned per kill in a round.</summary>
    [Range(0, 1_000_000)]
    public int RewardExpPerKill { get; set; }

    /// <summary>Gets or sets the multiplier applied to the winning team's experience.</summary>
    [Range(1, 100)]
    public int RewardWinMultiplier { get; set; } = 2;

    /// <summary>Gets or sets the Lucci earned, as a percentage of the experience earned.</summary>
    [Range(0, 10_000)]
    public int RewardLucciPercent { get; set; } = 35;

    /// <summary>
    /// Gets or sets how many times a round tries to settle one seat's experience and Lucci before it logs the seat
    /// as unsettled. Every attempt carries the round start's match id, so a retry after an unacknowledged commit
    /// returns the stored receipt instead of crediting again.
    ///
    /// </summary>
    [Range(1, 5)]
    public int SettlementAttempts { get; set; } = 2;

    /// <summary>
    /// Gets or sets the state carried by S2C 503 and the S2C 117 room snapshot.
    /// Defaults to <see cref="RoomCore.SessionSettingBlob.RoomStateWaiting"/>
    /// (2), which the kr-1256 start click requires (sub_970590).
    ///
    /// </summary>
    /// <remarks>
    /// The kr-1256 sub_975D50 controls are disabled in state 4. The round's
    /// S2C 608 consumer sets that state during initialization (sub_9E7570);
    /// sending it for a waiting room can disable its controls prematurely.
    /// </remarks>
    public int RoomState { get; set; } = RoomCore.SessionSettingBlob.RoomStateWaiting;

    /// <summary>
    /// Gets or sets a mode id that replaces the one the client sent on create.
    /// Zero keeps the client's value, which is the default.
    ///
    /// </summary>
    /// <remarks>
    /// The room-stage click gates refuse modes outside their sets: the team-change
    /// chain needs a mode in <c>sub_1150F80</c>'s twenty-four or in
    /// <c>sub_1151020</c>'s 30/42/44 (<c>sub_1150F80</c>,
    /// <c>sub_1151020</c>), and the click dispatcher's own switch starts at 3
    /// (<c>sub_969730</c>). Mode 1 - the base mode a default create carries -
    /// is in none of them, so a room created with it has its team-change, ready and
    /// start clicks refused before they build a C2S.
    /// <para>
    /// Overriding the mode makes those clicks pass, but it also changes the mode
    /// the client displays and the option set it resolves through
    /// <c>sub_711210</c>, so it is off by default rather than silently applied.
    /// </para>
    /// </remarks>
    public int ModeOverride { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        foreach (var result in ValidateAi()) yield return result;
        if (LotteryModes.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(LotteryWorkbookPath) == string.IsNullOrWhiteSpace(LotteryWorkbookClientPath)
                || LotteryModes.Any(mode => mode <= 0)
                || LotteryModes.Distinct().Count() != LotteryModes.Length || LotteryItems.Length is < 1 or > 128
                || LotteryItems.Any(item => !Data.LotteryCatalog.IsValidPrize(item)))
                yield return new ValidationResult("Reward-card modes and a bounded, valid native reward pool must be configured.");
            if (LotteryGroupedDraw && !Data.GroupedLottery.IsValid(this))
                yield return new ValidationResult("Grouped rewards require sufficient native prize families and bounded positive count/group weights.");
        }
        foreach (var result in ValidateRescue()) yield return result;
        foreach (var result in ValidatePvePoints()) yield return result;
        if (NativeLifeModeIds.Any(mode => mode is <= 0 or > ushort.MaxValue) || NativeLifeModeIds.Distinct().Count() != NativeLifeModeIds.Length)
            yield return new ValidationResult("native_life_mode_ids must be distinct mode ids from 1 to 65535.", [nameof(NativeLifeModeIds)]);
        if (DirectKillModes.Any(mode => mode is <= 0 or > ushort.MaxValue)
            || DirectKillModes.Distinct().Count() != DirectKillModes.Length)
            yield return new ValidationResult("direct_kill_modes must contain distinct positive mode identifiers.");
        if (PveRevivalModes.Any(mode => mode is <= 0 or > ushort.MaxValue)
            || PveRevivalModes.Distinct().Count() != PveRevivalModes.Length)
            yield return new ValidationResult("pve_revival_modes must contain distinct positive mode identifiers.");
        if ((CountedItemModeId > 0) != (CountedItemKinds.Length > 0)
            || (CountedItemModeId > 0 && CountedItemKinds.Length != CountedItemSlotCount)
            || CountedItemKinds.Any(kind => kind <= 0 || !RandomSupplyItemKinds.Contains(kind))
            || CountedItemKinds.Distinct().Count() != CountedItemKinds.Length)
            yield return new ValidationResult("counted_item_mode_id requires distinct counted_item_kinds present in random_supply_item_kinds.");
        if ((CountedImmediateItemKinds.Length > 0 && CountedItemModeId == 0)
            || CountedImmediateItemKinds.Any(kind => kind <= 0 || CountedItemKinds.Contains(kind))
            || CountedImmediateItemKinds.Distinct().Count() != CountedImmediateItemKinds.Length)
            yield return new ValidationResult("counted_immediate_item_kinds requires a counted mode and distinct positive kinds outside its counted slots.");
        if (CooperativeStageResultModes.Any(mode => mode is <= 0 or > ushort.MaxValue)
            || CooperativeStageResultModes.Distinct().Count() != CooperativeStageResultModes.Length)
            yield return new ValidationResult("cooperative_stage_result_modes must contain distinct positive mode identifiers.");
        if (CooperativeScoreOutcomeModes.Any(mode => !CooperativeStageResultModes.Contains(mode))
            || CooperativeScoreOutcomeModes.Distinct().Count() != CooperativeScoreOutcomeModes.Length)
            yield return new ValidationResult("cooperative_score_outcome_modes must contain distinct configured cooperative stage modes.");
        if (RandomSupplyItemKinds.Any(kind => kind <= 0) || RandomSupplyItemKinds.Distinct().Count() != RandomSupplyItemKinds.Length)
            yield return new ValidationResult("random_supply_item_kinds must contain distinct positive client item kinds.");
        if (ImmediateSupplyItemKinds.Any(kind => kind <= 0 || RandomSupplyItemKinds.Contains(kind))
            || ImmediateSupplyItemKinds.Distinct().Count() != ImmediateSupplyItemKinds.Length)
            yield return new ValidationResult("immediate_supply_item_kinds must contain distinct positive kinds that do not occupy an item slot.");
        if (SupplyDefaultMaxMs < SupplyDefaultMinMs || SupplyDefaultMaxMs > MaxSupplyIntervalMs)
            yield return new ValidationResult("supply_default_max_ms must be at least supply_default_min_ms and no more than max_supply_interval_ms.");
        if ((ItemModeIds.Length > 0) != (ItemModeKinds.Length > 0)
            || ItemModeIds.Any(mode => mode is <= 0 or > ushort.MaxValue || mode == CountedItemModeId
                || mode == ZombieConversionModeId || ZombiePvpModeIds.Contains(mode)
                || StarModeIds.Contains(mode) || StarTeamModeIds.Contains(mode))
            || ItemModeIds.Distinct().Count() != ItemModeIds.Length)
            yield return new ValidationResult("item_mode_ids requires item_mode_kinds, and must hold distinct positive mode ids that no counted, zombie or star mode uses.");
        if (ItemModeKinds.Any(kind => kind <= 0 || !(RandomSupplyItemKinds.Contains(kind) || ImmediateSupplyItemKinds.Contains(kind)))
            || ItemModeKinds.Distinct().Count() != ItemModeKinds.Length)
            yield return new ValidationResult("item_mode_kinds must contain distinct kinds present in random_supply_item_kinds or immediate_supply_item_kinds.");
        if (ZombieKingUltraItemKind > 0 && ZombiePvpModeIds.Length == 0)
            yield return new ValidationResult("zombie_king_ultra_item_kind requires zombie_pvp_mode_ids.");
        if (ZombiePvpModeIds.Any(mode => mode is <= 0 or > ushort.MaxValue || mode == ZombieConversionModeId)
            || ZombiePvpModeIds.Distinct().Count() != ZombiePvpModeIds.Length)
            yield return new ValidationResult("zombie_pvp_mode_ids must contain distinct positive mode ids other than zombie_conversion_mode_id.");
        var starModes = StarModeIds.Concat(StarTeamModeIds).ToArray();
        if (starModes.Any(mode => mode is <= 0 or > ushort.MaxValue || mode == ZombieConversionModeId || ZombiePvpModeIds.Contains(mode))
            || starModes.Distinct().Count() != starModes.Length)
            yield return new ValidationResult("star_mode_ids and star_team_mode_ids must contain distinct positive mode ids that no zombie mode uses.");
        if (AutoStartModeIds.Any(mode => mode is <= 0 or > ushort.MaxValue) || AutoStartModeIds.Distinct().Count() != AutoStartModeIds.Length)
            yield return new ValidationResult("auto_start_mode_ids must contain distinct positive mode ids.");
        if (LucciKindValues.Length != 5 || LucciKindValues.Any(value => value is < 0 or > 1000000))
            yield return new ValidationResult("lucci_kind_values must hold five values from 0 to 1000000, one per item kind 1 through 5.");
        if (AutoStartNoticeSeconds.Any(seconds => seconds is <= 0 or > 3600)
            || AutoStartNoticeSeconds.Distinct().Count() != AutoStartNoticeSeconds.Length)
            yield return new ValidationResult("auto_start_notice_seconds must contain distinct positive values up to 3600.");
        if (SendSceneObjectReply || SendInSessionSpawn || SendSceneObjectOnMapNamed || SendMatchArenaBlock
            || SendStageSeatRecords || SendStageRoomSnapshots)
            yield return new ValidationResult("Legacy scene/bootstrap experiments are retired; disable send_scene_object_reply, send_in_session_spawn, send_scene_object_on_map_named, send_match_arena_block, send_stage_seat_records and send_stage_room_snapshots. The match-connection row owns the handshake.");

        if (MatchTransportFallback && !NativeMatchRelay)
            yield return new ValidationResult("match_transport_fallback requires native_match_relay.", [nameof(MatchTransportFallback)]);
        if (NativeMatchRelay && (GameProtocol is null || !GameProtocol.EndsWith("/game", StringComparison.Ordinal)
            || RelayProtocol != GameProtocol[..^5] + "/relay"))
            yield return new ValidationResult("native_match_relay requires game and relay protocols owned by the same build.",
                [nameof(GameProtocol), nameof(RelayProtocol)]);

        if (RoomState is < 2 or > 6)
        {
            yield return new ValidationResult(
                $"room_state must be one of the recovered room states 2..6; {RoomState} is not one. "
                + "4 is the in-match state the kind-2 branch requires; 2 is the waiting-room state.",
                [nameof(RoomState)]);
        }

        if (!IPAddress.TryParse(RelayAdvertiseIpv4, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            yield return new ValidationResult(
                "relay_advertise_ipv4 must be a dotted IPv4 address.",
                [nameof(RelayAdvertiseIpv4)]);
        }

        // vecB is erased inside the same fold that reads it (sub_818190 clears
        // equipment+64 and the +8/+20/+32 containers before vecA is replayed), so
        // a deployment that sets this list believes it is dressing a character.
        // Refuse it at load instead of shipping a knob that cannot turn anything.
        if (LevelExpThresholds.Count > 0)
        {
            var ordered = LevelExpThresholds[0] == 0 && LevelExpThresholds.Count >= 2;
            for (var i = 1; ordered && i < LevelExpThresholds.Count; i++)
                ordered = LevelExpThresholds[i] > LevelExpThresholds[i - 1];
            if (!ordered)
            {
                yield return new ValidationResult(
                    "level_exp_thresholds must list at least two strictly increasing totals starting at 0.",
                    [nameof(LevelExpThresholds)]);
            }
        }

        if (ModelItemIds.Count > 0)
        {
            yield return new ValidationResult(
                $"model_item_ids is inert on the wire and only the empty list is accepted: the profile-entry fold replays vecB and vecC and then clears equipment+64 and the +8/+20/+32 containers (kr-1256 sub_818190) before vecA is replayed last, so the dresser sub_91F720 never sees a vecB value. Empty means the reference shape; set model_item_ids: [] and take the appearance effect through the S2C 806 equipped view.",
                [nameof(ModelItemIds)]);
        }

        // S2C 585 field 0 is the only gate the client has on writing its own
        // C2S 17 (sub_9E1EB0 case 0x249 writes session+1708; sub_9E7570 line
        // 129 reads it). A start chain with field 0 zero would leave every
        // seat sitting in the room after S2C 608, so it is a hard error.
        if (StartMatches && StartFlag0 == 0)
        {
            yield return new ValidationResult(
                "start_matches requires start_flag0 to be nonzero: S2C 585 field 0 arms session+1708, the C2S 17 release gate the S2C 608 handler checks.",
                [nameof(StartMatches), nameof(StartFlag0)]);
        }
    }
}
