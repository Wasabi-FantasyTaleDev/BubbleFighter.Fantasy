namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>The client policy a running Launcher listener sends, for management views that show what players receive.</summary>
public interface IClientPolicyView
{
    /// <summary>Gets the validated policy the listener started with.</summary>
    ClientPolicy Current { get; }
}
