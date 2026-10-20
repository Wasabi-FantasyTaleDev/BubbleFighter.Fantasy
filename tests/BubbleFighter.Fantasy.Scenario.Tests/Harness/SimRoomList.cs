using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Scenario.Tests.Harness;

/// <summary>One room-list row as a simulated client needs it.</summary>
/// <param name="Id">SessionSetting +0, the id C2S 403 echoes back (<c>kr-1256 sub_854C90</c>).</param>
/// <param name="Name">SessionSetting +8, the room name.</param>
/// <param name="Master">SessionSetting +40, the room master's nickname (bound to <c>lbRoomMaster</c> at <c>0x63C240</c>).</param>
internal sealed record SimRoom(ushort Id, string Name, string Master);

/// <summary>
/// Reads S2C 101 room lists for simulated clients. The simulator keeps its own reader so it never
/// loads a game plugin assembly into the host's context; it walks the same client layout as the
/// server codec (<c>SessionSetting</c> reader <c>kr-1256 sub_113BD20</c>, nested <c>MapMeta</c> reader
/// <c>kr-1256 sub_113D030</c>) and keeps only the fields a scenario uses.
/// </summary>
internal static class SimRoomList
{
    /// <summary>
    /// Picks the room a client should join from a list read before and after another client
    /// created it: the row carrying <paramref name="installId"/> when that id is listed, otherwise
    /// the single row that was not listed before. Fails when the room cannot be identified.
    /// </summary>
    public static ushort FindNewRoom(IReadOnlyList<SimRoom> before, IReadOnlyList<SimRoom> after, ushort installId)
    {
        if (installId != 0 && after.Any(r => r.Id == installId))
            return installId;
        var known = before.Select(r => r.Id).ToHashSet();
        var fresh = after.Where(r => !known.Contains(r.Id)).ToList();
        return fresh.Count == 1
            ? fresh[0].Id
            : throw new InvalidOperationException(
                $"cannot identify the new room: install id {installId}, {fresh.Count} new list rows " +
                $"({string.Join(", ", fresh.Select(r => r.Id))})");
    }

    private const uint MaxCount = 4096;

    /// <summary>Parses S2C 101: three i32 headers, a u32 count, then that many <c>SessionSetting</c> records.</summary>
    public static IReadOnlyList<SimRoom> Parse(byte[] payload)
    {
        var r = new PacketReader(payload);
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
        var count = r.ReadUInt32();
        if (count > 1024)
            throw new PacketFormatException($"S2C 101 room count {count} is implausible.", 12);
        var rooms = new List<SimRoom>((int)count);
        for (var i = 0; i < count; i++)
            rooms.Add(ReadSessionSetting(ref r));
        return rooms;
    }

    /// <summary>
    /// Reads <c>SessionSetting.kind</c> from an S2C 117 body (leading <c>SessionSetting</c>).
    /// Menu return uses kind 1 (<c>kr-1256 sub_9AC490</c> case at <c>0x9AC9E4</c>); do not confuse with the
    /// login character-session kind 2.
    /// </summary>
    public static int ReadSessionKind(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        SkipToSessionKind(ref r);
        return r.ReadInt32();
    }

    /// <summary>Reads one <c>SessionSetting</c> (with its <c>MapMeta</c>) and returns the list-row fields.</summary>
    public static SimRoom ReadSessionSetting(ref PacketReader r)
    {
        var id = r.ReadUInt16();
        r.ReadInt32();
        var name = r.ReadWideStringPrefixed();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadInt32();
        var master = r.ReadWideStringPrefixed();
        r.ReadInt32(); // kind
        SkipMapMeta(ref r);
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt64();
        r.ReadWideStringPrefixed();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadUInt16();
        return new SimRoom(id, name, master);
    }

    private static void SkipToSessionKind(ref PacketReader r)
    {
        r.ReadUInt16();
        r.ReadInt32();
        r.ReadWideStringPrefixed();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadInt32();
        r.ReadWideStringPrefixed();
    }

    private static void SkipMapMeta(ref PacketReader r)
    {
        r.ReadInt32();
        r.ReadUInt16();
        for (var i = 0; i < 7; i++)
            r.ReadInt32();
        for (var i = 0; i < 4; i++)
            r.ReadWideStringPrefixed();
        r.ReadInt32();
        for (var i = 0; i < 6; i++)
            r.ReadWideStringPrefixed();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
        var pairs = Count(ref r);
        for (var i = 0; i < pairs; i++)
        {
            r.ReadInt32();
            r.ReadInt32();
        }
        r.ReadInt32();
        var keyed = Count(ref r);
        for (var i = 0; i < keyed; i++)
        {
            r.ReadInt32();
            r.ReadWideStringPrefixed();
        }
        for (var i = 0; i < 5; i++)
            r.ReadInt32();
        var rows = Count(ref r);
        for (var i = 0; i < rows; i++)
        {
            r.ReadUInt16();
            var ints = Count(ref r);
            for (var j = 0; j < ints; j++)
                r.ReadInt32();
        }
        r.ReadInt32();
        r.ReadInt32();
        r.ReadByte();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadWideStringPrefixed();
        r.ReadWideStringPrefixed();
        r.ReadInt32();
    }

    private static uint Count(ref PacketReader r)
    {
        var count = r.ReadUInt32();
        return count <= MaxCount ? count : throw new PacketFormatException($"MapMeta vector count {count} is implausible.", 0);
    }
}
