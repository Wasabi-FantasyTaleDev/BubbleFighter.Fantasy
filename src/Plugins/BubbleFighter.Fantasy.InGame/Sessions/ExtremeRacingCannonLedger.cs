using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// The cannon commands a room generation has issued and not yet seen answered by a relayed shot. A command makes one
/// client fire one bullet (kr-1256 sub_67B6E0 then kr-1256 sub_6E1E90), so an answer without a command, a second answer
/// to one command or an answer that names another cannon or weapon is not forwarded. A cannon pair has at most one
/// command outstanding, which makes the ledger's size the map's cannon count rather than a free constant. The
/// ledger belongs to one generation's initialization state, so a new generation starts empty. Callers hold the
/// room's gate.
/// </summary>
internal sealed class ExtremeRacingCannonLedger(ExtremeRacingCannonSchedule schedule, uint firstCannonIdentity)
{
    private readonly Dictionary<int, (uint Identity, uint Weapon)> _outstanding = [];

    /// <summary>Gets the most commands that can be outstanding: one per initialized cannon pair.</summary>
    internal int Capacity => schedule.Cannons.Count;

    /// <summary>Gets the number of commands waiting for their shot.</summary>
    internal int Outstanding => _outstanding.Count;

    /// <summary>Returns whether a cannon number has a command that no shot has answered.</summary>
    /// <param name="cannon">The zero-based cannon number.</param>
    internal bool HasOutstanding(int cannon) => _outstanding.ContainsKey(cannon);

    /// <summary>
    /// Returns the identity a command names for a cannon number: the first identity of its pair. The initialization
    /// creates the cannon with the first identity and its disposer with the second (kr-1256 sub_67ADC0 passes the running
    /// counter to the cannon constructor kr-1256 sub_BF3700 before the disposer), and a command resolves its identity to an
    /// ExtremeCannon (kr-1256 sub_67B6E0).
    /// </summary>
    /// <param name="cannon">The zero-based cannon number.</param>
    internal uint CommandIdentity(int cannon) => checked(firstCannonIdentity + 2u * (uint)cannon);

    /// <summary>
    /// Returns whether an identity belongs to this generation's initialized cannon pairs. The initialization assigns two
    /// consecutive identities per pair; which of the two is the cannon rather than its disposer is not established, so
    /// both are accepted.
    /// </summary>
    internal bool IsInitialized(uint identity) => Pair(identity) >= 0;

    /// <summary>
    /// Records a command that is about to be sent. Refused, and nothing recorded, when the identity is not initialized,
    /// the weapon is not in the cannon's resource list or the cannon already has a command outstanding.
    /// </summary>
    /// <param name="identity">The commanded cannon's identity.</param>
    /// <param name="weapon">The commanded weapon.</param>
    internal bool TryIssue(uint identity, uint weapon)
    {
        var pair = Pair(identity);
        if (pair < 0 || !schedule.Lists(pair, weapon) || _outstanding.ContainsKey(pair))
            return false;
        _outstanding.Add(pair, (identity, weapon));
        return true;
    }

    /// <summary>Takes the command a shot answers; the cannon identity and weapon must both match it exactly.</summary>
    /// <param name="identity">The cannon identity the shot names.</param>
    /// <param name="weapon">The weapon the shot names.</param>
    internal bool TryConsume(uint identity, ushort weapon)
    {
        var pair = Pair(identity);
        if (pair < 0 || !_outstanding.TryGetValue(pair, out var command) || command.Identity != identity || command.Weapon != weapon)
            return false;
        _outstanding.Remove(pair);
        return true;
    }

    /// <summary>Drops every outstanding command, for example when its origin leaves; later shots for them are refused.</summary>
    internal void Revoke() => _outstanding.Clear();

    private int Pair(uint identity)
    {
        var offset = (long)identity - firstCannonIdentity;
        return offset >= 0 && offset < 2L * schedule.Cannons.Count ? (int)(offset / 2) : -1;
    }
}
