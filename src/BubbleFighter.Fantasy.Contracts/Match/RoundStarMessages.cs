using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// A round member's client asks for a star where its character dropped one. The bytes are those of
/// <see cref="RoundLucciDropRequested"/>: kr-812 sub_514830 writes the request with gcpReqCreateLucci's serializer.
/// </summary>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Kind">The client's star kind: 1 small star or 2 big star.</param>
/// <param name="Tag">The request's last word, passed back in <see cref="RoundStarCreated.Tag"/>: the drop's reason, which picks the dropped star's look.</param>
public sealed record RoundStarDropRequested(RoundLucciPoint Point, int Kind, int Tag) : IMessage;

/// <summary>Creates one star in every round member's client, the sender included.</summary>
/// <param name="ObjectId">The server-assigned object id a later pickup reports back.</param>
/// <param name="Kind">The star kind: 1 small star, 2 big star.</param>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Tag">
/// The word that picks the star's model and spawn effect. The kr-812 and kr-1256 star constructors give 1, 2, 5, 6 and 7
/// the dropped look and 3 the map look, and load no model for any other value. A dropped star passes the request's word
/// through; a map star carries 3.
/// </param>
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
public sealed record RoundStarReported(int ReporterSlot, RoundStarChange Change) : IMessage
{
    /// <summary>
    /// Gets the explicitly identified affected seat when the dialect has proved that field's meaning;
    /// null retains the original authenticated-sender attribution for unverified report layouts.
    /// </summary>
    public int? NamedReporterSlot { get; init; }
}

/// <summary>
/// One star point of the round's map: a <c>#sp_star_&lt;first&gt;_&lt;period&gt;_&lt;n&gt;</c> or
/// <c>#sp_bigStar_&lt;first&gt;_&lt;period&gt;_&lt;n&gt;</c> helper. The point first gets its star <paramref name="FirstMs"/>
/// after play begins and then checks every <paramref name="PeriodMs"/> whether its star was taken.
/// </summary>
/// <param name="X">The helper's X coordinate.</param>
/// <param name="Y">The helper's Y coordinate.</param>
/// <param name="Z">The helper's Z coordinate.</param>
/// <param name="Kind">The star kind: 1 small star, 2 big star.</param>
/// <param name="FirstMs">The helper name's third segment: milliseconds from the start of play to the first star.</param>
/// <param name="PeriodMs">The helper name's fourth segment: milliseconds between the point's later checks.</param>
public sealed record RoundStarPoint(float X, float Y, float Z, int Kind, int FirstMs, int PeriodMs);

/// <summary>The map star points the host client's map report lists (kr-812 sub_50C840, kr-1256 sub_6DA5D0).</summary>
/// <param name="Points">The points in the report's order.</param>
public sealed record RoundStarPointsReported(IReadOnlyList<RoundStarPoint> Points) : IMessage;

/// <summary>
/// Starts a star round's closing fever in a round member's client. The packet has no body; in modes 5 and 6
/// kr-1256 sub_6ECD60 plays the big star spawn effect and switches to the star fever music.
/// </summary>
public sealed record RoundStarFeverStarted : IMessage;
