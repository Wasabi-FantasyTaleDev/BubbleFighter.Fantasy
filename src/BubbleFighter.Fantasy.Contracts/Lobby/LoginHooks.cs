using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Asks active gameplay owners whether a login-shaped packet belongs to a match connection.
/// Waterfall handlers return true to claim it; the terminal returns false for ordinary login.</summary>
/// <param name="Protocol">The receiving connection protocol.</param>
/// <param name="Session">The connection being classified.</param>
public sealed record LoginConnectionRouting(string Protocol, ISession Session) : IEvent;

/// <summary>Awaits optional equipment publication after the login scene is installed and before
/// subsequent appearance packets. Handlers preserve the waterfall result and propagate failures.</summary>
/// <param name="Protocol">The codec's connection protocol.</param>
/// <param name="Session">The connection receiving the login sequence.</param>
/// <param name="Login">The authoritative state supplied by login logic.</param>
/// <param name="CharacterItemId">The character selected by the codec for the installed scene.</param>
public sealed record LoginEquipmentRequested(string Protocol, ISession Session, LoginSucceeded Login, int CharacterItemId) : IEvent;
