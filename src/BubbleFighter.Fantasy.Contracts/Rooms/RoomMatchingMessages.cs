using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Requests a waiting room in the selected game mode through the native matching entry.</summary>
/// <param name="ModeId">The selected client game mode.</param>
/// <param name="DedicatedQueue">Whether the native entry requests a separate PvE queue stage.</param>
/// <param name="RetryQueue">Whether the native repeat-entry path set its independent queue flag.</param>
public sealed record RoomMatchRequested(int ModeId, bool DedicatedQueue, bool RetryQueue = false) : IMessage;

/// <summary>Displays the native matching search or failure response.</summary>
/// <param name="Searching">Whether the server accepted a search rather than refusing it.</param>
public sealed record RoomMatchNotified(bool Searching) : IMessage;
