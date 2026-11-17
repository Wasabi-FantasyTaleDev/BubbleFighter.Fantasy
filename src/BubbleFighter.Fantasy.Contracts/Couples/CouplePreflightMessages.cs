using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Couples;

/// <summary>Checks whether the native invitation selector may open, without creating or reserving a proposal.</summary>
public sealed record CoupleInviterStatusRequested : IMessage;

/// <summary>Checks the target chosen before the native confirmation dialog sends its ring and message.</summary>
/// <param name="Selected">Whether the client selected a target.</param>
/// <param name="Target">The requested nickname; the server resolves the current authenticated fighter.</param>
public sealed record CoupleInitialInviteRequested(bool Selected, string Target) : IMessage;

/// <summary>The invitation selector's own eligibility result, independent of final proposal validation.</summary>
public enum CoupleInviterStatus
{
    /// <summary>The sender may choose a target and proceed to confirmation.</summary>
    Ready,
    /// <summary>The sender is entering or playing a round and cannot open the invitation selector.</summary>
    Unavailable,
    /// <summary>The sender already belongs to a couple.</summary>
    Paired,
    /// <summary>The sender already has a pending outgoing invitation.</summary>
    Busy,
}

/// <summary>The native preflight reply which opens the target selector or explains its refusal.</summary>
/// <param name="Status">The sender's current eligibility.</param>
public sealed record CoupleInviterStatusPublished(CoupleInviterStatus Status) : IMessage;

/// <summary>Opens the final proposal confirmation; it grants no relationship, ring ownership or recipient consent.</summary>
/// <param name="Target">The canonical nickname of the current target.</param>
public sealed record CoupleConfirmationReady(string Target) : IMessage;
