namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Failure that carries the process exit code the tool must return. It is thrown for every
/// condition the caller has to see, so no bad input is ever skipped silently.
/// </summary>
internal sealed class TrxSummaryException : Exception
{
    /// <summary>
    /// Creates the failure with the exit code the process must return.
    /// </summary>
    /// <param name="exitCode">Exit code that describes the failure class.</param>
    /// <param name="message">Message written to standard error.</param>
    internal TrxSummaryException(TrxSummaryExitCode exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    /// <summary>
    /// Exit code the process must return for this failure.
    /// </summary>
    internal TrxSummaryExitCode ExitCode { get; }
}
