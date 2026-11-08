using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Party;

/// <summary>
/// A party member asks for the party's match to start, read from C2S <c>gcpReqPartyQuickStart</c>. The wire is one
/// u32 kind (kr-812 sub_80A910); the client's own validator kr-812 sub_812AD0 only lets the party leader send kinds
/// 1 and 2, so a leader kind from any other member is refused with the cancel answer.
/// </summary>
/// <param name="Kind">The requested mode family: 1 starts mode 101, 2 starts mode 16 (kr-812 sub_68E390 and
/// kr-812 sub_68E580); 4 only asks for the wait dialog (kr-812 sub_68EF80).</param>
public sealed record PartyQuickStartRequested(int Kind) : IMessage;

/// <summary>
/// The server's quick-start answer, written as S2C <c>gspRepPartyQuickStart</c>: one u32 kind (kr-812 sub_68EF10
/// reads it in the stage dispatcher kr-812 sub_68D530 case 0x134). Every member receives the accepted kind, each
/// client runs its stage start, and the leader's client then sends the ordinary room-create request, which the
/// party follow flow serves. Kind 0 is the refusal/cancel form: the stage-local consumer kr-812 sub_6875C0 exits
/// the stage on it. Kind 3 starts mode 13 in the same dispatch though no sender produces it, and kind 4 shows the
/// wait dialog kr-812 sub_68F010.
/// </summary>
/// <param name="Kind">The accepted kind back to the clients, or 0 to cancel.</param>
public sealed record PartyQuickStartAnswered(int Kind) : IMessage;
