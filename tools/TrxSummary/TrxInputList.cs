namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Reader for the input list file: one TRX path per line, blank lines and <c>#</c> comment lines
/// ignored. A byte limit bounds the snapshot, including long lines and comments, before decoding.
/// </summary>
public static class TrxInputList
{
    /// <summary>
    /// Reads the TRX paths of an input list.
    /// </summary>
    /// <param name="listPath">Path of the list file.</param>
    /// <param name="limits">Validated byte and entry count limits for this invocation.</param>
    /// <returns>The entries in file order, with surrounding whitespace removed.</returns>
    /// <exception cref="TrxSummaryException">
    /// The list file is missing, unreadable, empty after comments are removed, or holds more than
    /// the configured number of entries.
    /// </exception>
    internal static IReadOnlyList<string> Read(string listPath, TrxResourceLimits limits)
    {
        if (!File.Exists(listPath))
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.InputError,
                $"{listPath}: input list file does not exist.");
        }

        var entries = new List<string>();

        try
        {
            var bytes = TrxByteSnapshot.Read(listPath, limits.MaxListBytes, "--max-list-bytes");
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false),
                new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } rawLine)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (entries.Count == limits.MaxFiles)
                {
                    throw new TrxSummaryException(
                        TrxSummaryExitCode.InputError,
                        $"{listPath}: more than {limits.MaxFiles} TRX files are listed (--max-files).");
                }

                entries.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.InputError,
                $"{listPath}: input list cannot be read: {ex.Message}");
        }

        if (entries.Count == 0)
        {
            throw new TrxSummaryException(
                TrxSummaryExitCode.InputError,
                $"{listPath}: input list names no TRX file (blank lines and '#' comments are ignored).");
        }

        return entries;
    }
}
