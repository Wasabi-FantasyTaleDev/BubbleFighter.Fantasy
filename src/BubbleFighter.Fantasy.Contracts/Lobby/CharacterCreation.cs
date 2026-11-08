using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Asks the client to enter its native character creation page before login.</summary>
public sealed record CharacterCreationOpened : IMessage;

/// <summary>Checks the display name entered on the native creation page.</summary>
/// <param name="Nickname">The requested display name.</param>
public sealed record CharacterNameRequested(string Nickname) : IMessage;

/// <summary>
/// Native character-creation results: kr-812 sub_81A0E0 / sub_819C20, cn-717 sub_8A0470 / sub_89FF30,
/// cn-279 sub_6FC5E0 / sub_6FCBA0 and kr-1256 sub_9A4E90 / sub_9A46A0 select the corresponding dialogs.
/// The modern creation reply has no dedicated case for <see cref="CreationFailed"/> and uses its generic refusal.
/// </summary>
public static class CharacterCreationResult
{
    /// <summary>The name or character is accepted and the page advances.</summary>
    public const int Accepted = 0;

    /// <summary>The fighter name is already registered.</summary>
    public const int NameTaken = 1;

    /// <summary>The character record could not be created; the player may retry.</summary>
    public const int CreationFailed = 2;

    /// <summary>The requested fighter name cannot be used.</summary>
    public const int NameUnusable = 3;

    /// <summary>The selected character cannot be used.</summary>
    public const int CharacterUnusable = 4;

    /// <summary>The fighter name has an unsupported length.</summary>
    public const int NameLength = 6;

    /// <summary>The fighter name contains prohibited language; reserved until a configurable filter exists.</summary>
    public const int NameProfane = 7;
}

/// <summary>Returns whether the entered name can be used.</summary>
/// <param name="Result">The client result code from <see cref="CharacterCreationResult"/>; zero accepts the name.</param>
/// <param name="Nickname">The name the client checked.</param>
public sealed record CharacterNameReplied(int Result, string Nickname) : IMessage;

/// <summary>Submits the initial character and display name.</summary>
/// <param name="Nickname">The chosen display name.</param>
/// <param name="CharacterType">The native character type index.</param>
public sealed record CharacterCreateRequested(string Nickname, int CharacterType) : IMessage;

/// <summary>Advances or rejects the client's character creation submission.</summary>
/// <param name="Result">The native result code from <see cref="CharacterCreationResult"/>; zero advances the page.</param>
public sealed record CharacterCreateReplied(int Result) : IMessage;

/// <summary>Requests normal login after the native creation animation finishes.</summary>
public sealed record CharacterLoginRequested : IMessage;

/// <summary>Reports successful completion of the active initial tutorial.</summary>
public sealed record InitialTutorialCompleted : IMessage;
