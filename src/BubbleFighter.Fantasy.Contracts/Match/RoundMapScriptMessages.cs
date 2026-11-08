using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Shows or hides a named map object and publishes its script sequence.</summary>
/// <param name="Name">Native byte string identifying the map object, preserved without transcoding.</param>
/// <param name="Visible">Whether the object's collision and presentation are enabled.</param>
/// <param name="Sequence">Opaque map-script sequence carried by the client.</param>
public sealed record RoundMapObjectVisibilityChanged(ReadOnlyMemory<byte> Name, bool Visible, int Sequence) : IMessage;

/// <summary>Creates the map script's named destructible object with its native health value.</summary>
/// <param name="Name">Native byte string identifying the map helper.</param>
/// <param name="HitPoints">Finite non-negative health supplied by the map script.</param>
/// <param name="Sequence">Opaque map-script sequence carried by the client.</param>
public sealed record RoundBreakableMapObjectCreated(ReadOnlyMemory<byte> Name, float HitPoints, int Sequence) : IMessage;
