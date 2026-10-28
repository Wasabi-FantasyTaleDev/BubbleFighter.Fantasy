using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Party;

/// <summary>Requests scene following after the party owner has committed a new membership.</summary>
/// <param name="LeaderUserNum">The committed party leader, whose live scene must be checked again at admission.</param>
/// <param name="AcknowledgeVisit">Whether this direct visit needs its native completion reply.</param>
public sealed record PartySceneFollowRequested(int LeaderUserNum, bool AcknowledgeVisit = false) : IMessage;

/// <summary>Completes a direct visit after the scene owner has admitted the visitor.</summary>
/// <param name="Target">The committed leader's displayed nickname.</param>
public sealed record PartyVisitSucceeded(string Target) : IMessage;

/// <summary>Completes a scene move accepted through an invitation on clients that have a success reply.</summary>
/// <param name="Target">The leader's displayed nickname.</param>
public sealed record PartyInvitationMoveSucceeded(string Target) : IMessage;
