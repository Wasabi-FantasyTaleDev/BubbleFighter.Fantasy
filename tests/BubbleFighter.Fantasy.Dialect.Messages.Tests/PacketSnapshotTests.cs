using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Serializes snapshot identity tests against other packet-table readers in this assembly.</summary>
[CollectionDefinition("Packet snapshots", DisableParallelization = true)]
public sealed class PacketSnapshotCollection;

/// <summary>Checks snapshot reuse, source freshness, isolation and collection without changing packet layouts.</summary>
[Collection("Packet snapshots")]
public sealed class PacketSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-snapshot-" + Guid.NewGuid().ToString("N"));
    private const string Fields = "version: 1\nstructs:\n  Seat:\n    - level: u32\n";

    public PacketSnapshotTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "builds.yaml"),
            "version: 1\nbuilds:\n  base:\n    options: {value: original}\n    items: [one, two]\n  later: {base: base}\n");
        File.WriteAllText(Path.Combine(_root, "seat.yaml"), Fields);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ConcurrentLoadsShareOneLiveSnapshot()
    {
        var results = new PacketTables[24];
        Parallel.For(0, results.Length, i => results[i] = PacketTables.Load(_root));
        Assert.All(results, tables => Assert.Same(results[0], tables));
        Assert.Same(results[0], PacketTables.Load(Path.Combine(_root, ".")));
    }

    [Fact]
    public void SameLengthRewriteWithPreservedTimestampIsStillRead()
    {
        var old = PacketTables.Load(_root);
        var path = Path.Combine(_root, "seat.yaml");
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, Fields.Replace("u32", "u16", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, timestamp);
        var updated = PacketTables.Load(_root);
        Assert.NotSame(old, updated);
        Assert.Equal("u32", old.Resolve("later", "Seat")[0].Type);
        Assert.Equal("u16", updated.Resolve("later", "Seat")[0].Type);
    }

    [Fact]
    public void BuildValuesAreRefreshedAndReturnedMapsCannotMutateTheSnapshot()
    {
        var old = PacketTables.Load(_root);
        var values = old.ValuesOf("later");
        Assert.IsType<Dictionary<string, object?>>(values["options"])["value"] = "changed";
        Assert.IsType<string[]>(values["items"])[0] = "changed";
        var fresh = old.ValuesOf("later");
        Assert.Equal("original", Assert.IsType<Dictionary<string, object?>>(fresh["options"])["value"]);
        Assert.Equal("one", Assert.IsType<string[]>(fresh["items"])[0]);
        var path = Path.Combine(_root, "builds.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("original", "updated", StringComparison.Ordinal));
        var updated = PacketTables.Load(_root);
        Assert.NotSame(old, updated);
        Assert.Equal("updated", Assert.IsType<Dictionary<string, object?>>(updated.ValuesOf("later")["options"])["value"]);
    }

    [Fact]
    public void AddedRemovedAndRenamedSourcesInvalidateTheSnapshot()
    {
        var initial = PacketTables.Load(_root);
        var path = Path.Combine(_root, "other.yaml");
        File.WriteAllText(path, "version: 1\nstructs:\n  Other:\n    - count: u8\n");
        var added = PacketTables.Load(_root);
        Assert.NotSame(initial, added);
        Assert.Contains("Other", added.Structs);
        File.Move(path, Path.Combine(_root, "renamed.yaml"));
        var renamed = PacketTables.Load(_root);
        Assert.NotSame(added, renamed);
        Assert.Equal(added.Structs, renamed.Structs);
        File.Delete(Path.Combine(_root, "renamed.yaml"));
        var removed = PacketTables.Load(_root);
        Assert.NotSame(renamed, removed);
        Assert.DoesNotContain("Other", removed.Structs);
    }

    [Fact]
    public void InvalidOrMissingSourcesNeverReturnAStaleSnapshot()
    {
        var valid = PacketTables.Load(_root);
        var path = Path.Combine(_root, "seat.yaml");
        File.WriteAllText(path, "version: 1\nstructs: [\n");
        Assert.Throws<ConfigException>(() => PacketTables.Load(_root));
        Assert.Equal("level", valid.Resolve("base", "Seat")[0].Name);
        File.WriteAllText(path, Fields);
        Assert.Same(valid, PacketTables.Load(_root));
        File.Delete(Path.Combine(_root, "builds.yaml"));
        Assert.Throws<ConfigException>(() => PacketTables.Load(_root));
    }

    [Fact]
    public void PublicListsCannotBeMutatedByAnotherConsumer()
    {
        var tables = PacketTables.Load(_root);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(tables.Builds)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(tables.Structs).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<PacketField>>(tables.Resolve("base", "Seat")).Clear());
        Assert.Equal("level", PacketTables.Load(_root).Resolve("base", "Seat")[0].Name);
    }

    [Fact]
    public void IdenticalDirectoriesHaveIndependentSnapshots()
    {
        var first = PacketTables.Load(_root);
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        foreach (var file in Directory.GetFiles(_root, "*.yaml"))
            File.Copy(file, Path.Combine(other, Path.GetFileName(file)));
        var second = PacketTables.Load(other);
        Assert.NotSame(first, second);
        Assert.Equal(Path.GetFullPath(other), second.Directory);
        Assert.Equal(first.Resolve("base", "Seat"), second.Resolve("base", "Seat"));
    }

    [Fact]
    public void CacheDoesNotKeepAnUnusedSnapshotAlive()
    {
        var reference = LoadWeak(_root);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(reference.IsAlive);
        Assert.Equal("level", PacketTables.Load(_root).Resolve("base", "Seat")[0].Name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadWeak(string directory) => new(PacketTables.Load(directory));
}
