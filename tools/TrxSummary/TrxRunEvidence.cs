using System.Globalization;
using System.Xml.Linq;

namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>Checks run-level evidence separately from the count of parsed result records.</summary>
internal static class TrxRunEvidence
{
    private static readonly string[] OutcomeCounters =
    [
        "passed", "failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
        "notRunnable", "notExecuted", "disconnected", "warning", "completed", "inProgress", "pending"
    ];

    internal static (string? Outcome, IReadOnlyList<string> Diagnostics) Read(
        XElement root, IReadOnlyList<TrxTestCaseResult> results, string path, TrxReadBudget budget)
    {
        var messages = new List<string>();
        var summary = SingleChild(root, "ResultSummary", path);
        if (summary is null)
        {
            Add("Missing ResultSummary: row counts cannot establish run completion or success.");
            return (null, messages);
        }
        var outcome = summary.Attribute("outcome")?.Value.Trim();
        if (outcome is not ("Completed" or "Passed"))
            Add($"Run outcome '{Line(outcome ?? "(missing)")}' does not establish a completed successful run.");

        var infos = SingleChild(summary, "RunInfos", path);
        if (infos is not null)
            foreach (var info in infos.Elements())
            {
                if (info.Name.LocalName != "RunInfo")
                    throw Malformed(path, $"unsupported RunInfos child <{info.Name.LocalName}>");
                var text = SingleChild(info, "Text", path)?.Value ?? info.Value;
                Add($"RunInfo outcome={Line(info.Attribute("outcome")?.Value ?? "(missing)")}: {Line(text)}");
            }
        foreach (var error in summary.Descendants().Where(element => element.Name.LocalName == "ErrorInfo"))
            Add($"Run ErrorInfo: {Line(error.Value)}");
        foreach (var stderr in summary.Descendants().Where(element => element.Name.LocalName == "StdErr" && !string.IsNullOrWhiteSpace(element.Value)))
            Add($"Run StdErr: {Line(stderr.Value)}");

        var counters = SingleChild(summary, "Counters", path);
        if (counters is null) Add("Missing Counters: reported row counts are not reconciled to run counters.");
        else
        {
            var counts = results.GroupBy(result => result.RawOutcome.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in counters.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                if (!int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var reported))
                    throw Malformed(path, $"counter '{attribute.Name.LocalName}' must be a nonnegative 32-bit integer");
                var name = attribute.Name.LocalName;
                int? observed = name switch
                {
                    "total" => results.Count,
                    "executed" when counts.Keys.All(IsStandardExecutedOutcome) =>
                        counts.GetValueOrDefault("passed") + counts.GetValueOrDefault("failed"),
                    _ when OutcomeCounters.Contains(name, StringComparer.Ordinal) => counts.GetValueOrDefault(name),
                    _ => null,
                };
                if (observed is null) Add($"Counter '{name}'={reported} cannot be verified for this result structure.");
                else if (reported != observed) Add($"COUNTER MISMATCH '{name}': reported {reported}, parsed records {observed}.");
            }
            if (counters.Attribute("total") is null) Add("Missing total counter: completeness cannot be verified.");
            foreach (var count in counts.Where(pair => pair.Value > 0))
            {
                var counter = OutcomeCounters.FirstOrDefault(name => name.Equals(count.Key, StringComparison.OrdinalIgnoreCase));
                if (counter is null) Add($"Unclassified record outcome '{Line(count.Key)}' has no supported counter mapping.");
                else if (counters.Attribute(counter) is null) Add($"Missing '{counter}' counter for {count.Value} parsed record(s).");
            }
        }
        if (results.Count == 0) Add("No result records were parsed; this is not evidence of passing tests.");
        return (outcome, messages);

        void Add(string message)
        {
            budget.IncludeTextCharacters(message.Length, path);
            messages.Add(message);
        }
    }

    internal static XElement? SingleChild(XElement parent, string name, string path)
    {
        var children = parent.Elements().Where(element => element.Name.LocalName == name).Take(2).ToArray();
        if (children.Length > 1) throw Malformed(path, $"multiple <{name}> containers are unsupported");
        return children.SingleOrDefault();
    }

    private static bool IsStandardExecutedOutcome(string value) =>
        value.Equals("Passed", StringComparison.OrdinalIgnoreCase) || value.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase);
    private static string Line(string value) => string.Concat(value.Select(character => char.IsControl(character) ? ' ' : character)).Trim();
    private static TrxSummaryException Malformed(string path, string reason) => new(TrxSummaryExitCode.TrxError, $"{path}: {reason}.");
}
