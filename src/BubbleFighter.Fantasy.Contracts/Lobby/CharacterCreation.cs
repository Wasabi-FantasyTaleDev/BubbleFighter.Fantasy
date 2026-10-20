using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Asks the client to enter its native character creation page before login.</summary>
public sealed record CharacterCreationOpened : IMessage;

/// <summary>Checks the display name entered on the native creation page.</summary>
/// <param name="Nickname">The requested display name.</param>
public sealed record CharacterNameRequested(string Nickname) : IMessage;

/// <summary>Returns whether the entered name can be used.</summary>
/// <param name="Result">The client result code; zero accepts the name.</param>
/// <param name="Nickname">The name the client checked.</param>
public sealed record CharacterNameReplied(int Result, string Nickname) : IMessage;

/// <summary>Submits the initial character and display name.</summary>
/// <param name="Nickname">The chosen display name.</param>
/// <param name="CharacterType">The native character type index.</param>
public sealed record CharacterCreateRequested(string Nickname, int CharacterType) : IMessage;

/// <summary>Advances or rejects the client's character creation submission.</summary>
/// <param name="Result">The native result code; zero advances the page.</param>
public sealed record CharacterCreateReplied(int Result) : IMessage;

/// <summary>Requests normal login after the native creation animation finishes.</summary>
public sealed record CharacterLoginRequested : IMessage;

/// <summary>Reports successful completion of the active initial tutorial.</summary>
public sealed record InitialTutorialCompleted : IMessage;
