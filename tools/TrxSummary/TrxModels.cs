namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Outcome bucket of a single TRX test result, reduced to the four states the summary reports.
/// </summary>
public enum TrxOutcome
{
    /// <summary>The TRX recorded <c>outcome="Passed"</c>.</summary>
    Passed,

    /// <summary>The TRX recorded a failing outcome: <c>Failed</c>, <c>Error</c>, <c>Timeout</c> or <c>Aborted</c>.</summary>
    Failed,

    /// <summary>The TRX recorded <c>outcome="NotExecuted"</c>, which is how the runner writes a skipped test.</summary>
    Skipped,

    /// <summary>The TRX recorded an outcome this tool does not classify; every such result is listed verbatim in the report.</summary>
    Unclassified,
}

/// <summary>
/// Result counts of a set of TRX test results. <see cref="Total"/> always equals the sum of the
/// four buckets, including <see cref="Unclassified"/>, so no result is dropped from the report.
/// </summary>
public readonly struct TrxOutcomeCounts
{
    /// <summary>
    /// Creates a count set from already computed bucket values.
    /// </summary>
    /// <param name="total">Number of results seen, including unclassified ones.</param>
    /// <param name="passed">Number of passed results.</param>
    /// <param name="failed">Number of failed results.</param>
    /// <param name="skipped">Number of skipped (not executed) results.</param>
    /// <param name="unclassified">Number of results whose outcome this tool does not classify.</param>
    public TrxOutcomeCounts(int total, int passed, int failed, int skipped, int unclassified)
    {
        Total = total;
        Passed = passed;
        Failed = failed;
        Skipped = skipped;
        Unclassified = unclassified;
    }

    /// <summary>Number of results seen, including unclassified ones.</summary>
    public int Total { get; }

    /// <summary>Number of passed results.</summary>
    public int Passed { get; }

    /// <summary>Number of failed results.</summary>
    public int Failed { get; }

    /// <summary>Number of skipped (not executed) results.</summary>
    public int Skipped { get; }

    /// <summary>Number of results whose outcome this tool does not classify.</summary>
    public int Unclassified { get; }

    /// <summary>
    /// Counts one sequence of results into the four buckets.
    /// </summary>
    /// <param name="results">Results to count; an empty sequence yields a zero count set.</param>
    /// <returns>The counts of <paramref name="results"/>.</returns>
    public static TrxOutcomeCounts FromResults(IEnumerable<TrxTestCaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        int total = 0;
        int passed = 0;
        int failed = 0;
        int skipped = 0;
        int unclassified = 0;

        foreach (TrxTestCaseResult result in results)
        {
            total++;
            switch (result.Outcome)
            {
                case TrxOutcome.Passed:
                    passed++;
                    break;
                case TrxOutcome.Failed:
                    failed++;
                    break;
                case TrxOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    unclassified++;
                    break;
            }
        }

        return new TrxOutcomeCounts(total, passed, failed, skipped, unclassified);
    }
}

/// <summary>
/// One <c>UnitTestResult</c> entry of one TRX file.
/// </summary>
public sealed class TrxTestCaseResult
{
    /// <summary>
    /// Creates a result entry.
    /// </summary>
    /// <param name="testName">Fully qualified test name as written by the runner.</param>
    /// <param name="assemblyName">Assembly the result belongs to.</param>
    /// <param name="rawOutcome">Raw <c>outcome</c> attribute value, or an empty string when it was absent.</param>
    /// <param name="outcome">Reduced outcome bucket of <paramref name="rawOutcome"/>.</param>
    /// <param name="failureMessageFirstLine">First line of the failure message for a failed result; otherwise <see langword="null"/>.</param>
    public TrxTestCaseResult(
        string testName,
        string assemblyName,
        string rawOutcome,
        TrxOutcome outcome,
        string? failureMessageFirstLine)
    {
        TestName = testName;
        AssemblyName = assemblyName;
        RawOutcome = rawOutcome;
        Outcome = outcome;
        FailureMessageFirstLine = failureMessageFirstLine;
    }

    /// <summary>Fully qualified test name as written by the runner, including any parameter list.</summary>
    public string TestName { get; }

    /// <summary>Assembly the result belongs to, taken from its test definition or from the run level fallback label.</summary>
    public string AssemblyName { get; }

    /// <summary>Raw <c>outcome</c> attribute value, or an empty string when the attribute was absent.</summary>
    public string RawOutcome { get; }

    /// <summary>Reduced outcome bucket of <see cref="RawOutcome"/>.</summary>
    public TrxOutcome Outcome { get; }

    /// <summary>First line of the failure message for a failed result; otherwise <see langword="null"/>.</summary>
    public string? FailureMessageFirstLine { get; }
}

/// <summary>
/// Everything the tool read from one TRX file.
/// </summary>
public sealed class TrxFileSummary
{
    /// <summary>
    /// Creates the summary of one TRX file.
    /// </summary>
    /// <param name="path">Absolute path the file was read from.</param>
    /// <param name="fileName">File name without directory, as reported to the user.</param>
    /// <param name="sha256">Upper case hexadecimal SHA256 of the exact bytes that were parsed.</param>
    /// <param name="testRunName">The <c>TestRun/@name</c> attribute when present; otherwise <see langword="null"/>.</param>
    /// <param name="results">Every parsed result, in document order.</param>
    /// <param name="resultsWithoutDefinition">Number of results whose <c>testId</c> had no matching <c>UnitTest</c> definition.</param>
    /// <param name="runOutcome">The ResultSummary outcome, independently of individual result outcomes.</param>
    /// <param name="runDiagnostics">Run messages, missing evidence and disagreements with declared counters.</param>
    public TrxFileSummary(
        string path,
        string fileName,
        string sha256,
        string? testRunName,
        IReadOnlyList<TrxTestCaseResult> results,
        int resultsWithoutDefinition,
        string? runOutcome = null,
        IReadOnlyList<string>? runDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        Path = path;
        FileName = fileName;
        Sha256 = sha256;
        TestRunName = testRunName;
        Results = results.ToArray();
        ResultsWithoutDefinition = resultsWithoutDefinition;
        RunOutcome = runOutcome;
        RunDiagnostics = runDiagnostics?.ToArray() ?? [];
    }

    /// <summary>Absolute path the file was read from.</summary>
    public string Path { get; }

    /// <summary>File name without directory, as reported to the user.</summary>
    public string FileName { get; }

    /// <summary>Upper case hexadecimal SHA256 of the exact bytes that were parsed.</summary>
    public string Sha256 { get; }

    /// <summary>The <c>TestRun/@name</c> attribute when present; otherwise <see langword="null"/>.</summary>
    public string? TestRunName { get; }

    /// <summary>The run summary outcome; a Passed result record alone does not establish this status.</summary>
    public string? RunOutcome { get; }

    /// <summary>Run messages and evidence gaps, including counter mismatches, retained for explicit reporting.</summary>
    public IReadOnlyList<string> RunDiagnostics { get; }

    /// <summary>Every parsed result, in document order.</summary>
    public IReadOnlyList<TrxTestCaseResult> Results { get; }

    /// <summary>
    /// Number of results whose <c>testId</c> had no matching <c>UnitTest</c> definition, so their
    /// assembly came from the run level fallback label instead of the definitions.
    /// </summary>
    public int ResultsWithoutDefinition { get; }
}

/// <summary>
/// The complete report: the input list that was read plus one summary per TRX file.
/// </summary>
public sealed class TrxSummaryReport
{
    /// <summary>
    /// Creates a report.
    /// </summary>
    /// <param name="inputListPath">Absolute path of the list file that named the inputs.</param>
    /// <param name="files">One summary per input file, in input order.</param>
    public TrxSummaryReport(string inputListPath, IReadOnlyList<TrxFileSummary> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        InputListPath = inputListPath;
        Files = files.ToArray();
    }

    /// <summary>Absolute path of the list file that named the inputs.</summary>
    public string InputListPath { get; }

    /// <summary>One summary per input file, in input order.</summary>
    public IReadOnlyList<TrxFileSummary> Files { get; }
}
