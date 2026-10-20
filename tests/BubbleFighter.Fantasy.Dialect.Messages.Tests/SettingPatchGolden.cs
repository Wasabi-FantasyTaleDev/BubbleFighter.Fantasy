using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Dialect.Rooms;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Retains the previous ordinal map patch as an independent native-order byte oracle.</summary>
internal static class SettingPatchGolden
{
    public static byte[] ApplyGameSettingChange(RoomSchema schema, PacketTables tables, string build,
        ReadOnlySpan<byte> gameSetting, ReadOnlySpan<byte> change)
    {
        if (gameSetting.IsEmpty)
            gameSetting = RoomCodec.WriteGameSettingEnvelope(tables, build, schema,
                new RoomValues(RoomCodec.PlayRoomKind, []) { Play = true });
        if (change.IsEmpty)
            return gameSetting.ToArray();

        var applied = PlazaRoomWire.ReadChangeGameSetting(tables, build, change);
        var r = new PacketReader(gameSetting);
        var buffer = new ArrayBufferWriter<byte>(gameSetting.Length + 128);
        var w = new PacketWriter(buffer);
        var strings = 0;
        var bools = 0;
        for (var i = 0; i < schema.GameSetting.Length; i++)
        {
            var field = schema.GameSetting[i];
            var start = r.Offset;
            if (field.Type == RoomFieldType.WStr)
            {
                var text = r.ReadWideStringPrefixed();
                // The first four strings sit at +16, +44, +72 and +100.
                w.WriteWideStringPrefixed(strings++ switch
                {
                    0 => applied.DisplayName,
                    1 => applied.CurrentMap,
                    2 => applied.Unknown72,
                    3 => applied.MapKey,
                    _ => text,
                });
                continue;
            }
            if (field.Type == RoomFieldType.Bool)
            {
                var value = r.ReadBool32();
                // The first two bools sit at +4 and +5.
                w.WriteBool32(bools++ switch { 0 => applied.Flag4, 1 => applied.Flag5, _ => value });
                continue;
            }
            if (i < 2 && field.Type == RoomFieldType.U32)
            {
                r.ReadInt32();
                w.WriteInt32(i == 0 ? applied.Mode : applied.SubMode);
                continue;
            }
            ConsumeField(ref r, field);
            w.WriteBytes(gameSetting[start..r.Offset]);
        }
        if (r.Remaining > 0)
            throw new PacketFormatException($"GameSetting has {r.Remaining} trailing byte(s).", r.Offset);
        return buffer.WrittenSpan.ToArray();
    }

    private static void ConsumeField(ref PacketReader r, RoomField field)
    {
        switch (field.Type)
        {
            case RoomFieldType.U8:
                r.ReadByte();
                break;
            case RoomFieldType.U16:
                r.ReadUInt16();
                break;
            case RoomFieldType.U32:
                r.ReadInt32();
                break;
            case RoomFieldType.Bool:
                r.ReadBool32();
                break;
            case RoomFieldType.F32:
                r.ReadSingle();
                break;
            case RoomFieldType.F64:
                r.ReadDouble();
                break;
            case RoomFieldType.WStr:
                r.ReadWideStringPrefixed();
                break;
            case RoomFieldType.EmptyVector:
                if (r.ReadInt32() != 0)
                    throw new PacketFormatException("expected empty vector count 0.", r.Offset - 4);
                break;
            case RoomFieldType.Equipped:
                var n = r.ReadInt32();
                for (var i = 0; i < n; i++)
                    r.ReadInt32();
                break;
            default:
                throw new PacketFormatException($"CreateSession reader cannot consume field type {field.Type}.", r.Offset);
        }
    }
}
