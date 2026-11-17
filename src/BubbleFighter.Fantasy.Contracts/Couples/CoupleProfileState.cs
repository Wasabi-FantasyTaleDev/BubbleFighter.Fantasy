using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Couples;

/// <summary>The committed relationship fields published in the player's own fighter profile.</summary>
/// <param name="CreatedAt">The instant this relationship was formed, retained through ring changes.</param>
/// <param name="DisplayRingItemId">The build's display ring resolved from the owned invitation item, or zero when its data has no display model.</param>
/// <param name="PartnerOnline">Whether the partner is currently online in this channel.</param>
public sealed record CoupleProfileState(DateTimeOffset CreatedAt, int DisplayRingItemId, bool PartnerOnline);

/// <summary>A connection's current couple profile, read again whenever its user statistics are published.</summary>
public interface ICoupleProfile
{
    /// <summary>Gets the current committed relationship, or null after breakup or when no relationship exists.</summary>
    CoupleProfileState? Current { get; }

    /// <summary>Gets the partner identity projected into this connection's room and plaza seat records.</summary>
    CoupleSceneState? Scene => null;

    /// <summary>Captures the partner identity and relationship details together for another player's inspection.</summary>
    CouplePartnerProfile? CapturePartner() => null;
}

/// <summary>A detached partner identity and relationship profile captured from the same committed state.</summary>
/// <param name="Partner">The persistent partner identity.</param>
/// <param name="Relationship">The creation date, display ring and current partner presence.</param>
public sealed record CouplePartnerProfile(CoupleSceneState Partner, CoupleProfileState Relationship);

/// <summary>The committed partner identity used to match two characters in a shared scene.</summary>
/// <param name="PartnerUserNum">The partner's account number, independent of their current scene or online status.</param>
/// <param name="PartnerName">The partner's current display name.</param>
public sealed record CoupleSceneState(long PartnerUserNum, string PartnerName);

/// <summary>Signals a changed relationship identity after the session feature has been updated.</summary>
/// <param name="Session">The authenticated connection whose relationship changed.</param>
/// <param name="Couple">The new partner identity, or null after breakup.</param>
public sealed record CoupleSceneChanged(ISession Session, CoupleSceneState? Couple) : IEvent;

/// <summary>Updates the native own-couple profile independently of the partner-presence notice.</summary>
/// <param name="Profile">The committed relationship, or null to clear the native coupled flag.</param>
public sealed record CoupleProfilePublished(CoupleProfileState? Profile) : IMessage;
