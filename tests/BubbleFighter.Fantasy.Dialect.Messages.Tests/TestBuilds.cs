using System.Collections.Concurrent;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>The legacy builds as the repository's packet tables describe them.</summary>
internal static class TestBuilds
{
    private static readonly Lazy<RoomTables> Loaded = new(() => RoomTables.Load(PacketsDirectory()));

    private static readonly Lazy<PacketTables> LoadedTables = new(() => PacketTables.Load(PacketsDirectory()));

    private static readonly ConcurrentDictionary<string, IOpcodeTable> Tables = new(StringComparer.Ordinal);

    /// <summary>Gets the repository's packet tables.</summary>
    public static PacketTables Packets => LoadedTables.Value;

    /// <summary>Gets every build read from <c>packets/</c>.</summary>
    public static RoomTables All => Loaded.Value;

    /// <summary>Gets one build's struct tables and play row.</summary>
    public static (RoomSchema Schema, PlayBuild Play) Of(string build) => All.Of(build);

    /// <summary>Loads one build's game-connection opcode table from its profile folder.</summary>
    /// <param name="build">The profile and build name, for example <c>kr-812</c>.</param>
    /// <returns>The opcode table view.</returns>
    public static IOpcodeTable Opcodes(string build) => Tables.GetOrAdd(build, static name =>
    {
        var profile = new ClientProfile(name, name[..2], int.Parse(name[3..], CultureInfo.InvariantCulture), 0, 0, name);
        var path = Path.Combine(Directory.GetParent(PacketsDirectory())!.FullName, "profiles", name, "opcodes.yaml");
        return OpcodeTableLoader.Load(path, profile).ForConnection(OpcodeConnection.Game);
    });

    /// <summary>Finds the repository's <c>packets</c> directory from the test output folder.</summary>
    public static string PacketsDirectory()
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
