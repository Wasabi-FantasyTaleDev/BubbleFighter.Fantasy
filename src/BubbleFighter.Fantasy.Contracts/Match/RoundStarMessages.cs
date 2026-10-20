using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// A round member's client asks for a star where its character dropped one. The bytes are those of
/// <see cref="RoundLucciDropRequested"/>: kr-812 sub_514830 writes the request with gcpReqCreateLucci's serializer.
/// </summary>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Kind">The client's star kind: 1 small star or 2 big star.</param>
/// <param name="Tag">The request's last word (the owner's seat), passed back in <see cref="RoundStarCreated.Tag"/>.</param>
public sealed record RoundStarDropRequested(RoundLucciPoint Point, int Kind, int Tag) : IMessage;

/// <summary>Creates one star in every round member's client, the sender included.</summary>
/// <param name="ObjectId">The server-assigned object id a later pickup reports back.</param>
/// <param name="Kind">The star kind: 1 small star, 2 big star.</param>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Tag">The word the request carried, passed through unchanged.</param>
public sealed record RoundStarCreated(int ObjectId, int Kind, RoundLucciPoint Point, int Tag) : IMessage;

/// <summary>A round member's client reports that its character touched a star.</summary>
/// <param name="CharacterObjectId">The touching character's object id as the client sent it.</param>
/// <param name="ObjectId">The star's object id from <see cref="RoundStarCreated.ObjectId"/>.</param>
public sealed record RoundStarPickupRequested(int CharacterObjectId, int ObjectId) : IMessage;

/// <summary>Removes a star from every round member's client after one player's pickup was accepted.</summary>
/// <param name="CharacterObjectId">The eater's character object id, echoed from the accepted request.</param>
/// <param name="ObjectId">The consumed star's object id.</param>
public sealed record RoundStarPicked(int CharacterObjectId, int ObjectId) : IMessage;

/// <summary>How a client says its own star count changed.</summary>
public enum RoundStarChange
{
    /// <summary>One small star more: a pickup, or one of the stars every client reports at the end of loading.</summary>
    SmallGained,

    /// <summary>One big star more.</summary>
    BigGained,

    /// <summary>One small star fewer: the client dropped it.</summary>
    SmallLost,

    /// <summary>One big star fewer: the client dropped it.</summary>
    BigLost,
}

/// <summary>
/// A round member's client reports a change of its own star count. The client keeps the stars and
/// the server only adds up its reports (kr-812 sub_75CE60, sub_75C680, sub_520A20, sub_661AF0).
/// </summary>
/// <param name="ReporterSlot">The seat the report names.</param>
/// <param name="Change">The change.</param>
public sealed record RoundStarReported(int ReporterSlot, RoundStarChange Change) : IMessage;
