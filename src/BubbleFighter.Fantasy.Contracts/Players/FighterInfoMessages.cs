using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Contracts.Players;

/// <summary>
/// A player asks to see another player's fighter information, the right-click "view info" item of the player menu
/// (legacy C2S <c>gcpReqFighterInfo</c>, modern C2S <c>gcpReqFighterInfo</c> under the build's own number). The client
/// names the target by account id, taken from the clicked row of its room or plaza list.
/// </summary>
/// <param name="TargetUserNum">The account id of the player whose information is wanted.</param>
public sealed record FighterInfoRequested(int TargetUserNum) : IMessage;

/// <summary>
/// The answer to a <see cref="FighterInfoRequested"/> (S2C <c>gspRepFighterInfo</c>). Only the fields a build's reader
/// is proven to show are carried; every other field of the client's record keeps its constructor default.
/// </summary>
/// <param name="Fighter">
/// The requested player as the server knows them, or <see langword="null"/> when the account is not logged in. A
/// <see langword="null"/> answer is written as the build's own failure result, never as another player's record.
/// </param>
public sealed record FighterInfoReplied(PlayerIdentity? Fighter) : IMessage;
