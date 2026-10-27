using System.Text;
using System.Text.RegularExpressions;

namespace BubbleFighter.Fantasy.Tests;

/// <summary>Copies the repository's packet tables into a test folder and changes one field of one structure.</summary>
internal static class PacketTableEdits
{
    /// <summary>
    /// Copies every packets/*.yaml into a fresh TestTempDirectories.ForTest("bff-packets") folder and, inside the
    /// block of <paramref name="structure"/> only, replaces the line `- {field}: {fromType}` with
    /// `- {field}: {toType}` (any trailing comment kept). Throws if the structure block is not found or the line
    /// is not found exactly once inside it. Returns the folder.
    /// </summary>
    /// <param name="structure">The structure name; the block header must be exactly two spaces and the key.</param>
    /// <param name="field">The field to change.</param>
    /// <param name="fromType">The type the repository currently states for the field.</param>
    /// <param name="toType">The type to state instead.</param>
    /// <returns>The copied packet directory.</returns>
    internal static DirectoryInfo WithFieldType(string structure, string field, string fromType, string toType)
    {
        ArgumentException.ThrowIfNullOrEmpty(structure);
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentException.ThrowIfNullOrEmpty(fromType);
        ArgumentException.ThrowIfNullOrEmpty(toType);
        var source = RepositoryPackets();
        var target = new DirectoryInfo(TestTempDirectories.ForTest("bff-packets"));
        foreach (var file in Directory.GetFiles(source, "*.yaml"))
            File.Copy(file, Path.Combine(target.FullName, Path.GetFileName(file)));
        var header = new Regex($@"^  {Regex.Escape(structure)}:\s*(#.*)?$", RegexOptions.CultureInvariant);
        // A structure header is two spaces and a name; a comment line at the same indent (restated
        // structures open with their evidence notes) is still inside the block, so '#' does not end it.
        var nextStructure = new Regex("^  [^ #-]", RegexOptions.CultureInvariant);
        var fieldLine = new Regex($@"^(?<prefix>\s*- ){Regex.Escape(field)}: {Regex.Escape(fromType)}(?<tail>\s+#.*)?\s*$",
            RegexOptions.CultureInvariant);
        var foundBlock = false;
        foreach (var file in target.GetFiles("*.yaml"))
        {
            var lines = File.ReadAllLines(file.FullName, Encoding.UTF8).ToList();
            var start = lines.FindIndex(line => header.IsMatch(line));
            if (start < 0)
                continue;
            foundBlock = true;
            var end = start + 1;
            while (end < lines.Count && !nextStructure.IsMatch(lines[end]))
                end++;
            var matches = new List<int>();
            for (var i = start + 1; i < end; i++)
                if (fieldLine.IsMatch(lines[i]))
                    matches.Add(i);
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"'{structure}.{field}: {fromType}' occurs {matches.Count} time(s) inside its block in '{file.Name}'; exactly one is required.");
            var match = fieldLine.Match(lines[matches[0]]);
            lines[matches[0]] = match.Groups["prefix"].Value + field + ": " + toType + (match.Groups["tail"].Success ? match.Groups["tail"].Value : "");
            File.WriteAllLines(file.FullName, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return target;
        }
        if (!foundBlock)
            throw new InvalidOperationException($"No structure block '  {structure}:' exists in the repository packet tables.");
        throw new InvalidOperationException($"'{structure}' was found in none of the copied packet files.");
    }

    private static string RepositoryPackets()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }
}
