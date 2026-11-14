namespace BubbleFighter.Fantasy.InGame.Sessions;

public sealed partial class PlayRoom
{
    /// <summary>Gets or sets the current match's elimination publication state; the start generation fences reuse.</summary>
    internal FinalZoneRoundState? FinalZone { get; set; }
    internal RoomDeadlineClock? FinalZoneWaterClock { get; set; }
    internal RoomDeadlineClock? FinalZoneDroneClock { get; set; }
    /// <summary>Retains terminal-publication cleanup through state reset and room shutdown.</summary>
    internal Task FinalZoneCleanupTask { get; set; } = Task.CompletedTask;

    private void CancelModeClocks()
    {
        ExtremeRacingClock?.Cancel();
        ExtremeRacingCannonClock?.Cancel();
        BnBClock?.Cancel();
        FinalZoneWaterClock?.Cancel();
        FinalZoneDroneClock?.Cancel();
    }
}
