using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Installs an existing player on a destination connection after independently claiming its account authority.</summary>
public interface IRetainedPlayerAdmission
{
    /// <summary>Gets the game protocol whose lobby owns the installed player registrations.</summary>
    string Protocol { get; }

    /// <summary>Requires retained account authority, loads stored state without replaying daily login grants, and installs the player.</summary>
    ValueTask<IPlayerSession> InstallAsync(ISession session, long userNum, CancellationToken cancellationToken);

    /// <summary>Removes this lobby's player registration after a failed migration; account authority is released separately.</summary>
    void Remove(ISession session);
}
