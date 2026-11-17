using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>
    /// Gets or sets whether player-hosted prize rooms (<c>is_user_host</c>) are enabled for this build.
    /// Defaults to <see langword="false"/> so builds without verified user-host tables stay disabled.
    /// </summary>
    public bool UserHostEnabled { get; set; }

    /// <summary>Gets or sets the minimum Lucci prize allowed in a user-host prize room.</summary>
    public uint UserHostMinPrize { get; set; } = 1_000;

    /// <summary>Gets or sets the maximum Lucci prize allowed in a user-host prize room.</summary>
    public uint UserHostMaxPrize { get; set; } = 100_000;

    /// <summary>Gets or sets the server fee percentage deducted from settled prizes (0..99).</summary>
    [Range(0, 99)]
    public int UserHostFeePercent { get; set; } = 10;

    /// <summary>
    /// Gets or sets this server's cluster node id (the cluster node row's <c>node_id</c>), recorded on every prize
    /// escrow; empty on a single server. Startup recovery refunds only this node's escrows, so a node that restarts
    /// never refunds a match another node sharing the database is still playing.
    /// </summary>
    [MaxLength(64)]
    public string UserHostNodeId { get; set; } = "";

    /// <summary>
    /// Gets or sets the pre-match countdown duration in milliseconds after <c>gspUserHostStart(0)</c>
    /// (kr-812 sub_5C6890 / tw-454 sub_7E06F0 wait 5000 ms before entering the round).
    /// </summary>
    [Range(0, 60_000)]
    public int UserHostCountdownMs { get; set; } = 5_000;

    /// <summary>
    /// Gets or sets the primary tournament ticket item id consumed when starting a prize room
    /// (<c>101164</c> in ShopItem2.xml and kr-812 sub_5BD770 / kr-1256 sub_970590).
    /// </summary>
    [Range(1, int.MaxValue)]
    public int UserHostTicketItemId { get; set; } = 101_164;

    /// <summary>
    /// Gets or sets an optional secondary tournament ticket item id accepted when the primary ticket
    /// is absent (<c>101859</c> in ShopItem2.xml and kr-1256 sub_970590); zero disables the fallback.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int UserHostCaptureTicketItemId { get; set; } = 101_859;

    /// <summary>
    /// Gets or sets the number of playable slots (<c>0 .. playable - 1</c>) exposed for competitors
    /// in a prize room, leaving the referee at the dialect referee slot (<c>12</c>).
    /// </summary>
    [Range(2, PlayRoom.RoomSeatCount - 1)]
    public int UserHostPlayableSeats { get; set; } = 12;

    /// <summary>
    /// Gets or sets the team PvP modes permitted in user-host prize rooms.
    /// Empty means all team-based PvP modes are rejected by policy (<see cref="UserHostStartStatus.PolicyRejected"/>).
    /// </summary>
    [MaxLength(64)]
    public int[] UserHostAllowedModes { get; set; } =
    [
        (int)GameModeId.Pvp,
        (int)GameModeId.Item,
        (int)GameModeId.PvpStarTeam,
        (int)GameModeId.Sniper,
        (int)GameModeId.TeamSurvival,
        (int)GameModeId.SniperTeamSurvival,
        (int)GameModeId.Bomb,
        (int)GameModeId.Captain,
        (int)GameModeId.SuddenSurvival,
        (int)GameModeId.Snowfight,
        (int)GameModeId.ControlPoint,
        (int)GameModeId.Shotgun,
        (int)GameModeId.AllPang,
        (int)GameModeId.JumpJump,
        (int)GameModeId.Psionic,
        (int)GameModeId.BnB,
    ];

    private IEnumerable<ValidationResult> ValidateUserHost()
    {
        if (UserHostAllowedModes is null
            || UserHostAllowedModes.Any(mode => mode is <= 0 or > ushort.MaxValue)
            || UserHostAllowedModes.Distinct().Count() != UserHostAllowedModes.Length)
        {
            yield return new ValidationResult(
                "user_host_allowed_modes must contain distinct positive mode identifiers.",
                [nameof(UserHostAllowedModes)]);
        }

        if (!UserHostEnabled)
            yield break;

        if (UserHostMinPrize == 0 || UserHostMinPrize > UserHostMaxPrize)
        {
            yield return new ValidationResult(
                "user_host_min_prize must be positive and no greater than user_host_max_prize when user_host_enabled is true.",
                [nameof(UserHostMinPrize), nameof(UserHostMaxPrize)]);
        }

        if (UserHostFeePercent is < 0 or >= 100)
        {
            yield return new ValidationResult(
                "user_host_fee_percent must be in [0, 99] when user_host_enabled is true.",
                [nameof(UserHostFeePercent)]);
        }

        if (UserHostPlayableSeats % 2 != 0 || UserHostPlayableSeats is < 2 or >= PlayRoom.RoomSeatCount)
        {
            yield return new ValidationResult(
                $"user_host_playable_seats must be an even count in [2, {PlayRoom.RoomSeatCount - 1}] when user_host_enabled is true.",
                [nameof(UserHostPlayableSeats)]);
        }

        if (UserHostTicketItemId <= 0 || UserHostCaptureTicketItemId < 0)
        {
            yield return new ValidationResult(
                "user_host_ticket_item_id must be positive and user_host_capture_ticket_item_id must be non-negative.",
                [nameof(UserHostTicketItemId), nameof(UserHostCaptureTicketItemId)]);
        }

        if (UserHostAllowedModes is not null && UserHostAllowedModes.Length == 0)
        {
            yield return new ValidationResult(
                "user_host_allowed_modes must list at least one mode when user_host_enabled is true.",
                [nameof(UserHostAllowedModes)]);
        }
    }
}
