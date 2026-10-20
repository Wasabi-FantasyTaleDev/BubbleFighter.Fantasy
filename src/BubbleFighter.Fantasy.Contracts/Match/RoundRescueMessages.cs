using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Describes one native rescue presentation and the arguments applied to its map spawners.</summary>
public sealed record RoundRescueMission(int MissionId, int StartTimeMs, int DurationMs, IReadOnlyList<string> Arguments);

/// <summary>Installs rescue definitions before map initialization or refreshes timing between missions.</summary>
public sealed record RoundRescueMissionsPrepared(IReadOnlyList<RoundRescueMission> Missions) : IMessage;

/// <summary>Activates a rescue by its zero-based index in the prepared mission list.</summary>
public sealed record RoundRescueMissionStarted(int MissionIndex) : IMessage;

/// <summary>Fails the active rescue and selects the following prepared mission index.</summary>
public sealed record RoundRescueMissionFailed(int NextMissionIndex) : IMessage;

/// <summary>Completes the specified rescue and invokes its native success presentation.</summary>
public sealed record RoundRescueMissionSucceeded(int MissionIndex) : IMessage;

/// <summary>Reports the death of a citizen or hostile actor associated with a rescue mission.</summary>
public sealed record RoundRescueProgressReported(int MissionIndex, int CharacterType) : IMessage;
