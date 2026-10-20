namespace BubbleFighter.Fantasy.Contracts.Wallet;

/// <summary>
/// A player's two balances. The client keeps them in its user-info block: Lucci at
/// <c>dword_18E56E8+0x4C</c> and Cash at <c>+0x50</c>, filled from the fifth and sixth
/// <c>i32</c> of S2C 106 <c>gspUserMoneyStat</c>; the other four fields of that packet are
/// UI switches built when the packet is sent, not player data.
/// </summary>
/// <param name="UserNum">Login <c>players.user_num</c>.</param>
/// <param name="Lucci">The in-game currency balance.</param>
/// <param name="Cash">The cash currency balance.</param>
public sealed record PlayerWallet(long UserNum, int Lucci, int Cash);
