using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>A reserved native actor, with no connection, login, loading barrier or reward account.</summary>
internal sealed class RoomBot(RoomMemberView appearance)
{
    internal RoomMemberView Appearance { get; set; } = appearance;
    internal PlayerMovementState? Movement { get; set; }
    internal ParticipantLife Life { get; } = new();
}
