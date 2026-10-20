using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages;

/// <summary>KR 1256 match handshake codecs derived from 0x95FD30, 0x9601E0 and 0x10F32C0.</summary>
public static class ModernMatchCodec
{
    /// <summary>
    /// Writes S2C game 521: u16 mode, u8 unknown8 (zero), u32 blobCount (zero), no blob.
    /// Reader 0x1155B00 stores the mode at object+4; 0x9616A9 requires it to equal the room mode.
    /// </summary>
    public static byte[] WriteSessionSignature(ushort gameMode)
    {
        var payload = new byte[7];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload, gameMode);
        return payload;
    }

    /// <summary>Reads C2S 1: 20-byte key, u8 unknown20, i32 unknown21, i32 account, wstring nickname (0x95FD30).</summary>
    public static MatchAuthenticate ReadAuthentication(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var ticket = r.ReadBytes(20).ToArray();
        r.ReadByte();
        r.ReadInt32();
        var account = r.ReadInt32();
        var nickname = r.ReadWideStringPrefixed();
        End(r);
        return new(ticket, account, nickname);
    }

    /// <summary>
    /// Reads C2S 12 (0x10F32C0): account/entity, nickname, unknown32, equipment (0x10C4510),
    /// profile vectors (0x10D7D70), wstring unknown128, u8 unknown152, three u16,
    /// session signature (0x1155AB0), bool32 unknown188. Unused fields stay inside the dialect.
    /// </summary>
    public static MatchSceneRegistered ReadScene(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var account = r.ReadInt32();
        var entity = r.ReadInt32();
        var nickname = r.ReadWideStringPrefixed();
        r.ReadInt32();
        r.ReadInt32();
        SkipVector(ref r, 4);
        SkipVector(ref r, 4);
        SkipVector(ref r, 4);
        SkipVector(ref r, 1);
        var count = Count(ref r, 20);
        for (var i = 0; i < count; i++)
        {
            r.ReadInt32();
            r.ReadInt32();
            SkipVector(ref r, 4);
            r.ReadInt32();
            r.ReadWideStringPrefixed();
        }
        r.ReadInt32();
        SkipVector(ref r, 12);
        SkipVector(ref r, 1);
        r.ReadWideStringPrefixed();
        r.ReadByte();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadByte();
        SkipVector(ref r, 1);
        r.ReadInt32();
        End(r);
        return new(account, entity, nickname);
    }

    /// <summary>Reads C2S 3 from 0x9F02F0: version, flag, map, entity, nickname, scene id, mode.</summary>
    public static MatchMapNamed ReadMap(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        if (r.ReadInt32() != 1256) throw new PacketFormatException("Unsupported match reconnect version.", 0);
        r.ReadInt32();
        var map = r.ReadWideStringPrefixed();
        var entity = r.ReadInt32();
        var nickname = r.ReadWideStringPrefixed();
        r.ReadInt32();
        r.ReadInt32();
        End(r);
        return new(entity, nickname, map);
    }

    /// <summary>
    /// Sends S2C 22 then 1 after authentication.
    /// 0x961155 reads bool32 and six bytes; false disables its optional adaptive sampler without a divisor.
    /// The S2C 1 dispatcher at 0x96026F starts C2S 12; its result body is four zero bytes.
    /// </summary>
    public static async ValueTask SendAuthenticationAsync(ISession session, CancellationToken ct)
    {
        await session.SendAsync(22, new byte[10], ct).ConfigureAwait(false);
        await session.SendAsync(1, new byte[4], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes a new entity's tables: S2C 4 entity + two counted u32 vectors (0x6CD590),
    /// S2C 5 entity + counted bytes (0x6CD810), S2C 6 entity + counted u32 vector (0x6CDA20).
    /// Consumers 0x4832D0/0x483420/0x483540 reset 32-word bitsets and 1000-byte per-entity state.
    /// S2C 13 has no body and reports success to the game connection (0x960880).
    /// </summary>
    public static async ValueTask SendSceneAsync(ISession session, int entity, CancellationToken ct)
    {
        await session.SendAsync(4, EntityTables(entity, 32, 4, true), ct).ConfigureAwait(false);
        await session.SendAsync(5, EntityTables(entity, 1000, 1, false), ct).ConfigureAwait(false);
        await session.SendAsync(6, EntityTables(entity, 32, 4, false), ct).ConfigureAwait(false);
        await session.SendAsync(13, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    private static byte[] EntityTables(int entity, int count, int width, bool pair)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(entity);
        w.WriteInt32(count);
        w.WriteBytes(new byte[count * width]);
        if (pair)
        {
            w.WriteInt32(count);
            w.WriteBytes(new byte[count * width]);
        }
        return ((System.Buffers.ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    private static int Count(ref PacketReader r, int minimumWidth)
    {
        var count = r.ReadUInt32();
        if (count > r.Remaining / minimumWidth)
            throw new PacketFormatException("Match vector exceeds the remaining payload.", 0);
        return checked((int)count);
    }

    private static void SkipVector(ref PacketReader r, int width) => r.ReadBytes(Count(ref r, width) * width);
    private static void End(PacketReader r)
    {
        if (r.Remaining != 0) throw new PacketFormatException("Trailing match handshake bytes.", 0);
    }
}

/// <summary>Provides typed match bindings without allowing the business plugin to depend on opcode numbers.</summary>
internal sealed class ModernMatchBinding(string protocol) : IInboundMessageBinding<MatchAuthenticate>,
    IInboundMessageBinding<MatchSceneRegistered>, IInboundMessageBinding<MatchMapNamed>,
    IMessageSender<MatchAuthenticationAccepted>, IMessageSender<MatchSceneAccepted>
{
    public string Protocol => protocol;
    public ValueTask SendAsync(ISession session, MatchAuthenticationAccepted message, CancellationToken ct) =>
        ModernMatchCodec.SendAuthenticationAsync(session, ct);
    public ValueTask SendAsync(ISession session, MatchSceneAccepted message, CancellationToken ct) =>
        ModernMatchCodec.SendSceneAsync(session, message.EntityId, ct);
}
