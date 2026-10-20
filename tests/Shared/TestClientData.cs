using System.Text;

namespace BubbleFighter.Fantasy.Tests;

/// <summary>
/// Builds a loose-file client-data tree the <c>clientdata</c> row can serve in a test host: files are written
/// by their client paths under <c>raw/</c>, the override directory of the real reader, and an empty
/// <c>data/</c> archive directory beside it. Each named tree is written once per test process.
/// </summary>
internal static class TestClientData
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string DataDirectory, string RawDirectory)> Trees = new(StringComparer.Ordinal);

    /// <summary>Gets a tree holding the given client files, writing it on first use.</summary>
    /// <param name="name">A stable name for the tree within this test process.</param>
    /// <param name="files">The client path and bytes of every loose file.</param>
    /// <returns>The data and raw directories for a <c>clientdata</c> row.</returns>
    public static (string DataDirectory, string RawDirectory) Tree(string name, params (string ClientPath, byte[] Content)[] files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (Gate)
        {
            if (Trees.TryGetValue(name, out var existing))
                return existing;
            var root = Path.Combine(TestTempDirectories.ForProcess("bff-test-clientdata"), name);
            var data = Path.Combine(root, "data");
            var raw = Path.Combine(root, "raw");
            Directory.CreateDirectory(data);
            foreach (var (clientPath, content) in files)
            {
                var target = Path.Combine(raw, clientPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, content);
            }
            return Trees[name] = (data, raw);
        }
    }

    /// <summary>Gets a client-data tree holding the synthetic shop workbook the public tests pin.</summary>
    /// <returns>The data and raw directories.</returns>
    public static (string DataDirectory, string RawDirectory) SyntheticShop() =>
        Tree("synthetic-shop", ("Config/ShopItem2.xml", Encoding.UTF8.GetBytes(SyntheticClientTables.ShopItem2Content)));

    /// <summary>Gets the configuration of a <c>clientdata</c> row over a tree.</summary>
    /// <param name="tree">The data and raw directories.</param>
    /// <returns>The row configuration.</returns>
    public static Dictionary<string, object?> RowConfig((string DataDirectory, string RawDirectory) tree) => new()
    {
        ["data_directory"] = tree.DataDirectory,
        ["raw_directory"] = tree.RawDirectory,
    };
}
