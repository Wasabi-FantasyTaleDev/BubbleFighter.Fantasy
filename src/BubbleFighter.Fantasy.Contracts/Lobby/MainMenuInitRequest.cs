using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Requests the main-menu session signature; the message has no gameplay fields.</summary>
public sealed record MainMenuInitRequest : IMessage;

/// <summary>Installs an empty session signature for main-menu initialization.</summary>
public sealed record EmptySessionSignature : IMessage;
