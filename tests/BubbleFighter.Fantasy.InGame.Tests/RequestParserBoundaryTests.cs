using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Truncation and trailing-byte boundary pins for every public C2S request
/// parser in the InGame plugin. Each parser must throw
/// <see cref="PacketFormatException"/> for a body one byte short and for a body
/// carrying one trailing byte, so a client cursor desynchronization is a loud
/// error instead of a silent mis-parse. Bodies are built with the same
/// primitives the client builders use, so every length below is the client code
/// length, not a round number.
/// </summary>
public sealed class RequestParserBoundaryTests
{
    /// <summary>The client wstring read cap (<c>N &gt; 0x4000</c> is rejected).</summary>
    private const int ClientWideCharCap = 0x4000;

    /// <summary>The counted-vector cap inside MapMeta and the room blobs.</summary>
    private const int ClientVectorCountCap = 1024;

    private static byte[] OneShort(byte[] payload) => payload[..^1];

    private static byte[] OneLong(byte[] payload) => [.. payload, 0];

    [Fact]
    public void Int32PayloadPinsFourBytesAndRejectsBothBoundaries()
    {
        // C2S 571 (relay connect result) and C2S 607 (scene object ready) share
        // this parser; neither had any coverage before this audit.
        var payload = WireLengthSupport.Written(w => w.WriteInt32(-5));
        Assert.Equal(4, payload.Length);
        Assert.Equal(-5, Int32Payload.Parse(payload, "C2S 571").Value);
        Assert.Equal(-5, Int32Payload.Parse(payload, "C2S 607").Value);
        Assert.Throws<PacketFormatException>(() => Int32Payload.Parse(OneShort(payload), "C2S 571"));
        Assert.Throws<PacketFormatException>(() => Int32Payload.Parse(OneLong(payload), "C2S 571"));
        Assert.Throws<PacketFormatException>(() => Int32Payload.Parse([], "C2S 607"));
    }

    [Fact]
    public void JoinRoomRequestRejectsAShortBody()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt32(7));
        Assert.Throws<PacketFormatException>(() => JoinRoomRequest.Parse(OneShort(payload)));
    }

    /// <summary>
    /// Every parser whose body has a fixed end, with the canonical body the
    /// client builder writes. The two boundary tests below walk this table, so a
    /// parser added later is covered by adding one row here.
    /// </summary>
    private static (string Name, byte[] Body, Func<byte[], object> Parse)[] CanonicalBodies() =>
    [
        ("C2S 401", WireLengthSupport.Written(w =>
        {
            w.WriteUInt16(2);
            w.WriteUInt16(10);
        }), b => Unknown401Request.Parse(b)),

        // C2S 402 carries the optional i32 here so that dropping the last byte
        // still truncates a real value: a body that ends after the room name is
        // a legal shorter form (a live client sends it), so the one-byte-short
        // pin needs a body whose last byte is part of a value.
        ("C2S 402", WireLengthSupport.Written(w =>
        {
            RoomWireSupport.WaitingSetting().Write(w);
            w.WriteWideStringPrefixed("pw");
            w.WriteByte(WireShapes.OptionalPresent);
            w.WriteInt32(7);
        }), b => Unknown402Request.Parse(b)),

        ("C2S 403", WireLengthSupport.Written(w =>
        {
            w.WriteInt32(12);
            w.WriteWideStringPrefixed("x");
        }), b => Unknown403Request.Parse(b)),

        ("C2S 503", CreateRoomBody(), b => CreateRoomRequest.Parse(b)),
        ("C2S 107", QuickCreateBody(), b => QuickCreateRoomRequest.Parse(b)),

        ("C2S 517", WireLengthSupport.Written(w => w.WriteInt32(7)),
            b => JoinRoomRequest.Parse(b)),

        ("C2S 505", WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteInt32(0);
            w.WriteInt32(0);
        }), b => RoomStatusRequest.Parse(b)),

        ("C2S 502", WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteWideStringPrefixed("hi");
            w.WriteWideStringPrefixed(string.Empty);
        }), b => RoomChatRequest.Parse(b)),

        ("C2S 520", WireLengthSupport.Written(w =>
        {
            w.WriteInt32(101);
            w.WriteWideStringPrefixed("x");
            w.WriteInt32(2);
            w.WriteInt32(3);
        }), b => RoomChatTypedRequest.Parse(b)),

        ("C2S 511", WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteInt32(2);
            w.WriteInt32(3);
            w.WriteInt32(4);
            w.WriteByte(5);
            w.WriteByte(6);
            w.WriteInt32(7);
        }), b => RoomStateCommandRequest.Parse(b)),

        ("C2S 608", WireLengthSupport.Written(w =>
        {
            w.WriteByte(1);
            w.WriteInt32(10);
            w.WriteInt32(20);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteUInt16(0);
            w.WriteUInt16(0);
        }), b => GameplayAction.Parse(b)),

        ("C2S 2029", WireLengthSupport.Written(w =>
        {
            w.WriteByte(7);
            w.WriteInt32(3);
            w.WriteByte(1);
        }), b => AlternateAction.Parse(b)),

        ("C2S 578", WireLengthSupport.Written(w =>
        {
            w.WriteInt32(3000);
            w.WriteInt32(1);
            w.WriteInt32(0);
            w.WriteInt32(0);
        }), b => RelayEndpointRequest.Parse(b)),

        ("C2S 103", WireLengthSupport.Written(w =>
        {
            for (var i = 0; i < GameResultStats.Count; i++)
                w.WriteInt32(i);
        }), b => GameResultStats.Parse(b)),

        // Int32Payload is the shared C2S 571 / C2S 607 single-i32 shape; the
        // label below is the one the parser writes into its own message.
        ("C2S 571", WireLengthSupport.Written(w => w.WriteInt32(-5)),
            b => Int32Payload.Parse(b, "C2S 571")),

        ("relay C2S 1", WireLengthSupport.Written(w =>
        {
            new RelaySessionKey(1, 2, 3, 4, 5, 6).Write(w);
            w.WriteByte(0);
            w.WriteInt32(0);
            w.WriteInt32(9);
            w.WriteWideStringPrefixed("nick");
        }), b => RelayAuthenticate.Parse(b)),

        ("C2S 301/511", WireLengthSupport.Written(w =>
        {
            w.WriteInt32(7);
            w.WriteInt32(1);
            w.WriteInt32(2);
            w.WriteInt32(3);
            w.WriteByte(4);
            w.WriteByte(5);
            w.WriteInt32(6);
        }), b => CharacterPositionRequest.Parse(b)),
    ];

    [Fact]
    public void EveryParserRejectsABodyOneByteShort()
    {
        // The reader's own truncation message carries the byte count and the
        // read offset but not the opcode label (see the audit document), so the
        // type and the offset are what is asserted here.
        foreach (var (_, body, parse) in CanonicalBodies())
        {
            var exception = Assert.Throws<PacketFormatException>(() => parse(OneShort(body)));
            Assert.Contains("remain", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryParserRejectsOneTrailingByte()
    {
        // RequireConsumed names the codec, so the message is asserted to carry
        // the opcode label as well as the trailing-byte wording.
        foreach (var (name, body, parse) in CanonicalBodies())
        {
            var exception = Assert.Throws<PacketFormatException>(() => parse(OneLong(body)));
            Assert.Contains("trailing", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(name, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unknown403RequestRejectsATrailingByte()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(12);
            w.WriteWideStringPrefixed("x");
        });
        Assert.Equal(4 + 4 + 2, payload.Length);
        Assert.Throws<PacketFormatException>(() => Unknown403Request.Parse(OneLong(payload)));
    }

    [Fact]
    public void CreateRoomRequestRejectsATrailingByte()
    {
        var payload = CreateRoomBody();
        Assert.Throws<PacketFormatException>(() => CreateRoomRequest.Parse(OneLong(payload)));
    }

    [Fact]
    public void QuickCreateRoomRequestRejectsATrailingByte()
    {
        var payload = QuickCreateBody();
        Assert.Throws<PacketFormatException>(() => QuickCreateRoomRequest.Parse(OneLong(payload)));
    }

    [Fact]
    public void RoomChatRequestRejectsAShortBody()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteWideStringPrefixed("hi");
            w.WriteWideStringPrefixed(string.Empty);
        });
        Assert.Throws<PacketFormatException>(() => RoomChatRequest.Parse(OneShort(payload)));
    }

    [Fact]
    public void RoomChatTypedRequestRejectsATrailingByte()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(101);
            w.WriteWideStringPrefixed("x");
            w.WriteInt32(2);
            w.WriteInt32(3);
        });
        Assert.Equal(4 + 6 + 8, payload.Length);
        Assert.Throws<PacketFormatException>(() => RoomChatTypedRequest.Parse(OneLong(payload)));
    }

    [Fact]
    public void RoomStateCommandRequestRejectsATrailingByte()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteInt32(2);
            w.WriteInt32(3);
            w.WriteInt32(4);
            w.WriteByte(5);
            w.WriteByte(6);
            w.WriteInt32(7);
        });
        Assert.Equal(16 + 2 + 4, payload.Length);
        Assert.Throws<PacketFormatException>(() => RoomStateCommandRequest.Parse(OneLong(payload)));
    }

    [Fact]
    public void GameplayActionRejectsShortAndTrailingBodies()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteByte(1);
            w.WriteInt32(10);
            w.WriteInt32(20);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteUInt16(0);
            w.WriteUInt16(0);
        });
        Assert.Equal(16, payload.Length);
        Assert.Throws<PacketFormatException>(() => GameplayAction.Parse(OneShort(payload)));
        Assert.Throws<PacketFormatException>(() => GameplayAction.Parse(OneLong(payload)));
    }

    [Fact]
    public void GameplayActionParsesEveryFixedLayoutType()
    {
        // Every type the fixed family claims must produce a 16-byte body the
        // parser accepts, and every other byte must be refused. A regression in
        // either half of the table changes what the relay accepts.
        foreach (var type in GameplayAction.FixedLayoutTypes)
        {
            var payload = WireLengthSupport.Written(w =>
            {
                w.WriteByte(type);
                w.WriteInt32(1);
                w.WriteInt32(2);
                w.WriteByte(3);
                w.WriteByte(4);
                w.WriteByte(5);
                w.WriteUInt16(6);
                w.WriteUInt16(7);
            });

            Assert.Equal(16, payload.Length);
            var parsed = GameplayAction.Parse(payload);
            Assert.Equal(type, parsed.Type);
            Assert.Equal(1, parsed.Unknown1);
            Assert.Equal(2, parsed.Unknown2);
            Assert.Equal((byte)3, parsed.Unknown3);
            Assert.Equal((byte)4, parsed.Unknown4);
            Assert.Equal((byte)5, parsed.Unknown5);
            Assert.Equal((ushort)6, parsed.Unknown6);
            Assert.Equal((ushort)7, parsed.Unknown7);
        }

        Assert.False(GameplayAction.IsFixedLayout(0));
        Assert.False(GameplayAction.IsFixedLayout(21));
        Assert.False(GameplayAction.IsFixedLayout(255));
    }

    [Fact]
    public void AlternateActionRejectsShortAndTrailingBodies()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteByte(7);
            w.WriteInt32(3);
            w.WriteByte(1);
        });
        Assert.Equal(6, payload.Length);
        Assert.Throws<PacketFormatException>(() => AlternateAction.Parse(OneShort(payload)));
        Assert.Throws<PacketFormatException>(() => AlternateAction.Parse(OneLong(payload)));
    }

    [Fact]
    public void RelayEndpointRequestRejectsShortAndTrailingBodies()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(3000);
            w.WriteInt32(1);
            w.WriteInt32(0);
            w.WriteInt32(0);
        });
        Assert.Equal(16, payload.Length);
        Assert.Throws<PacketFormatException>(() => RelayEndpointRequest.Parse(OneShort(payload)));
        Assert.Throws<PacketFormatException>(() => RelayEndpointRequest.Parse(OneLong(payload)));
    }

    [Fact]
    public void GameResultStatsRejectsATrailingByte()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            for (var i = 0; i < GameResultStats.Count; i++)
                w.WriteInt32(i);
        });
        Assert.Equal(120, payload.Length);
        Assert.Throws<PacketFormatException>(() => GameResultStats.Parse(OneLong(payload)));
    }

    [Fact]
    public void RelayAuthenticateRejectsShortAndTrailingBodies()
    {
        var key = new RelaySessionKey(1, 2, 3, 4, 5, 6);
        var payload = WireLengthSupport.Written(w =>
        {
            key.Write(w);
            w.WriteByte(0);
            w.WriteInt32(0);
            w.WriteInt32(9);
            w.WriteWideStringPrefixed("nick");
        });
        Assert.Equal(20 + 1 + 8 + 4 + 8, payload.Length);
        Assert.Throws<PacketFormatException>(() => RelayAuthenticate.Parse(OneShort(payload)));
        Assert.Throws<PacketFormatException>(() => RelayAuthenticate.Parse(OneLong(payload)));
    }

    [Fact]
    public void RelayMotionRejectsASevenByteIdentity()
    {
        // The 8-byte identity prefix is the only proven part of the frame; the
        // remainder stays opaque, so a trailing byte is legal here by design.
        Assert.Throws<PacketFormatException>(() => RelayMotion.Parse(new byte[7]));
        Assert.Throws<PacketFormatException>(() => RelayMotion.Parse([]));
        var exact = RelayMotion.Parse(new byte[8]);
        Assert.Empty(exact.Remainder);
        var withRest = RelayMotion.Parse(new byte[13]);
        Assert.Equal(5, withRest.Remainder.Length);
    }

    [Fact]
    public void Unknown402RequestAcceptsABodyThatEndsAfterTheRoomName()
    {
        // A live client sends a 317-byte C2S 402 whose session blob and room
        // name end exactly at the tag's offset, so there is no tag byte. The
        // strict read closed the connection on that client (2026-09-21 live
        // session); the tag is optional on the wire, not mandatory.
        var payload = WireLengthSupport.Written(w =>
        {
            RoomWireSupport.WaitingSetting().Write(w);
            w.WriteWideStringPrefixed("pw");
        });

        var request = Unknown402Request.Parse(payload);

        Assert.Equal("pw", request.Password);
        Assert.Equal(WireShapes.OptionalAbsent402, request.OptTag);
        Assert.Null(request.UnknownOpt);
    }

    [Fact]
    public void Unknown402RequestRejectsAnUnknownOptionalTag()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            RoomWireSupport.WaitingSetting().Write(w);
            w.WriteWideStringPrefixed("pw");
            w.WriteByte(0x00);
        });

        // Only the two client tags are legal; anything else is a cursor
        // desynchronization and must not be parsed optimistically.
        Assert.Throws<PacketFormatException>(() => Unknown402Request.Parse(payload));
    }

    [Fact]
    public void Unknown402RequestRejectsATruncatedOptionalInt32()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            RoomWireSupport.WaitingSetting().Write(w);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteByte(WireShapes.OptionalPresent);
            w.WriteInt32(7);
        });

        Assert.Throws<PacketFormatException>(() => Unknown402Request.Parse(OneShort(payload)));
    }

    [Fact]
    public void MapMetaReadRejectsAnOversizedWstringCount()
    {
        // 34 bytes of the MapMeta prefix (i32 + u16 + seven i32) then a wstring
        // count past the client's 0x4000 read cap: the read must fail before it
        // allocates.
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteUInt16(0);
            for (var i = 0; i < 7; i++)
                w.WriteInt32(0);
            w.WriteUInt32((uint)ClientWideCharCap + 1);
        });

        Assert.Throws<PacketFormatException>(() => ReadMapMeta(payload));
    }

    [Fact]
    public void MapMetaReadRejectsAnOversizedVectorCount()
    {
        // The same prefix with all-empty wstrings, then a pair-vector count past
        // the 1024 cap that bounds every counted vector in the room blobs.
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteUInt16(0);
            for (var i = 0; i < 7; i++)
                w.WriteInt32(0);
            for (var i = 0; i < 4; i++)
                w.WriteWideStringPrefixed(string.Empty);
            w.WriteInt32(0);
            for (var i = 0; i < 6; i++)
                w.WriteWideStringPrefixed(string.Empty);
            for (var i = 0; i < 3; i++)
                w.WriteInt32(0);
            w.WriteUInt32((uint)ClientVectorCountCap + 1);
        });

        Assert.Throws<PacketFormatException>(() => ReadMapMeta(payload));
    }

    /// <summary>
    /// Reads one <c>MapMeta</c> from a payload. Kept out of the test lambdas
    /// because a <c>PacketReader</c> is a ref struct and cannot be captured.
    /// </summary>
    /// <param name="payload">The payload to read.</param>
    private static void ReadMapMeta(byte[] payload)
    {
        var r = new PacketReader(payload);
        _ = MapMetaBlob.Read(ref r);
    }

    private static byte[] CreateRoomBody()
    {
        return WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteInt32(0);
            for (var i = 0; i < 13; i++)
                w.WriteInt32(0);
            w.WriteByte(0);
            w.WriteInt32(0);
        });
    }

    private static byte[] QuickCreateBody()
    {
        return WireLengthSupport.Written(w =>
        {
            for (var i = 0; i < 13; i++)
                w.WriteInt32(i);
            w.WriteByte(0);
            w.WriteWideStringPrefixed("a");
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteInt32(0);
        });
    }
}
