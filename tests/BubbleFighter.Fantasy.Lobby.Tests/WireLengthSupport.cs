using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Shared fixtures for the lobby's wire pins: the packet tables and a body writer.</summary>
internal static class WireLengthSupport
{
    /// <summary>The repository's packet tables.</summary>
    internal static PacketTables Tables { get; } = Load();

    internal static byte[] Written(Action<PacketWriter> write)
    {
        var w = PacketWriter.Create();
        write(w);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    internal static int PrefixedWideStringBytes(string value) => 4 + (2 * value.Length);

    private static PacketTables Load()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return PacketTables.Load(candidate);
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }
}
