namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Renders a <see cref="TrxSummaryReport"/> as the human readable text the tool prints on success.
/// Sections follow the required order: assemblies, totals, failures, then the per-file SHA256 list.
/// Every counts block prints the unclassified bucket as well, so the four buckets always add up to
/// the printed total and no result can disappear from the report unnoticed.
/// </summary>
public static class TrxReportWriter
{
    /// <summary>
    /// Writes the report: one section per assembly, the totals, every failure on one line, each
    /// input file with its SHA256, and the notes that explain any fallback that was used.
    /// </summary>
    /// <param name="report">Report to render.</param>
    /// <param name="writer">Writer that receives the text.</param>
    public static void Write(TrxSummaryReport report, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        List<(TrxFileSummary File, TrxTestCaseResult Result)> pairs = [];
        foreach (TrxFileSummary file in report.Files)
        {
            foreach (TrxTestCaseResult result in file.Results)
            {
                pairs.Add((file, result));
            }
        }

        TrxOutcomeCounts totals = TrxOutcomeCounts.FromResults(pairs.Select(pair => pair.Result));
        List<IGrouping<string, (TrxFileSummary File, TrxTestCaseResult Result)>> assemblies = pairs
            .GroupBy(pair => pair.Result.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        writer.WriteLine("TRX summary");
        writer.WriteLine("===========");
        writer.WriteLine();
        writer.WriteLine($"input list  {report.InputListPath}");
        writer.WriteLine($"files       {report.Files.Count}");
        writer.WriteLine("report-only  exit 0 means report generated; it is not a test-pass or CI-gate verdict.");
        writer.WriteLine("counting     totals count result records per input entry, not unique tests or independent runs.");
        writer.WriteLine();

        WriteAssemblies(assemblies, writer);
        WriteTotals(totals, report, writer);
        WriteFailures(pairs, writer);
        WriteInputFiles(report, writer);
        WriteNotes(report, totals, pairs, writer);
    }

    /// <summary>
    /// Writes a section header followed by a matching underline.
    /// </summary>
    private static void WriteHeader(string header, TextWriter writer)
    {
        writer.WriteLine(header);
        writer.WriteLine(new string('-', header.Length));
    }

    /// <summary>
    /// Writes the per-assembly counts. The file count of an assembly is the number of input entries
    /// that contributed results to it: entries are processed exactly as the input list spells them,
    /// so a path listed twice is counted twice here and its results are counted twice as well.
    /// </summary>
    private static void WriteAssemblies(
        List<IGrouping<string, (TrxFileSummary File, TrxTestCaseResult Result)>> assemblies,
        TextWriter writer)
    {
        WriteHeader($"Assemblies ({assemblies.Count})", writer);

        if (assemblies.Count == 0)
        {
            writer.WriteLine("(none)");
            writer.WriteLine();
            return;
        }

        foreach (IGrouping<string, (TrxFileSummary File, TrxTestCaseResult Result)> group in assemblies)
        {
            TrxOutcomeCounts counts = TrxOutcomeCounts.FromResults(group.Select(pair => pair.Result));
            int fileCount = group.Select(pair => pair.File).Distinct().Count();

            writer.WriteLine(group.Key);
            writer.WriteLine($"  files         {fileCount}");
            writer.WriteLine($"  total         {counts.Total}");
            writer.WriteLine($"  passed        {counts.Passed}");
            writer.WriteLine($"  failed        {counts.Failed}");
            writer.WriteLine($"  skipped       {counts.Skipped}");
            writer.WriteLine($"  unclassified  {counts.Unclassified}");
        }

        writer.WriteLine();
    }

    /// <summary>
    /// Writes the totals across every input file.
    /// </summary>
    private static void WriteTotals(TrxOutcomeCounts totals, TrxSummaryReport report, TextWriter writer)
    {
        WriteHeader("Totals", writer);
        writer.WriteLine($"files         {report.Files.Count}");
        writer.WriteLine($"total         {totals.Total}");
        writer.WriteLine($"passed        {totals.Passed}");
        writer.WriteLine($"failed        {totals.Failed}");
        writer.WriteLine($"skipped       {totals.Skipped}");
        writer.WriteLine($"unclassified  {totals.Unclassified}");
        writer.WriteLine();
    }

    /// <summary>
    /// Writes one line per failed result: assembly, fully qualified name, first message line, file.
    /// </summary>
    private static void WriteFailures(
        List<(TrxFileSummary File, TrxTestCaseResult Result)> pairs,
        TextWriter writer)
    {
        List<(TrxFileSummary File, TrxTestCaseResult Result)> failures = pairs
            .Where(pair => pair.Result.Outcome == TrxOutcome.Failed)
            .OrderBy(pair => pair.Result.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Result.TestName, StringComparer.Ordinal)
            .ThenBy(pair => pair.File.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WriteHeader($"Failures ({failures.Count})", writer);

        if (failures.Count == 0)
        {
            writer.WriteLine("(none)");
            writer.WriteLine();
            return;
        }

        foreach ((TrxFileSummary file, TrxTestCaseResult result) in failures)
        {
            string message = result.FailureMessageFirstLine ?? "(no failure message)";
            writer.WriteLine($"[{result.AssemblyName}] {result.TestName} | {message} [file: {file.FileName}]");
        }

        writer.WriteLine();
    }

    /// <summary>
    /// Writes the SHA256 and file name of every input file.
    /// </summary>
    private static void WriteInputFiles(TrxSummaryReport report, TextWriter writer)
    {
        WriteHeader("Input files", writer);

        for (int index = 0; index < report.Files.Count; index++)
        {
            TrxFileSummary file = report.Files[index];
            TrxOutcomeCounts counts = TrxOutcomeCounts.FromResults(file.Results);

            writer.WriteLine($"{index + 1}. {file.FileName}");
            writer.WriteLine($"   path          {file.Path}");
            writer.WriteLine($"   sha256        {file.Sha256}");
            writer.WriteLine($"   run outcome   {file.RunOutcome ?? "(missing)"}");
            foreach (var diagnostic in file.RunDiagnostics) writer.WriteLine($"   RUN EVIDENCE  {diagnostic}");
            if (!string.IsNullOrWhiteSpace(file.TestRunName))
            {
                writer.WriteLine($"   test run      {file.TestRunName}");
            }

            writer.WriteLine(
                $"   total {counts.Total}  passed {counts.Passed}  failed {counts.Failed}  "
                + $"skipped {counts.Skipped}  unclassified {counts.Unclassified}");
        }

        writer.WriteLine();
    }

    /// <summary>
    /// Writes the notes: unclassified outcomes, definition fallbacks, and the empty-input case.
    /// Nothing that was read is left out of the report.
    /// </summary>
    private static void WriteNotes(
        TrxSummaryReport report,
        TrxOutcomeCounts totals,
        List<(TrxFileSummary File, TrxTestCaseResult Result)> pairs,
        TextWriter writer)
    {
        List<(TrxFileSummary File, TrxTestCaseResult Result)> unclassified = pairs
            .Where(pair => pair.Result.Outcome == TrxOutcome.Unclassified)
            .OrderBy(pair => pair.Result.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Result.TestName, StringComparer.Ordinal)
            .ToList();

        if (unclassified.Count > 0)
        {
            WriteHeader($"Unclassified outcomes ({unclassified.Count})", writer);
            foreach ((TrxFileSummary file, TrxTestCaseResult result) in unclassified)
            {
                string raw = result.RawOutcome.Length == 0 ? "(missing outcome attribute)" : result.RawOutcome;
                writer.WriteLine($"[{result.AssemblyName}] {result.TestName} | outcome '{raw}' [file: {file.FileName}]");
            }

            writer.WriteLine();
        }

        WriteHeader("Notes", writer);

        bool any = false;
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var duplicate in report.Files.GroupBy(file => file.Path, pathComparer).Where(group => group.Count() > 1))
        {
            any = true;
            writer.WriteLine($"- DUPLICATE INPUT: {duplicate.Key} appears {duplicate.Count()} times; every occurrence and its records are counted.");
        }
        foreach (TrxFileSummary file in report.Files)
        {
            if (file.ResultsWithoutDefinition > 0)
            {
                any = true;
                writer.WriteLine(
                    $"- {file.FileName}: {file.ResultsWithoutDefinition} result(s) had no matching <UnitTest> definition; "
                    + "their assembly comes from the run level fallback label.");
            }
        }

        if (totals.Total == 0)
        {
            any = true;
            writer.WriteLine("- no test result was found in the input files.");
        }

        if (!any)
        {
            writer.WriteLine("(none)");
        }
    }
}
