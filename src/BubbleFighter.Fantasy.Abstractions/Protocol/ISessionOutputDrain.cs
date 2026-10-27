namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>Allows an operation to await queued outbound frames before deliberately closing a connection.</summary>
public interface ISessionOutputDrain
{
    /// <summary>Waits until frames queued before this call have completed their transport writes.</summary>
    /// <param name="cancellationToken">Cancels the wait without retracting already queued frames.</param>
    /// <returns>Completion of the ordered transport fence, not an acknowledgement of client receipt or rendering.</returns>
    /// <remarks>A closed session, full outbound queue or failed write faults the operation. Callers supply their validated deadline.</remarks>
    ValueTask DrainOutputAsync(CancellationToken cancellationToken);
}
