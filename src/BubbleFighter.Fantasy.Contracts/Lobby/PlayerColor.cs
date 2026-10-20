using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Notifies shared room and plaza owners that a player's durable avatar color changed.</summary>
/// <param name="UserNum">The owner of the committed color.</param>
/// <param name="ColorIndex">The material color selected by the owned spray definition.</param>
public sealed record PlayerColorChanged(int UserNum, ushort ColorIndex) : IEvent;

/// <summary>Refreshes native local color while retaining the current nickname, level and wallet values.</summary>
/// <param name="Player">The current persisted identity and avatar color.</param>
/// <param name="Wallet">The current wallet resolved for the same player.</param>
public sealed record UserStatUpdated(PlayerRecord Player, PlayerWallet Wallet) : IMessage;
