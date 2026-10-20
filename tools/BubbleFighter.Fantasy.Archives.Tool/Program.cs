using BubbleFighter.Fantasy.Archives;

namespace BubbleFighter.Fantasy.Archives.Tool;

/// <summary>Command-line front end for <see cref="Archive"/>, <see cref="PknWriter"/> and <see cref="BfzArchive"/>.</summary>
public static class Program
{
    private const string Usage = """
        bffarchive - read and write the client's .pkn and .bfz archives

          bffarchive                     (no arguments: interactive menu)
          bffarchive list    <archive> [--path <client-relative path>]
          bffarchive extract <archive> <output dir> [--path <client-relative path>] [--filter <text>]
          bffarchive verify  <archive> [--path <client-relative path>]
          bffarchive pack    <input dir> <output archive> [--path <client-relative path>] [--no-compress] [--no-encrypt]
          bffarchive scan    <client root>
          bffarchive index   <client root>
          bffarchive generate <build> <client root> <build data dir> [--max-files <count>] [--max-bytes <bytes>]
          bffarchive copy    <client root> <build data dir> <client path>... [--from <list file>]
          bffarchive unpack  <client root> <build data dir> <client path>... [--from <list file>]

        Every archive opens the same way: .bfz, early .pkn and version-2 .pkn are told apart from
        the file. Keys come from the archive's path relative to the client root, case included
        (Packs/Config_00000.pkn, Pack/Config0000.pkn, Config/Config.bfz). Without --path a .pkn is
        assumed to sit in Packs/ (version 2) or Pack/ (early), a .bfz in a folder of its own name.
        pack writes a version-2 .pkn or a .bfz by the output's extension; --no-compress and
        --no-encrypt apply to .pkn only. scan opens and verifies every archive under a client root.
        index prints one tab-separated line per member: archive path, member path, size.
        scan, index, copy and unpack consider only the archives the client mounts: .pkn packs directly in
        the root Pack/ or Packs/ folder (every one of them), and .bfz archives named after their folder
        that hold files under it (a file is read from the nearest folder's archive). Backups such as
        Packs/old/ and patch staging such as Pkg/ are skipped.
        copy finds the archives holding the given client paths (for example ServerShared/MapMeta.xml)
        and copies each into <build data dir>/data at its original client-relative path. unpack
        writes the given members as loose files into <build data dir>/dataraw at their client paths;
        the server reads dataraw before data. --from adds the client paths listed in a file, one per line.
        Every command that writes files records where each came from in a tab-separated sources.tsv
        (target, source archive, member, size, sha256): <build data dir>/sources.tsv for copy and
        unpack, <output dir>/sources.tsv for extract, <output archive>.sources.tsv for pack.
        """;

    /// <summary>Runs one command, or the interactive menu without arguments, and returns the process exit code.</summary>
    /// <param name="args">The command line.</param>
    public static int Main(string[] args)
    {
        // Member names include Korean and Chinese text; scripts read this output as UTF-8.
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length == 0)
        {
            if (Console.IsInputRedirected)
            {
                Console.WriteLine(Usage);
                return 2;
            }
            return Interactive(Console.In, Console.Out);
        }
        return Run(args);
    }

    private static int Run(string[] args)
    {
        try
        {
            if (args[0] is "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var (positional, options) = Parse(args[1..]);
            if (options.ContainsKey("pkn-layout"))
                throw new ArgumentException("--pkn-layout is no longer needed: the PKN layout is detected from the file.");
            return args[0] switch
            {
                "list" when positional.Count == 1 => List(positional[0], options),
                "extract" when positional.Count == 2 => Extract(positional[0], positional[1], options),
                "verify" when positional.Count == 1 => Verify(positional[0], options),
                "pack" when positional.Count == 2 => Pack(positional[0], positional[1], options),
                "scan" when positional.Count == 1 => Scan(positional[0]),
                "index" when positional.Count == 1 => Index(positional[0]),
                "generate" when positional.Count == 3 => Generate(positional, options),
                "copy" when positional.Count >= 2 => Copy(positional[0], positional[1], Wanted(positional, options)),
                "unpack" when positional.Count >= 2 => Unpack(positional[0], positional[1], Wanted(positional, options)),
                _ => Fail("unknown command or wrong arguments\n\n" + Usage),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
            or ArgumentException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>Asks for a command and its arguments, runs it, and repeats until the user quits.</summary>
    /// <param name="input">Where answers are read from.</param>
    /// <param name="output">Where the menu is written.</param>
    /// <returns>The exit code of the last command, or 0.</returns>
    public static int Interactive(TextReader input, TextWriter output)
    {
        (string Name, string Title, string[] Prompts)[] menu =
        [
            ("list", "List the files inside an archive", ["Archive file"]),
            ("extract", "Extract an archive into a folder", ["Archive file", "Output folder"]),
            ("verify", "Verify every file of an archive", ["Archive file"]),
            ("pack", "Pack a folder into a .pkn or .bfz", ["Input folder", "Output archive (.pkn or .bfz)"]),
            ("scan", "Verify every archive of a client", ["Client folder"]),
            ("copy", "Copy the archives holding client files into <build>/data", ["Client folder", "Build data folder (client-data/<build>)", "Client paths, separated by spaces"]),
            ("unpack", "Unpack client files into <build>/dataraw", ["Client folder", "Build data folder (client-data/<build>)", "Client paths, separated by spaces"]),
        ];
        var last = 0;
        while (true)
        {
            output.WriteLine();
            output.WriteLine("bffarchive - client archives (.bfz, .pkn of any layout)");
            for (var i = 0; i < menu.Length; i++)
                output.WriteLine($"  [{i + 1}] {menu[i].Title}");
            output.WriteLine("  [0] Quit");
            output.Write("Choose: ");
            var choice = input.ReadLine()?.Trim();
            if (choice is null or "0" or "q")
                return last;
            if (!int.TryParse(choice, out var number) || number < 1 || number > menu.Length)
            {
                output.WriteLine("Please type one of the numbers above.");
                continue;
            }
            var (name, _, prompts) = menu[number - 1];
            var args = new List<string> { name };
            var cancelled = false;
            foreach (var prompt in prompts)
            {
                output.Write(prompt + ": ");
                var answer = input.ReadLine()?.Trim().Trim('"');
                if (string.IsNullOrEmpty(answer)) { cancelled = true; break; }
                if (prompt.StartsWith("Client paths", StringComparison.Ordinal))
                    args.AddRange(answer.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                else
                    args.Add(answer);
            }
            if (cancelled)
            {
                output.WriteLine("Cancelled.");
                continue;
            }
            if (name is "list" or "extract" or "verify" or "pack")
            {
                output.Write("Client-relative path of the archive (Enter to infer it from the file name): ");
                var relative = input.ReadLine()?.Trim().Trim('"');
                if (!string.IsNullOrEmpty(relative)) args.AddRange(["--path", relative]);
            }
            last = Run([.. args]);
        }
    }

    private static int List(string archive, Dictionary<string, string?> options)
    {
        using var reader = Archive.Open(archive, options.GetValueOrDefault("path"));
        foreach (var entry in reader.Entries)
            Console.WriteLine($"{entry.Size,12}  {entry.Path}");
        Console.WriteLine($"{reader.Entries.Count} member(s)");
        return 0;
    }

    private static int Generate(List<string> args, Dictionary<string, string?> options)
    {
        // These CLI limits match the installation defaults and can be overridden explicitly.
        var fileLimit = 32768;
        var byteLimit = 17179869184L;
        if (options.TryGetValue("max-files", out var files) &&
            (!int.TryParse(files, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out fileLimit) || fileLimit < 1))
            throw new ArgumentException("--max-files requires a positive integer.");
        if (options.TryGetValue("max-bytes", out var bytes) &&
            (!long.TryParse(bytes, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out byteLimit) || byteLimit < 1))
            throw new ArgumentException("--max-bytes requires a positive integer.");
        new ClientDataGenerator().Generate(args[0], args[1], args[2], fileLimit, byteLimit, CancellationToken.None);
        Console.WriteLine($"Generated client data: {Path.GetFullPath(args[2])}");
        return 0;
    }

    private static int Extract(string archive, string output, Dictionary<string, string?> options)
    {
        using var reader = Archive.Open(archive, options.GetValueOrDefault("path"));
        var filter = options.GetValueOrDefault("filter");
        var sources = new Sources(Path.Combine(output, Sources.FileName));
        foreach (var entry in reader.Entries)
        {
            if (filter is not null && !entry.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var bytes = reader.Read(entry.Path);
            WriteMember(output, entry.Path, bytes);
            sources.Add(entry.Path, Path.GetFullPath(archive), entry.Path, bytes);
        }
        sources.Save();
        Console.WriteLine($"extracted {sources.Count} member(s) to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int Verify(string archive, Dictionary<string, string?> options)
    {
        using var reader = Archive.Open(archive, options.GetValueOrDefault("path"));
        foreach (var entry in reader.Entries)
            _ = reader.Read(entry.Path);
        Console.WriteLine($"{reader.Entries.Count} member(s) verified");
        return 0;
    }

    private static int Pack(string input, string output, Dictionary<string, string?> options)
    {
        var root = Path.GetFullPath(input);
        var members = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(relative => (relative, File.ReadAllBytes(Path.Combine(root, relative))))
            .ToList();
        var relativePath = options.GetValueOrDefault("path");
        using var stream = File.Create(output);
        if (output.EndsWith(".pkn", StringComparison.OrdinalIgnoreCase))
        {
            var uri = relativePath?.Replace('\\', '/').ToLowerInvariant() ?? PknFormat.UriForPack(Path.GetFileName(output));
            PknWriter.Write(stream, uri, members, !options.ContainsKey("no-compress"), !options.ContainsKey("no-encrypt"));
        }
        else if (output.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase))
        {
            BfzArchive.Write(stream, relativePath ?? BfzFormat.InferRelativePath(output), members);
        }
        else
        {
            throw new NotSupportedException("the output must end in .pkn or .bfz");
        }
        var sources = new Sources(output + "." + Sources.FileName);
        foreach (var (relative, bytes) in members)
            sources.Add(relative, Path.Combine(root, relative), "", bytes);
        sources.Save();
        Console.WriteLine($"packed {members.Count} member(s) into {output}");
        return 0;
    }

    private static int Scan(string clientRoot)
    {
        int archives = 0, members = 0, failures = 0;
        foreach (var (file, relative) in Archives(clientRoot))
        {
            archives++;
            try
            {
                using var reader = Archive.Open(file, relative);
                foreach (var entry in reader.Entries)
                    _ = reader.Read(entry.Path);
                members += reader.Entries.Count;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                failures++;
                Console.WriteLine($"FAIL {relative}: {ex.Message}");
            }
        }
        Console.WriteLine($"{archives} archive(s), {members} member(s) verified, {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static int Index(string clientRoot)
    {
        var failures = 0;
        foreach (var (file, relative) in Archives(clientRoot))
        {
            try
            {
                using var reader = Archive.Open(file, relative);
                if (!Reachable(relative, reader))
                {
                    Console.Error.WriteLine($"bffarchive: skipped {relative}: the client never reads its files from there");
                    continue;
                }
                foreach (var entry in reader.Entries)
                    Console.WriteLine($"{relative}\t{entry.Path}\t{entry.Size}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                failures++;
                Console.Error.WriteLine($"bffarchive: skipped {relative}: {ex.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Copy(string clientRoot, string destination, IReadOnlyList<string> wanted)
    {
        var copies = Locate(clientRoot, wanted, out var missing);
        if (missing.Count > 0)
            return Fail("not found in any archive: " + string.Join(", ", missing));
        var data = Path.Combine(destination, "data");
        var sources = new Sources(Path.Combine(destination, Sources.FileName));
        foreach (var (source, relative, held) in copies)
        {
            var target = Inside(data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            sources.Add("data/" + relative, relative, "", File.ReadAllBytes(target));
            Console.WriteLine($"{relative}: {string.Join(", ", held)}");
        }
        sources.Save();
        Console.WriteLine($"copied {copies.Count} archive(s) into {Path.GetFullPath(data)}");
        return 0;
    }

    private static int Unpack(string clientRoot, string destination, IReadOnlyList<string> wanted)
    {
        var copies = Locate(clientRoot, wanted, out var missing);
        if (missing.Count > 0)
            return Fail("not found in any archive: " + string.Join(", ", missing));
        var raw = Path.Combine(destination, "dataraw");
        var sources = new Sources(Path.Combine(destination, Sources.FileName));
        foreach (var (source, relative, held) in copies)
        {
            using var reader = Archive.Open(source, relative);
            foreach (var member in held)
            {
                var stored = reader.Entries.First(e => e.Path.Equals(member, StringComparison.OrdinalIgnoreCase)).Path;
                var bytes = reader.Read(stored);
                WriteMember(raw, stored, bytes);
                sources.Add("dataraw/" + stored, relative, stored, bytes);
            }
        }
        sources.Save();
        Console.WriteLine($"unpacked {sources.Count} file(s) into {Path.GetFullPath(raw)}");
        return 0;
    }

    // Each wanted client path goes to the first archive (in path order) holding it, as the client's lookup does.
    private static List<(string Source, string Relative, List<string> Held)> Locate(string clientRoot, IReadOnlyList<string> wanted,
        out HashSet<string> remaining)
    {
        remaining = new HashSet<string>(wanted.Select(p => p.Replace('\\', '/').TrimStart('/')), StringComparer.OrdinalIgnoreCase);
        var found = new List<(string, string, List<string>)>();
        foreach (var (file, relative) in Archives(clientRoot))
        {
            if (remaining.Count == 0)
                break;
            IArchiveReader reader;
            try { reader = Archive.Open(file, relative); }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"bffarchive: skipped {relative}: {ex.Message}");
                continue;
            }
            using var opened = reader;
            if (!Reachable(relative, reader))
                continue;
            var held = reader.Entries.Select(e => e.Path).Where(remaining.Contains).ToList();
            if (held.Count == 0)
                continue;
            remaining.ExceptWith(held);
            found.Add((file, relative, held));
        }
        return found;
    }

    private static List<string> Wanted(List<string> positional, Dictionary<string, string?> options)
    {
        var wanted = positional[2..];
        if (options.GetValueOrDefault("from") is { } list)
            wanted.AddRange(File.ReadAllLines(list).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')));
        if (wanted.Count == 0)
            throw new ArgumentException("name at least one client path, or a --from list file");
        return wanted;
    }

    // The archives the client mounts. A pack client opens every.pkn directly in its root Pack/ or Packs/ folder
    // (cn-279 sub_8A18B0, kr-1256 sub_1018640); a.bfz client opens <folder>/<folder>.bfz for a file under that
    // folder (kr-812 sub_A4B600), so a.bfz named otherwise is never opened.
    private static IEnumerable<(string File, string Relative)> Archives(string clientRoot)
    {
        var root = Path.GetFullPath(clientRoot);
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Select(f => (File: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(pair => Mounted(pair.Relative))
            .OrderBy(pair => pair.Relative, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Mounted(string relative)
    {
        return ClientDataGenerator.IsMounted(relative);
    }

    // A.bfz serves only files under its own folder; one whose files live elsewhere (a staged update copied into
    // another folder) is never reached by the client's lookup.
    private static bool Reachable(string relative, IArchiveReader reader)
    {
        return ClientDataGenerator.IsReachable(relative, reader);
    }

    private static void WriteMember(string root, string memberPath, byte[] bytes)
    {
        var target = Inside(root, memberPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
    }

    private static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(full, relative));
        if (!target.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{relative}' would escape {full}");
        return target;
    }

    private static (List<string> Positional, Dictionary<string, string?> Options) Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(args[i]);
                continue;
            }
            var name = args[i][2..];
            if (name is "no-compress" or "no-encrypt")
                options[name] = null;
            else if (i + 1 < args.Length)
                options[name] = args[++i];
            else
                throw new ArgumentException($"--{name} needs a value");
        }
        return (positional, options);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("bffarchive: " + message);
        return 1;
    }

    /// <summary>
    /// The tab-separated provenance file: one row per written file with its target path, the archive (or input
    /// file) it came from, the member inside that archive, its size and SHA-256. Rows already in the file are kept
    /// unless the same target is written again.
    ///
    /// </summary>
    private sealed class Sources
    {
        internal const string FileName = "sources.tsv";
        private const string Header = "target\tsource\tmember\tsize\tsha256";
        private readonly string _path;
        private readonly SortedDictionary<string, string> _rows = new(StringComparer.Ordinal);
        private int _added;

        internal Sources(string path)
        {
            _path = path;
            if (File.Exists(path))
                foreach (var line in File.ReadLines(path).Skip(1).Where(line => line.Length > 0))
                    _rows[line.Split('\t')[0]] = line;
        }

        internal int Count => _added;

        internal void Add(string target, string source, string member, byte[] bytes)
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            _rows[target] = string.Join('\t', target, source.Replace('\\', '/'), member, bytes.Length, hash);
            _added++;
        }

        internal void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllLines(_path, _rows.Values.Prepend(Header));
        }
    }
}
