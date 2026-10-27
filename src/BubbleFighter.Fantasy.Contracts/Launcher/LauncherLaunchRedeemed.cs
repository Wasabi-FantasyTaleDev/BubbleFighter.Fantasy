using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>
/// A game connection redeemed a launcher ticket that belongs to a launch. Launch-scoped services such as the companion
/// bind to <paramref name="Session"/> here instead of guessing from names or addresses; a login refused after this
/// closes the connection, which ends the binding.
/// </summary>
/// <param name="LaunchId">The launch the redeemed game ticket was issued for.</param>
/// <param name="Account">The launcher account.</param>
/// <param name="Build">The client build that redeemed it.</param>
/// <param name="Session">The game session that redeemed it.</param>
public sealed record LauncherLaunchRedeemed(string LaunchId, string Account, string Build, ISession Session) : IEvent;
