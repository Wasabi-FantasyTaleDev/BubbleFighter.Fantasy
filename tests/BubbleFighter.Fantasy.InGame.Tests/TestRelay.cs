using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.MatchRelay;
using Microsoft.Extensions.Logging.Abstractions;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The match-relay row's handlers over a test's room registry, wired the way the two rows are at load.</summary>
internal sealed class TestRelay
{
    internal TestRelay(RoomRegistry registry, bool broadcastMovementSamples = false, bool echoMovementReports = false,
        IPlayerInventoryState? playerState = null)
    {
        Roster = new RoomRoster(registry, NullLogger.Instance, CancellationToken.None);
        Rounds = new RoundStates(Roster, 64);
        var options = new MatchRelayOptions
        {
            BroadcastMovementSamples = broadcastMovementSamples,
            EchoMovementReports = echoMovementReports,
        };
        Flows = new MatchRelayFlow(options, Roster, Rounds, NullLogger.Instance, playerState);
        Actions = new MatchActionRelay(Roster, Roster, NullLogger.Instance, CancellationToken.None);
    }

    internal RoomRoster Roster { get; }

    internal RoundStates Rounds { get; }

    internal MatchRelayFlow Flows { get; }

    internal MatchActionRelay Actions { get; }

    /// <summary>The merged movement of a seat in its room's current round.</summary>
    internal PlayerMovementState? MovementOf(PlayRoom room, RoomSeat seat) =>
        Rounds.For(Roster.Find(room.Id)!).Seat(seat.Slot).Movement;
}
