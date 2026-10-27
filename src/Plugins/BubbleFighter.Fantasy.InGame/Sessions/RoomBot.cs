using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>A reserved native actor, with no connection, login, loading barrier or reward account.</summary>
internal sealed class RoomBot(RoomMemberView appearance)
{
    internal RoomMemberView Appearance { get; set; } = appearance;
    internal PlayerMovementState? Movement { get; set; }
    internal SeatLife Life { get; set; }
    internal bool DeathCleaned { get; set; }
    internal ISession? ResetRelay { get; set; }
}
