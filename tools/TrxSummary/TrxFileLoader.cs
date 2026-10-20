using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Reads one TRX file and projects it into the summary model. Elements are matched by local name,
/// so files from runners that omit the TeamTest namespace are read the same way.
/// </summary>
public static class TrxFileLoader
{
    /// <summary>
    /// <c>TestRun</c> attributes consulted, in order, when a result has no usable test definition:
    /// the first non-blank value labels the assembly of that result.
    /// </summary>
    private static readonly string[] RunLevelAssemblyAttributes = ["storage", "assembly", "name"];

    /// <summary>
    /// Reads a TRX file: hashes its bytes, parses it, and resolves every result to an assembly.
    /// </summary>
    /// <param name="path">Path of the TRX file.</param>
    /// <returns>The parsed summary of that file.</returns>
    /// <exception cref="TrxSummaryException">
    /// The file is missing or unreadable, is not well formed XML, has a root element other than
    /// <c>TestRun</c>, has no <c>Results</c> element, or holds a result that cannot be named.
    /// </exception>
    public static TrxFileSummary Load(string path)
    {
        var limits = new TrxResourceLimits();
        return Load(path, limits, new TrxReadBudget(limits));
    }

    internal static TrxFileSummary Load(string path, TrxResourceLimits limits, TrxReadBudget budget)
    {
        if (!File.Exists(path))
        {
            throw new TrxSummaryException(TrxSummaryExitCode.InputError, $"{path}: TRX file does not exist.");
        }

        byte[] bytes = TrxByteSnapshot.Read(path, Math.Min(limits.MaxFileBytes, budget.RemainingBytes),
            "--max-file-bytes or remaining --max-total-bytes");
        budget.IncludeBytes(bytes.Length);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        XDocument document = LoadDocument(path, bytes, limits);
        budget.IncludeText(document, path);
        XElement root = document.Root
            ?? throw new TrxSummaryException(TrxSummaryExitCode.TrxError, $"{path}: the document has no root element.");

        if (!string.Equals(root.Name.LocalName, "TestRun", StringComparison.Ordinal))
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.TrxError,
                $"{path}: root element is <{root.Name.LocalName}>, expected <TestRun>; this is not a TRX result file.");
        }

        Dictionary<string, DefinitionInfo> definitions = ReadDefinitions(root, path);
        string fallbackLabel = RunLevelAssemblyLabel(root, path);

        XElement results = TrxRunEvidence.SingleChild(root, "Results", path)
            ?? throw new TrxSummaryException(
                TrxSummaryExitCode.TrxError,
                $"{path}: <TestRun> has no <Results> element, so no test outcome can be read.");

        var parsed = new List<TrxTestCaseResult>();
        int resultsWithoutDefinition = 0;
        int index = 0;

        foreach (XElement element in results.Elements())
        {
            if (element.Name.LocalName != "UnitTestResult" || element.Descendants().Any(n =>
                    n.Name.LocalName is "UnitTestResult" or "TestResultAggregation" or "InnerResults"))
                throw new TrxSummaryException(TrxSummaryExitCode.TrxError,
                    $"{path}: unsupported aggregate or nested result structure <{element.Name.LocalName}>; only flat UnitTestResult records are accepted.");
            budget.IncludeResult(path);
            index++;
            string? testId = Attribute(element, "testId");
            DefinitionInfo? definition = testId is null ? null : FindDefinition(definitions, testId);
            string assemblyName = fallbackLabel;

            if (definition is null)
            {
                resultsWithoutDefinition++;
            }
            else
            {
                assemblyName = definition.AssemblyName;
            }

            string testName = ResolveTestName(element, definition, testId, path, index);
            string rawOutcome = Attribute(element, "outcome") ?? string.Empty;
            TrxOutcome outcome = ClassifyOutcome(rawOutcome);
            string? messageFirstLine = outcome == TrxOutcome.Failed ? FailureMessageFirstLine(element) : null;
            budget.IncludeTextCharacters((long)testName.Length + assemblyName.Length + rawOutcome.Length + (messageFirstLine?.Length ?? 0), path);
            parsed.Add(new TrxTestCaseResult(testName, assemblyName, rawOutcome, outcome, messageFirstLine));
        }

        string? testRunName = Attribute(root, "name");
        var evidence = TrxRunEvidence.Read(root, parsed, path, budget);
        return new TrxFileSummary(path, Path.GetFileName(path), sha256, testRunName, parsed, resultsWithoutDefinition,
            evidence.Outcome, evidence.Diagnostics);
    }

    /// <summary>
    /// Parses the document with entity resolution and DTDs switched off, so reading a crafted file
    /// cannot reach the network or expand entities.
    /// </summary>
    private static XDocument LoadDocument(string path, byte[] bytes, TrxResourceLimits limits)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = limits.MaxTextCharacters,
        };

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.TrxError,
                $"{path}: malformed XML at line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.InputError,
                $"{path}: TRX file cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps every <c>UnitTest</c> definition id to the assembly name of its stored test container.
    /// </summary>
    private static Dictionary<string, DefinitionInfo> ReadDefinitions(XElement root, string path)
    {
        var definitions = new Dictionary<string, DefinitionInfo>(StringComparer.Ordinal);
        XElement? container = Child(root, "TestDefinitions");
        if (container is null)
        {
            return definitions;
        }

        foreach (XElement definition in Children(container, "UnitTest"))
        {
            string? id = Attribute(definition, "id");
            string? assemblyName = DefinitionAssemblyName(definition);
            if (id is null || assemblyName is null)
            {
                continue;
            }

            string? name = Attribute(definition, "name");

            if (definitions.TryGetValue(id, out DefinitionInfo? existing))
            {
                if (!string.Equals(existing.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new TrxSummaryException(
                        TrxSummaryExitCode.TrxError,
                        $"{path}: UnitTest id '{id}' declares both storage '{existing.AssemblyName}' and '{assemblyName}'; "
                        + "the assembly of that result is ambiguous.");
                }

                continue;
            }

            definitions.Add(id, new DefinitionInfo(assemblyName, name));
        }

        return definitions;
    }

    /// <summary>
    /// Resolves the assembly name of one definition from its <c>storage</c> attribute, falling back
    /// to the <c>codeBase</c> of the nested <c>TestMethod</c>.
    /// </summary>
    private static string? DefinitionAssemblyName(XElement definition)
    {
        string? fromStorage = FileNameWithoutExtension(Attribute(definition, "storage"));
        if (fromStorage is not null)
        {
            return fromStorage;
        }

        XElement? testMethod = Child(definition, "TestMethod");
        return testMethod is null ? null : FileNameWithoutExtension(Attribute(testMethod, "codeBase"));
    }

    /// <summary>
    /// Label used for results that have no usable definition, taken from the documented
    /// <see cref="RunLevelAssemblyAttributes"/> chain and finally from the file name.
    /// </summary>
    private static string RunLevelAssemblyLabel(XElement root, string path)
    {
        foreach (string attributeName in RunLevelAssemblyAttributes)
        {
            string? value = Attribute(root, attributeName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 0 ? path : name;
    }

    /// <summary>
    /// Returns the file name without directory or extension, or <see langword="null"/> when the
    /// attribute is missing or blank.
    /// </summary>
    private static string? FileNameWithoutExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string name = Path.GetFileNameWithoutExtension(value.Trim());
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Finds the definition of a test id.
    /// </summary>
    private static DefinitionInfo? FindDefinition(Dictionary<string, DefinitionInfo> definitions, string testId)
    {
        return definitions.TryGetValue(testId, out DefinitionInfo? definition) ? definition : null;
    }

    /// <summary>
    /// Names one result, preferring <c>testName</c>, then the definition name, then the raw test id.
    /// </summary>
    private static string ResolveTestName(
        XElement element,
        DefinitionInfo? definition,
        string? testId,
        string path,
        int index)
    {
        string? candidate = Attribute(element, "testName");
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = definition?.Name;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = testId;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.TrxError,
                $"{path}: <UnitTestResult> number {index} has neither a testName nor a testId attribute, so it cannot be reported.");
        }

        return candidate.Trim();
    }

    /// <summary>
    /// Reduces a raw <c>outcome</c> value to a report bucket; unknown and missing values are kept as
    /// <see cref="TrxOutcome.Unclassified"/> so the report can show them instead of hiding them.
    /// </summary>
    private static TrxOutcome ClassifyOutcome(string rawOutcome)
    {
        return rawOutcome.Trim().ToLowerInvariant() switch
        {
            "passed" => TrxOutcome.Passed,
            "notexecuted" => TrxOutcome.Skipped,
            "failed" or "error" or "timeout" or "aborted" => TrxOutcome.Failed,
            _ => TrxOutcome.Unclassified,
        };
    }

    /// <summary>
    /// Extracts the first non-blank line of the failure message of a result.
    /// </summary>
    private static string? FailureMessageFirstLine(XElement result)
    {
        XElement? errorInfo = Child(Child(result, "Output"), "ErrorInfo");
        string? message = Child(errorInfo, "Message")?.Value;
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        foreach (string line in message.Split('\n'))
        {
            string candidate = SanitizeLine(line);
            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Flattens one line of failure text so the report stays one line per failure.
    /// </summary>
    private static string SanitizeLine(string line)
    {
        var builder = new System.Text.StringBuilder(line.Length);

        foreach (char character in line)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Returns the first direct child with the given local name.
    /// </summary>
    private static XElement? Child(XElement? parent, string localName)
    {
        if (parent is null)
        {
            return null;
        }

        foreach (XElement child in parent.Elements())
        {
            if (string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates the direct children with the given local name.
    /// </summary>
    private static IEnumerable<XElement> Children(XElement parent, string localName)
    {
        foreach (XElement child in parent.Elements())
        {
            if (string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Returns the attribute value, or <see langword="null"/> when the attribute is absent.
    /// </summary>
    private static string? Attribute(XElement element, string name)
    {
        return element.Attribute(name)?.Value;
    }

    /// <summary>
    /// Assembly name and display name of one <c>UnitTest</c> definition.
    /// </summary>
    private sealed class DefinitionInfo
    {
        /// <summary>
        /// Creates the definition info.
        /// </summary>
        /// <param name="assemblyName">Assembly file name without directory or extension.</param>
        /// <param name="name">Definition name attribute, when present.</param>
        internal DefinitionInfo(string assemblyName, string? name)
        {
            AssemblyName = assemblyName;
            Name = name;
        }

        /// <summary>Assembly file name without directory or extension.</summary>
        internal string AssemblyName { get; }

        /// <summary>Definition name attribute, when present.</summary>
        internal string? Name { get; }
    }
}
