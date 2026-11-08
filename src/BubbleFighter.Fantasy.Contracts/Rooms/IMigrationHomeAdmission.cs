using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Restores the idle scene after a retained account returns to its home node.</summary>
public interface IMigrationHomeAdmission
{
    /// <summary>Gets the client build whose scene encoders this service owns.</summary>
    string ClientBuild { get; }

    /// <summary>Installs stored appearance in the requested idle scene without replaying login rewards; returns false when ineligible.</summary>
    /// <param name="session">The promoted connection whose retained identity has been installed.</param>
    /// <param name="scene">The departing idle scene; builds without plaza encoders restore the main menu.</param>
    /// <param name="cancellationToken">Cancels scene installation.</param>
    ValueTask<bool> EnterAsync(ISession session, SessionPhase scene, CancellationToken cancellationToken);
}
