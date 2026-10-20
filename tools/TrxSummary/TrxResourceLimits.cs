using System.Globalization;
using System.Xml.Linq;

namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>Validated invocation limits, shared by list loading, XML parsing and result projection.</summary>
internal sealed record TrxResourceLimits
{
    internal const int DefaultMaxFiles = 64;
    internal int MaxFiles { get; init; } = DefaultMaxFiles;
    internal int MaxListBytes { get; init; } = 1048576;
    internal int MaxFileBytes { get; init; } = 33554432;
    internal int MaxTotalBytes { get; init; } = 268435456;
    internal int MaxResults { get; init; } = 250000;
    internal int MaxTextCharacters { get; init; } = 16777216;

    internal static (string ListPath, TrxResourceLimits Limits) Parse(string[]? args)
    {
        var limits = new TrxResourceLimits();
        string? path = null;
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        if (args is null) throw Usage("An input list path is required.");
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (path is not null || string.IsNullOrWhiteSpace(argument)) throw Usage("Exactly one input list path is required.");
                path = argument;
                continue;
            }
            if (!supplied.Add(argument)) throw Usage($"Option '{argument}' was repeated.");
            if (++index >= args.Length || !int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                throw Usage($"Option '{argument}' requires a positive 32-bit integer.");
            limits = argument switch
            {
                "--max-files" => limits with { MaxFiles = value },
                "--max-list-bytes" => limits with { MaxListBytes = value },
                "--max-file-bytes" => limits with { MaxFileBytes = value },
                "--max-total-bytes" => limits with { MaxTotalBytes = value },
                "--max-results" => limits with { MaxResults = value },
                "--max-text-chars" => limits with { MaxTextCharacters = value },
                _ => throw Usage($"Unknown option '{argument}'."),
            };
        }
        return (path ?? throw Usage("An input list path is required."), limits);
    }

    private static TrxSummaryException Usage(string message) => new(TrxSummaryExitCode.UsageError, message);
}

/// <summary>Counts total TRX bytes, result records and decoded XML content for one invocation.</summary>
internal sealed class TrxReadBudget(TrxResourceLimits limits)
{
    private long _bytes;
    private long _text;
    private long _results;
    internal int RemainingBytes => checked((int)(limits.MaxTotalBytes - _bytes));

    internal void IncludeBytes(int bytes) => _bytes = checked(_bytes + bytes);

    internal void IncludeResult(string path)
    {
        if (++_results > limits.MaxResults)
            throw new TrxSummaryException(TrxSummaryExitCode.TrxError, $"{path}: aggregate result count exceeds --max-results {limits.MaxResults}.");
    }

    internal void IncludeText(XDocument document, string path)
    {
        foreach (var node in document.DescendantNodes())
        {
            if (node is XText text) IncludeTextCharacters(text.Value.Length, path);
            if (node is XElement element)
            {
                IncludeTextCharacters(element.Name.LocalName.Length + element.Name.NamespaceName.Length, path);
                foreach (var attribute in element.Attributes())
                    IncludeTextCharacters((long)attribute.Value.Length + attribute.Name.LocalName.Length + attribute.Name.NamespaceName.Length, path);
            }
        }
    }

    internal void IncludeTextCharacters(long count, string path)
    {
        _text = checked(_text + count);
        if (_text > limits.MaxTextCharacters)
            throw new TrxSummaryException(TrxSummaryExitCode.TrxError, $"{path}: aggregate decoded and projected text exceeds --max-text-chars {limits.MaxTextCharacters}.");
    }
}

/// <summary>Reads one bounded snapshot so parsing and evidence hashing consume identical bytes.</summary>
internal static class TrxByteSnapshot
{
    internal static byte[] Read(string path, int limit, string limitDescription)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            if (length > limit || length > Array.MaxLength)
                throw new TrxSummaryException(TrxSummaryExitCode.InputError, $"{path}: byte length {length} exceeds {limitDescription} ({limit}).");
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new TrxSummaryException(TrxSummaryExitCode.InputError, $"{path}: file grew while its bounded snapshot was being read.");
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TrxSummaryException(TrxSummaryExitCode.InputError, $"{path}: input cannot be read: {ex.Message}");
        }
    }
}
