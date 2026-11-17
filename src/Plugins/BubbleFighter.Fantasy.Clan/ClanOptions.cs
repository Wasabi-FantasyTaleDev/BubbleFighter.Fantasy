using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Configuration for the <see cref="ClanPlugin"/> row: the typed dispatch protocol, the stored-snapshot bound
/// and the per-connection open bound. The build's packet layouts belong to the row's dialect adapter, so this
/// plugin stays build-neutral.
/// </summary>
public sealed class ClanOptions
{
    /// <summary>Gets or sets the game-connection protocol name whose typed messages this row handles.</summary>
    [Required]
    public string Protocol { get; set; } = "kr-1256/game";

    /// <summary>Gets or sets the largest stored clan or membership snapshot accepted by this row.</summary>
    [Range(1, int.MaxValue - 1)]
    public int MaxSnapshotEntries { get; set; } = 4096;

    /// <summary>
    /// Gets or sets how many live connections may retain a clan-open record in this row.
    /// Matches the game listener default so the table cannot outgrow accepted sockets.
    /// </summary>
    [Range(1, 65536)]
    public int MaxTrackedOpens { get; set; } = 512;

    /// <summary>
    /// Gets or sets whether a clan-connection login must present the ticket this row put in the game session's
    /// passport. On, the account a clan connection acts for is proven by the game login behind it. Off is the
    /// client's own flow and nothing more: the passport carries no ticket and the login is accepted for the
    /// account it states, provided a live game session of that account asked for a passport.
    /// </summary>
    public bool Authentication { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a passport is sent right after the game login. The kr-812 family asks for one only on the
    /// clan server's hello after a clan login result marked the connection ready (kr-812 sub_498790 and cn-717
    /// sub_4C6E70 case 1002 test the flag cn-717 sub_4C6D20 sets after case 1006), so it never asks before its first
    /// login; its passport handler logs in at once (cn-717 sub_89F890 -> sub_4C7310 stamps 1005). kr-1256 asks on every
    /// hello and leaves this off. YAML key <c>passport_on_login</c>.
    /// </summary>
    public bool PassportOnLogin { get; set; }

    /// <summary>
    /// Gets or sets how many days after the master's request a clan is disbanded. The client only says "at the next
    /// maintenance time" (kr-1256 sub_52E860) and states no number, so this is server policy.
    /// </summary>
    [Range(1, 3650)]
    public int DisbandDays { get; set; } = 7;

    /// <summary>Gets or sets how often, in seconds, this row deletes the clans whose disband date has passed.</summary>
    [Range(1, 86400)]
    public int DisbandCheckSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets how many posts one board page holds. The client lists whatever a page carries and pages by post id
    /// (kr-1256 sub_584250), so the page length is server policy.
    /// </summary>
    [Range(1, 100)]
    public int BoardPageSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets how long a passport's ticket may be redeemed, in seconds. The client forwards the passport
    /// as its clan login in the same frame it receives it (kr-1256 sub_9D2FF0 case 21), so only the network
    /// delay between the two connections has to fit.
    /// </summary>
    [Range(1, 3600)]
    public int TicketSeconds { get; set; } = 60;
}
