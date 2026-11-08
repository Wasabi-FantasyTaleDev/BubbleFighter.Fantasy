using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// The round names a team's Captain, sent to every member as S2C <c>gspCreateCaptain</c> (kr-812 982). The
/// receiving client looks the seat up in its per-seat character table and transforms that character; with no
/// character there it ignores the message (kr-812 sub_4304C0), so the round sends it once the round is playing.
/// </summary>
/// <param name="Slot">The Captain's seat index.</param>
public sealed record RoundCaptainAppointed(int Slot) : IMessage;

/// <summary>
/// A client reports that its own player died in a Captain round, read from C2S <c>gcpDropCaptain</c>
/// (kr-812 2018, kr-812 sub_42BCE0). Every local death reports, not only the Captain's, so the round decides
/// whether the seat was a Captain.
/// </summary>
/// <param name="Slot">The seat the client names as its own.</param>
public sealed record RoundCaptainDropped(int Slot) : IMessage;
