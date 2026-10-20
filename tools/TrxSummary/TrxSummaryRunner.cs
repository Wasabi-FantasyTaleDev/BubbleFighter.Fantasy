namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Command line driver: reads the input list, parses every named TRX file, writes the report, and
/// returns the process exit code. Any bad input fails the run instead of being skipped.
/// </summary>
public static class TrxSummaryRunner
{
    /// <summary>
    /// Default number of TRX file entries accepted; the validated max-files option may override it.
    /// </summary>
    public const int MaxInputFiles = TrxResourceLimits.DefaultMaxFiles;

    /// <summary>
    /// Runs the tool against an explicit command line and output writers.
    /// </summary>
    /// <param name="args">One input list path and optional validated resource-limit switches.</param>
    /// <param name="output">Writer that receives the report on success.</param>
    /// <param name="error">Writer that receives the failure message on any non-zero result.</param>
    /// <returns>The process exit code described by <see cref="TrxSummaryExitCode"/>.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            var (inputList, limits) = TrxResourceLimits.Parse(args);
            string listPath = ResolvePath(inputList);
            IReadOnlyList<string> entries = TrxInputList.Read(listPath, limits);
            var budget = new TrxReadBudget(limits);

            var files = new List<TrxFileSummary>(entries.Count);
            foreach (string entry in entries)
            {
                files.Add(TrxFileLoader.Load(ResolvePath(entry), limits, budget));
            }

            TrxReportWriter.Write(new TrxSummaryReport(listPath, files), output);
            return (int)TrxSummaryExitCode.Success;
        }
        catch (TrxSummaryException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            if (ex.ExitCode == TrxSummaryExitCode.UsageError) WriteUsage(error);
            return (int)ex.ExitCode;
        }
        catch (Exception ex)
        {
            error.WriteLine($"error: unexpected failure: {ex}");
            return (int)TrxSummaryExitCode.UnexpectedError;
        }
    }

    /// <summary>
    /// Writes the usage text that documents the accepted command line.
    /// </summary>
    private static void WriteUsage(TextWriter error)
    {
        error.WriteLine("usage: TrxSummary <input-list> [--max-files N] [--max-list-bytes N]");
        error.WriteLine("                  [--max-file-bytes N] [--max-total-bytes N]");
        error.WriteLine("                  [--max-results N] [--max-text-chars N]");
        error.WriteLine("  <input-list>  text file with one TRX path per line, read in order.");
        error.WriteLine("                Blank lines and lines starting with '#' are ignored.");
        error.WriteLine("                Relative paths are resolved against the current directory.");
        error.WriteLine($"                Default: {MaxInputFiles} entries; duplicate entries are counted and warned.");
        error.WriteLine("                Limits require positive integers; defaults are documented in README.md.");
        error.WriteLine("  stdout        per-assembly counts, totals, failures, and each file's SHA256.");
        error.WriteLine("  success       means report generated, never a test-pass or CI-gate verdict.");
        error.WriteLine($"  exit codes    0 report generated, {(int)TrxSummaryExitCode.UnexpectedError} unexpected, "
            + $"{(int)TrxSummaryExitCode.UsageError} usage, {(int)TrxSummaryExitCode.InputError} input, "
            + $"{(int)TrxSummaryExitCode.TrxError} TRX parse");
    }

    /// <summary>
    /// Resolves one path from the command line or the input list to an absolute path.
    /// </summary>
    private static string ResolvePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.InputError,
                $"'{path}' is not a usable file path: {ex.Message}");
        }
    }
}
