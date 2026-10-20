using BubbleFighter.Fantasy.Archives;

namespace BubbleFighter.Fantasy.Tests;

/// <summary>
/// Reads one client file the way the clientdata row does: a loose copy under
/// <c>client-data/&lt;build&gt;/dataraw</c> first, otherwise the member of a <c>.pkn</c> or <c>.bfz</c>
/// archive under <c>client-data/&lt;build&gt;/data</c>. The tree is the developer's own client data at the
/// repository root; tests that use it carry the <c>LocalClientData</c> category, and a missing tree fails them.
/// </summary>
internal static class LocalClientData
{
    private static readonly Dictionary<string, string> Extracted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes the build's client file to a temporary path once per test process and returns that path.</summary>
    /// <param name="build">The client build id, for example <c>kr-812</c>.</param>
    /// <param name="clientPath">The file's path as the client names it, for example <c>Config/ShopItem2.xml</c>.</param>
    /// <returns>The path of the extracted copy.</returns>
    public static string Extract(string build, string clientPath)
    {
        lock (Extracted)
        {
            var key = build + "|" + clientPath;
            if (Extracted.TryGetValue(key, out var existing))
                return existing;
            var target = Path.Combine(TestTempDirectories.ForProcess("bff-client-data"), build, clientPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, Bytes(build, clientPath));
            Extracted[key] = target;
            return target;
        }
    }

    /// <summary>Reads one client file from the junction's archives or loose files.</summary>
    /// <param name="build">The client build id, for example <c>kr-1256</c>.</param>
    /// <param name="clientPath">The file's path as the client names it, for example <c>Config/ShopItem2.xml</c>.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Bytes(string build, string clientPath)
    {
        var root = Path.Combine(RepositoryRoot(), "client-data", build);
        var loose = Path.Combine(root, "dataraw", clientPath);
        if (File.Exists(loose))
            return File.ReadAllBytes(loose);
        var data = Path.Combine(root, "data");
        if (!Directory.Exists(data))
            throw new InvalidOperationException($"Provision the {build} client data under '{root}' (data/ or dataraw/).");
        foreach (var file in Directory.EnumerateFiles(data, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".pkn", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            using var archive = Archive.Open(file, Path.GetRelativePath(data, file).Replace('\\', '/'));
            if (archive.Contains(clientPath))
                return archive.Read(clientPath);
        }
        throw new FileNotFoundException($"No archive under '{data}' holds '{clientPath}'.", clientPath);
    }

    /// <summary>Gets the directories a <c>clientdata</c> row reads for one build.</summary>
    /// <param name="build">The client build id, for example <c>kr-1256</c>.</param>
    /// <returns>The <c>data</c> and <c>dataraw</c> directories of the build's junction tree.</returns>
    public static (string DataDirectory, string RawDirectory) Directories(string build)
    {
        var root = Path.Combine(RepositoryRoot(), "client-data", build);
        return (Path.Combine(root, "data"), Path.Combine(root, "dataraw"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
    }
}
