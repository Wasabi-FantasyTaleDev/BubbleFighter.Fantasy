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
}
