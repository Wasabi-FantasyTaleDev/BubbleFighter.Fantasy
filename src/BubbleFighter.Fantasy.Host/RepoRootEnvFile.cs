using System.Text;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Host;

/// <summary>
/// Loads a repository-root <c>.env</c> file into the process environment so profile YAML
/// interpolation (<c>${VAR}</c>, <c>${VAR:-default}</c>) can resolve local values. Existing
/// process variables are never overwritten. File contents are never logged.
/// </summary>
public static class RepoRootEnvFile
{
    /// <summary>Maximum accepted size of a repository-root <c>.env</c> file.</summary>
    public const int MaxFileBytes = 64 * 1024;

    private const string SolutionFileName = "BubbleFighter.Fantasy.sln";
    private const string EnvFileName = ".env";

    /// <summary>
    /// Finds the repository root (the directory that contains
    /// <c>BubbleFighter.Fantasy.sln</c> or a <c>.git</c> entry) by walking upward from the
    /// current working directory, then from the host base directory. When that root has a
    /// <c>.env</c> file, each well-formed <c>KEY=VALUE</c> assignment is applied only if the
    /// variable is not already set in the process environment.
    /// </summary>
    /// <returns>
    /// The absolute path of the file that was read, or <see langword="null"/> when no
    /// repository-root <c>.env</c> is present.
    /// </returns>
    /// <exception cref="ConfigException">
    /// The file exists but exceeds <see cref="MaxFileBytes"/>, is not valid UTF-8, or contains
    /// a malformed line. Error messages include the file path and line number only — never
    /// assignment values.
    /// </exception>
    public static string? Apply()
    {
        var root = FindRepoRoot();
        if (root is null)
            return null;

        var path = Path.Combine(root, EnvFileName);
        if (!File.Exists(path))
            return null;

        ApplyFile(path);
        return path;
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? dir;
            try
            {
                dir = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                continue;
            }

            for (; dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                    return dir.FullName;
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                    return dir.FullName;
            }
        }

        return null;
    }

    private static void ApplyFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new ConfigException($"File '{path}': exceeds the {MaxFileBytes}-byte limit.");

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigException($"File '{path}': is not valid UTF-8.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"File '{path}': {ex.Message}");
        }

        var assignments = Parse(path, lines);
        foreach (var (name, value) in assignments)
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
                continue;
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static Dictionary<string, string> Parse(string path, IReadOnlyList<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && line[0] == '\uFEFF')
                line = line[1..];

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            if (trimmed.StartsWith("export ", StringComparison.Ordinal)
                || trimmed.StartsWith("export\t", StringComparison.Ordinal))
            {
                trimmed = trimmed.AsSpan(6).TrimStart().ToString();
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
                throw new ConfigException($"File '{path}': line {i + 1}: expected KEY=VALUE.");

            var key = trimmed[..eq].TrimEnd();
            if (!IsName(key))
                throw new ConfigException($"File '{path}': line {i + 1}: invalid variable name.");

            values[key] = ParseValue(path, i + 1, trimmed[(eq + 1)..]);
        }

        return values;
    }

    private static string ParseValue(string path, int lineNumber, string raw)
    {
        raw = raw.TrimStart();
        if (raw.Length == 0)
            return string.Empty;

        if (raw[0] is '"' or '\'')
            return ParseQuoted(path, lineNumber, raw);

        var hash = IndexOfUnquotedComment(raw);
        if (hash >= 0)
            raw = raw[..hash];
        return raw.TrimEnd();
    }

    private static string ParseQuoted(string path, int lineNumber, string raw)
    {
        var quote = raw[0];
        var builder = new StringBuilder(raw.Length);
        var closed = false;
        var i = 1;
        while (i < raw.Length)
        {
            var c = raw[i++];
            if (c == quote)
            {
                closed = true;
                break;
            }

            if (quote == '"' && c == '\\' && i < raw.Length)
            {
                var escaped = raw[i++];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped,
                });
                continue;
            }

            builder.Append(c);
        }

        if (!closed)
            throw new ConfigException($"File '{path}': line {lineNumber}: unclosed quoted value.");

        var rest = raw[i..].TrimStart();
        if (rest.Length > 0 && rest[0] != '#')
            throw new ConfigException($"File '{path}': line {lineNumber}: unexpected characters after quoted value.");

        return builder.ToString();
    }

    private static int IndexOfUnquotedComment(string raw)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && (i == 0 || char.IsWhiteSpace(raw[i - 1])))
                return i;
        }

        return -1;
    }

    private static bool IsName(string key)
    {
        if (key.Length == 0)
            return false;
        if (!IsNameStart(key[0]))
            return false;
        for (var i = 1; i < key.Length; i++)
        {
            if (!IsNamePart(key[i]))
                return false;
        }

        return true;
    }

    private static bool IsNameStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    private static bool IsNamePart(char c) => IsNameStart(c) || c is >= '0' and <= '9';
}
