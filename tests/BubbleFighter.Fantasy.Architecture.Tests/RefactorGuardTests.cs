using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace BubbleFighter.Fantasy.Architecture.Tests;

/// <summary>
/// Machine-checks the unification rules (blueprint S14 part G): no numeric opcode constants, no literal
/// opcode registrations, no build-tagged type or file names, every opcode name used in code exists in some
/// profile's table, and every dialect binding that reads or writes through <c>PacketCodec</c> also asserts its
/// layout through <c>PacketLayoutContract</c>. Existing offenders are listed in <c>refactor-guards.txt</c>, one
/// <c>&lt;guard&gt; &lt;repo-relative path&gt;:&lt;identifier&gt;</c> line each; the set may only shrink, so a
/// change in either direction fails until it is recorded with <c>BFF_RATCHET_WRITE=1</c> in the same commit.
/// </summary>
public sealed class RefactorGuardTests
{
    private const string BaselineRelativePath = "tests/BubbleFighter.Fantasy.Architecture.Tests/refactor-guards.txt";

    private static readonly string[] Guards =
    [
        "build_name", "layout_contract", "opcode_const", "opcode_literal", "opcode_name",
    ];

    private static readonly Lazy<IReadOnlyList<string>> OffenderLines = new(CollectOffenders);

    /// <summary>No <c>const ushort C2S*</c>-style number and no <c>*Opcodes</c> holder class may exist in <c>src/</c>.</summary>
    [Fact]
    public void NoNumericOpcodeConstants() => Check("opcode_const");

    /// <summary>No handler may be registered or sent with a bare integer opcode in <c>src/</c>.</summary>
    [Fact]
    public void NoLiteralOpcodeRegistrations() => Check("opcode_literal");

    /// <summary>No type or file may be named after a client build (<c>Kr1256</c>, <c>Legacy</c>, <c>Modern</c>, ...).</summary>
    [Fact]
    public void NoBuildTaggedTypeNames() => Check("build_name");

    /// <summary>Every opcode name the code passes to <c>S2C</c>/<c>C2S</c>/<c>Find</c> must exist in a profile table.</summary>
    [Fact]
    public void EveryOpcodeNameInCodeExists() => Check("opcode_name");

    /// <summary>Every dialect file that uses <c>PacketCodec</c> must also use <c>PacketLayoutContract</c>.</summary>
    [Fact]
    public void EveryBindingChecksItsLayout() => Check("layout_contract");

    private static void Check(string guard)
    {
        var root = RepoRoot();
        var baselinePath = Path.Combine(root, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Environment.GetEnvironmentVariable("BFF_RATCHET_WRITE") == "1")
        {
            File.WriteAllText(baselinePath, Format(OffenderLines.Value));
            return;
        }

        Assert.True(File.Exists(baselinePath), $"Missing {baselinePath}; run the Architecture tests once with BFF_RATCHET_WRITE=1.");
        var baseline = Parse(File.ReadAllLines(baselinePath));
        var known = new SortedSet<string>(
            baseline.Where(line => line.StartsWith(guard + " ", StringComparison.Ordinal)), StringComparer.Ordinal);
        var current = new SortedSet<string>(
            OffenderLines.Value.Where(line => line.StartsWith(guard + " ", StringComparison.Ordinal)), StringComparer.Ordinal);
        var added = new SortedSet<string>(current.Except(known), StringComparer.Ordinal);
        var removed = new SortedSet<string>(known.Except(current), StringComparer.Ordinal);
        var errors = new List<string>();
        if (added.Count > 0)
        {
            errors.Add($"{guard} rose from {known.Count} to {current.Count}:");
            errors.AddRange(added.Select(line => "  + " + line));
        }
        if (removed.Count > 0)
        {
            errors.Add($"{guard} fell from {known.Count} to {current.Count}:");
            errors.AddRange(removed.Select(line => "  - " + line));
        }
        if (errors.Count > 0)
        {
            errors.Add("Record the new set with BFF_RATCHET_WRITE=1 in the same commit.");
            Assert.Fail(string.Join(Environment.NewLine, errors));
        }
    }

    private static IReadOnlyList<string> CollectOffenders()
    {
        var root = RepoRoot();
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(root))
        {
            var path = RelativePath(root, file);
            var text = File.ReadAllText(file);
            var code = Strip(text, stripStrings: true);
            AddMatches(lines, "opcode_const", path, OpcodeConstant.Matches(code).Select(match => match.Groups[1].Value));
            AddMatches(lines, "opcode_const", path, OpcodeClass.Matches(code).Select(match => match.Groups[1].Value));
            foreach (var offender in LiteralRegisterOffenders(code, path))
                lines.Add(offender);
            foreach (var offender in LiteralSendOffenders(code, path))
                lines.Add(offender);
            AddMatches(lines, "build_name", path, TypeDeclaration.Matches(code)
                .Select(match => match.Groups[1].Value)
                .Where(name => BuildTag.IsMatch(name)));
            var stem = Path.GetFileNameWithoutExtension(file);
            if (BuildTag.IsMatch(stem))
                lines.Add($"build_name {path}:{stem}");
        }

        // Names are checked in every source file: logic rows such as the shop also look requests up by name.
        var opcodeNames = LoadOpcodeNames(root);
        foreach (var file in SourceFiles(root))
        {
            var path = RelativePath(root, file);
            var used = OpcodeNameLiterals(Strip(File.ReadAllText(file), stripStrings: false));
            foreach (var name in used.Where(name => !opcodeNames.Contains(name)))
                lines.Add($"opcode_name {path}:{name}");
        }

        foreach (var file in SourceFiles(root))
        {
            if (!IsDialectFile(root, file))
                continue;
            var path = RelativePath(root, file);
            var code = Strip(File.ReadAllText(file), stripStrings: true);
            var reads = code.Contains("PacketCodec.ReadScalars(", StringComparison.Ordinal);
            var writes = code.Contains("PacketCodec.Write(", StringComparison.Ordinal);
            if (!reads && !writes)
                continue;
            if (code.Contains("PacketLayoutContract.", StringComparison.Ordinal))
                continue;
            if (reads)
                lines.Add($"layout_contract {path}:PacketCodec.ReadScalars");
            if (writes)
                lines.Add($"layout_contract {path}:PacketCodec.Write");
        }

        return lines.ToList();
    }

    // Guard 2: `.Register(<anything>, <integer literal>`; the enclosing type plus the literal identifies it.
    private static IEnumerable<string> LiteralRegisterOffenders(string code, string path)
    {
        var declarations = Declarations(code);
        var search = 0;
        while (true)
        {
            var start = code.IndexOf(".Register(", search, StringComparison.Ordinal);
            if (start < 0)
                yield break;
            search = start + ".Register(".Length;
            var open = start + ".Register".Length;
            var arguments = TopLevelArguments(code, open);
            if (arguments.Count < 2 || !IntegerLiteral.IsMatch(arguments[1].Trim()))
                continue;
            yield return $"opcode_literal {path}:{EnclosingType(declarations, path, start)}:{arguments[1].Trim()}";
        }
    }

    // Guard 2: `SendAsync(<integer literal>,`.
    private static IEnumerable<string> LiteralSendOffenders(string code, string path)
    {
        var declarations = Declarations(code);
        foreach (Match match in SendLiteral.Matches(code))
            yield return $"opcode_literal {path}:{EnclosingType(declarations, path, match.Index)}:{match.Groups[1].Value}";
    }

    // Guard 4: strings passed to S2C/C2S/Find, in code with the comments removed (strings are the subject).
    private static IEnumerable<string> OpcodeNameLiterals(string code)
    {
        foreach (Match match in S2CLiteral.Matches(code))
            yield return match.Groups[1].Value;
        foreach (Match match in C2SLiteral.Matches(code))
            yield return match.Groups[1].Value;
        foreach (Match match in FindLiteral.Matches(code))
            yield return match.Groups[1].Value;
    }

    private static IReadOnlyList<(int Index, string Name)> Declarations(string code) =>
        TypeDeclaration.Matches(code).Select(match => (match.Index, match.Groups[1].Value)).ToArray();

    private static string EnclosingType(IReadOnlyList<(int Index, string Name)> declarations, string path, int index) =>
        declarations.Where(declaration => declaration.Index < index).Select(declaration => declaration.Name).LastOrDefault()
        ?? Path.GetFileNameWithoutExtension(path);

    private static IReadOnlyList<string> TopLevelArguments(string code, int openParen)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = openParen + 1;
        for (var index = openParen; index < code.Length; index++)
        {
            var current = code[index];
            if (current is '(' or '[' or '{')
            {
                depth++;
                continue;
            }
            if (current is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    arguments.Add(code[start..index]);
                    return arguments;
                }
                continue;
            }
            if (current == ',' && depth == 1)
            {
                arguments.Add(code[start..index]);
                start = index + 1;
            }
        }
        return arguments;
    }

    private static void AddMatches(SortedSet<string> lines, string guard, string path, IEnumerable<string> identifiers)
    {
        foreach (var identifier in identifiers)
            lines.Add($"{guard} {path}:{identifier}");
    }

    private static SortedSet<string> LoadOpcodeNames(string root)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var profiles = Path.Combine(root, "profiles");
        foreach (var file in Directory.EnumerateFiles(profiles, "opcodes.yaml", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var stream = new YamlStream();
            using (var reader = new StreamReader(file))
                stream.Load(reader);
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode document)
                continue;
            if (MappingChild(document, "opcodes") is not YamlSequenceNode rows)
                continue;
            foreach (var row in rows.Children)
            {
                if (row is YamlMappingNode mapping && MappingChild(mapping, "name") is YamlScalarNode { Value: { Length: > 0 } name })
                    names.Add(name);
            }
        }
        return names;
    }

    private static YamlNode? MappingChild(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
                return pair.Value;
        }
        return null;
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Order(StringComparer.Ordinal);

    private static bool IsDialectFile(string root, string file)
    {
        var plugins = Path.Combine(root, "src", "Plugins");
        var relative = Path.GetRelativePath(plugins, file);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.StartsWith("BubbleFighter.Fantasy.Dialect", StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RelativePath(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Format(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Refactor guard offenders; every line records a place a unification rule is not met yet.");
        builder.AppendLine("# The set may only shrink. See RefactorGuardTests; regenerate with BFF_RATCHET_WRITE=1.");
        foreach (var line in lines)
            builder.AppendLine(line);
        return builder.ToString();
    }

    private static IReadOnlyList<string> Parse(IEnumerable<string> lines) =>
        lines.Where(line => line.Length > 0 && line[0] != '#').ToList();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("The test does not run under the repository.");
    }

    private static string Strip(string source, bool stripStrings)
    {
        var builder = new StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            var current = source[index];
            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    builder.Append(' ');
                    index++;
                }
                continue;
            }
            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                builder.Append("  ");
                index += 2;
                while (index < source.Length)
                {
                    if (current == '*' && index + 1 < source.Length && source[index + 1] == '/')
                    {
                        builder.Append("  ");
                        index += 2;
                        break;
                    }
                    builder.Append(source[index] == '\n' ? '\n' : ' ');
                    index++;
                }
                continue;
            }
            if (current == '"')
            {
                var start = index;
                index = RawStringEnd(source, index) ?? StringEnd(source, index);
                Append(builder, source, start, index, stripStrings);
                continue;
            }
            if (current == '\'')
            {
                var start = index;
                index++;
                while (index < source.Length)
                {
                    if (source[index] == '\\')
                    {
                        index += 2;
                        continue;
                    }
                    if (source[index] == '\'')
                    {
                        index++;
                        break;
                    }
                    index++;
                }
                Append(builder, source, start, index, stripStrings);
                continue;
            }
            builder.Append(current);
            index++;
        }
        return builder.ToString();
    }

    private static int? RawStringEnd(string source, int start)
    {
        var quotes = 0;
        while (start + quotes < source.Length && source[start + quotes] == '"')
            quotes++;
        if (quotes < 3)
            return null;
        var close = source.IndexOf(new string('"', quotes), start + quotes, StringComparison.Ordinal);
        return close < 0 ? source.Length : close + quotes;
    }

    private static int StringEnd(string source, int start)
    {
        var verbatim = start > 0 && source[start - 1] == '@';
        var index = start + 1;
        while (index < source.Length)
        {
            if (!verbatim && source[index] == '\\')
            {
                index += 2;
                continue;
            }
            if (source[index] == '"')
            {
                if (verbatim && index + 1 < source.Length && source[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        return source.Length;
    }

    private static void Append(StringBuilder builder, string source, int start, int end, bool stripStrings)
    {
        for (var index = start; index < end && index < source.Length; index++)
            builder.Append(stripStrings && source[index] != '\n' ? ' ' : source[index]);
    }

    private static readonly Regex OpcodeConstant = new(
        @"\bconst\s+(?:ushort|int|short|byte)\s+((?:C2S|S2C|Cs|Sc)\w*)", RegexOptions.Compiled);

    private static readonly Regex OpcodeClass = new(
        @"\bstatic\s+class\s+(\w*Opcodes)\b", RegexOptions.Compiled);

    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|record(?:\s+(?:class|struct))?|struct|interface)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

    private static readonly Regex BuildTag = new(
        @"(_?Kr\d+|_?Cn\d+|_?Tw\d+|_?Id\d+|\d{3,4}$|Legacy|Modern|MidCn)", RegexOptions.Compiled);

    private static readonly Regex SendLiteral = new(
        @"\bSendAsync\s*\(\s*(0[xX][0-9a-fA-F]+|\d+)\s*,", RegexOptions.Compiled);

    private static readonly Regex S2CLiteral = new(
        @"\.S2C\s*\(\s*@?""((?:[^""\\]|\\.)*)""\s*\)", RegexOptions.Compiled);

    private static readonly Regex C2SLiteral = new(
        @"\.C2S\s*\(\s*@?""((?:[^""\\]|\\.)*)""\s*\)", RegexOptions.Compiled);

    private static readonly Regex FindLiteral = new(
        @"\.Find\s*\([^()]*,\s*@?""((?:[^""\\]|\\.)*)""\s*\)", RegexOptions.Compiled);

    private static readonly Regex IntegerLiteral = new(
        @"^(?:0[xX][0-9a-fA-F]+|\d+)$", RegexOptions.Compiled);
}
