using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>The local fighter entered a numbered objective or left the objective area (-1).</summary>
public sealed record RoundControlPointChanged(int Node) : IMessage;

/// <summary>Ownership, capture progress and the waiting-room seats standing on one objective.</summary>
public sealed record ControlPointView(int Owner, int CapturingTeam, IReadOnlyList<int> FirstTeamSeats,
    IReadOnlyList<int> SecondTeamSeats, int FirstTeamProgress, int SecondTeamProgress);

/// <summary>The ordered node state consumed by the native Control Point strategy.</summary>
public sealed record RoundControlPointsUpdated(IReadOnlyList<ControlPointView> Nodes) : IMessage;

/// <summary>Publishes objective state and exposes the exact number of records this client's HUD accepts.</summary>
public interface IControlPointSnapshotSender : IMessageSender<RoundControlPointsUpdated>
{
    /// <summary>Gets the count of ordered records in one complete native snapshot, including unused nodes.</summary>
    int NodeCount { get; }
}
