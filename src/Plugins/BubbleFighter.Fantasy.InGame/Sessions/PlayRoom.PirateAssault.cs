using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    internal int PirateAssaultMasterRevision { get; private set; }
    internal PirateAssaultWaveResult? PirateAssaultResult { get; set; }
}

/// <summary>The host's terminal wave notice precedes its stage result on the same game connection.</summary>
internal sealed record PirateAssaultWaveResult(int Generation, RoomSeat Master, RoundNpcOperation Operation, IMessageSender<RoundNpcAcknowledged> Reply)
{
    // A replay after a partial publication skips recipients whose native wave state already changed.
    internal HashSet<RoomSeat> Notified { get; } = [];
}
