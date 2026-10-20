namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Process exit codes of the tool. Every input file must parse and at least one file must be
/// summarised for the run to succeed, so a non-zero code always means the report was not written.
/// </summary>
public enum TrxSummaryExitCode
{
    /// <summary>Every input parsed and a report was written; test or run failures do not change this reporting status.</summary>
    Success = 0,

    /// <summary>An unexpected internal failure; the full exception is written to standard error.</summary>
    UnexpectedError = 1,

    /// <summary>The input list argument or a resource-limit option is missing, invalid, duplicated or unknown.</summary>
    UsageError = 2,

    /// <summary>The input list was unreadable, empty, over the file cap, or named a TRX file that cannot be read.</summary>
    InputError = 3,

    /// <summary>An input file was readable but is not well formed TRX.</summary>
    TrxError = 4,
}
