namespace BubbleFighter.Fantasy.Contracts.Sessions;

/// <summary>The user's UI language supplied by the authenticated launch request for this connection.</summary>
public interface ISessionLanguage
{
    /// <summary>Gets the Launcher UI language; it does not describe the client build or the native rendering language.</summary>
    string LauncherLanguage { get; }
}
