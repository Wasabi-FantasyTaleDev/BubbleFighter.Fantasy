using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Where a dropped Lucci item appears and how it is thrown, as the killing player's client reported it.</summary>
/// <param name="X">The x position.</param>
/// <param name="Y">The y position.</param>
/// <param name="Z">The z position.</param>
/// <param name="VelocityX">The x launch velocity.</param>
/// <param name="VelocityY">The y launch velocity.</param>
/// <param name="VelocityZ">The z launch velocity.</param>
public sealed record RoundLucciPoint(float X, float Y, float Z, float VelocityX, float VelocityY, float VelocityZ);

/// <summary>
/// A round member's client asks for a plain or big Lucci item where a killed NPC stood. The word
/// the client sends last is echoed to the created item unchanged.
/// </summary>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Kind">The client's item kind: 1 plain Lucci or 2 big Lucci.</param>
/// <param name="Tag">The request's last word, passed back in <see cref="RoundLucciCreated.Tag"/>.</param>
public sealed record RoundLucciDropRequested(RoundLucciPoint Point, int Kind, int Tag) : IMessage;

/// <summary>
/// A round member's client reports that a killed NPC should leave a horde award item. The request
/// carries no item kind: the round chooses the award item.
/// </summary>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Tag">The request's last word, passed back in <see cref="RoundLucciCreated.Tag"/>.</param>
public sealed record RoundHordeAwardRequested(RoundLucciPoint Point, int Tag) : IMessage;

/// <summary>Creates one Lucci or horde-award item in every round member's client.</summary>
/// <param name="ObjectId">The server-assigned object id a later pickup reports back.</param>
/// <param name="Kind">The client item kind: 1 Lucci, 2 big Lucci, 3/4/5 horde award worth 1/5/10.</param>
/// <param name="Point">The position and launch velocity.</param>
/// <param name="Tag">The word the request carried, passed through unchanged.</param>
public sealed record RoundLucciCreated(int ObjectId, int Kind, RoundLucciPoint Point, int Tag) : IMessage;

/// <summary>A round member's client reports that its character touched a Lucci item.</summary>
/// <param name="CharacterObjectId">The touching character's object id as the client sent it.</param>
/// <param name="ObjectId">The item's object id from <see cref="RoundLucciCreated.ObjectId"/>.</param>
public sealed record RoundLucciPickupRequested(int CharacterObjectId, int ObjectId) : IMessage;

/// <summary>Removes a Lucci item from every round member's client after one player's pickup was accepted.</summary>
/// <param name="CharacterObjectId">The eater's character object id, echoed from the accepted request.</param>
/// <param name="ObjectId">The consumed item's object id.</param>
public sealed record RoundLucciPicked(int CharacterObjectId, int ObjectId) : IMessage;
