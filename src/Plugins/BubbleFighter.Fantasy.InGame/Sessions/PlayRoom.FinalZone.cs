namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    /// <summary>Gets or sets the current match's elimination publication state; the start generation fences reuse.</summary>
    internal FinalZoneRoundState? FinalZone { get; set; }
    internal RoomDeadlineClock? FinalZoneWaterClock { get; set; }
    internal RoomDeadlineClock? FinalZoneDroneClock { get; set; }

    private void CancelModeClocks()
    {
        ExtremeRacingClock?.Cancel();
        FinalZoneWaterClock?.Cancel();
        FinalZoneDroneClock?.Cancel();
    }
}
