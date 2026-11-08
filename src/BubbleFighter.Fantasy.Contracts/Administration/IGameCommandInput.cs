using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Administration;

/// <summary>Accepts authenticated command text through a gameplay owner's shared chat budget and command dispatcher.</summary>
public interface IGameCommandInput
{
    /// <summary>Returns whether this owner holds the exact live game session, rather than a matching numeric identifier.</summary>
    bool OwnsSession(ISession session);

    /// <summary>Executes bare command text as this game's player, using the same permissions and rate window as chat.</summary>
    ValueTask<CommandResult> ExecuteTextAsync(ISession session, string command, CancellationToken cancellationToken);
}
