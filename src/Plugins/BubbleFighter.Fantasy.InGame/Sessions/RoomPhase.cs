namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Lifecycle of one in-memory room after create or join.</summary>
public enum RoomPhase
{
    /// <summary>Seats accepted; waiting for ready / host start.</summary>
    Waiting,

    /// <summary>Start flags, relay advertise, timer, and round-init have been sent.</summary>
    Starting,

    /// <summary>At least one seat has sent C2S 17.</summary>
    Playing,

    /// <summary>C2S 103 or C2S 625 opened the result pair.</summary>
    Result,

    /// <summary>Every seat left or the room was torn down.</summary>
    Closed,
}
