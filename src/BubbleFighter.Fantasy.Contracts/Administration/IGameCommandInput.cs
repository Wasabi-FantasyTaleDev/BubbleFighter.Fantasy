using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Administration;

/// <summary>Accepts authenticated command text through a gameplay owner's shared chat budget and command dispatcher.</summary>
public interface IGameCommandInput
{
    /// <summary>Returns whether this owner holds the exact live game session, rather than a matching numeric identifier.</summary>
    bool OwnsSession(ISession session);

    /// <summary>Executes bare command text as this game's player, using the same permissions and rate window as chat.</summary>
    /// <param name="session">The player's game session.</param>
    /// <param name="command">The bare command text.</param>
    /// <param name="source">Where the text came from, such as <see cref="CommandSource.Nirinsou"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<CommandResult> ExecuteTextAsync(ISession session, string command, CommandSource source, CancellationToken cancellationToken);
}
