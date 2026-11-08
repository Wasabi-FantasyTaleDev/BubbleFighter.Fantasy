using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Reads and writes the shared NPC control words independently of tutorial scene layouts.</summary>
internal static class NpcWire
{
    /// <summary>
    /// Writes the spawn-group index consumed by cn-717 sub_744160 after cn-717 sub_747250 builds the request.
    /// </summary>
    public static byte[] WriteCreateMobsFromSpawnGroup(PacketTables tables, string build, int spawnGroup)
    {
        RequireSpawnGroup(tables, build);
        return PacketCodec.Write(tables, build, "NpcSpawnGroup",
            new Dictionary<string, object?> { ["spawn_group"] = spawnGroup });
    }

    /// <summary>Reads the native spawn-group word and rejects incomplete or trailing bytes.</summary>
    public static int ReadCreateMobsFromSpawnGroup(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        RequireSpawnGroup(tables, build);
        return (int)PacketCodec.ReadScalars(tables, build, "NpcSpawnGroup", payload)["spawn_group"];
    }

    /// <summary>
    /// Writes the wave word consumed by cn-717 sub_ABD9A0 and kr-1256 sub_7BAD80 to release the start latch.
    /// </summary>
    public static byte[] WriteStartNewWave(PacketTables tables, string build, int wave)
    {
        RequireWave(tables, build);
        return PacketCodec.Write(tables, build, "NpcWave",
            new Dictionary<string, object?> { ["wave"] = wave });
    }

    /// <summary>Reads the native start/end-wave word and rejects incomplete or trailing bytes.</summary>
    public static int ReadStartNewWave(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        RequireWave(tables, build);
        return (int)PacketCodec.ReadScalars(tables, build, "NpcWave", payload)["wave"];
    }

    private static void RequireSpawnGroup(PacketTables tables, string build) =>
        PacketLayoutContract.RequireSignature(tables, build, "NpcSpawnGroup", "NPC spawn group", "spawn_group:u32");

    private static void RequireWave(PacketTables tables, string build) =>
        PacketLayoutContract.RequireSignature(tables, build, "NpcWave", "NPC wave", "wave:u32");
}
