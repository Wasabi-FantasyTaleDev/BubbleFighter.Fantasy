using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Wire;
using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Byte-length pins for the version/patch handshake codecs: C2S
/// <c>gcpReqGamePatch</c> (builder <c>sub_99DD00</c>) and S2C
/// <c>gspRepGamePatch</c> (label <c>0x15B699C</c>, handler <c>sub_9A1250</c>).
/// </summary>
public sealed class PatchHandshakeCodecTests
{
    [Fact]
    public void GamePatchFramePins104PlaintextBytesAndDocumentedHeader()
    {
        var payload = WirePins.GamePatchPayload();
        Assert.Equal(WirePins.GamePatchPayloadSize, payload.Length);
        Assert.Equal(1256, BinaryPrimitives.ReadInt32LittleEndian(payload));

        var frame = WireFrame.Encode(WireOpcodes.C2S.GamePatch, payload);
        Assert.Equal(WirePins.GamePatchFrameSize, frame.Length);
        Assert.Equal(WirePins.GamePatchPlainHeaderHex, Convert.ToHexString(frame[..WireFrame.HeaderSize]));
        Assert.Equal(payload, frame[WireFrame.HeaderSize..]);
    }

    [Fact]
    public void GamePatchFrameEncryptsToPinnedC2SCiphertext()
    {
        var frame = WireFrame.Encode(WireOpcodes.C2S.GamePatch, WirePins.GamePatchPayload());
        Assert.Equal(WirePins.GamePatchFrameSize, frame.Length);
        SimpleCipher.ClientToServer().EncryptInPlace(frame);
        Assert.Equal(WirePins.GamePatchC2SFrameCipherHex, Convert.ToHexString(frame));
        Assert.Equal(WirePins.GamePatchC2SHeaderCipherHex, Convert.ToHexString(frame[..WireFrame.HeaderSize]));
    }

    [Fact]
    public void GamePatchCiphertextDecryptsToPinnedPlaintextFrame()
    {
        var wire = Convert.FromHexString(WirePins.GamePatchC2SFrameCipherHex);
        Assert.Equal(WirePins.GamePatchFrameSize, wire.Length);
        SimpleCipher.ClientToServer().DecryptInPlace(wire);

        Assert.True(WireFrame.TryReadHeader(wire, out var header));
        Assert.Equal(WirePins.GamePatchPayloadSize, header.PayloadLength);
        Assert.Equal(WireOpcodes.C2S.GamePatch, header.Opcode);
        Assert.True(WireFrame.ValidatePayload(header, wire.AsSpan(WireFrame.HeaderSize)));
        Assert.Equal(1256, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(WireFrame.HeaderSize)));
    }

    [Fact]
    public void RepGamePatchEmptyReplyPins16PlaintextBytes()
    {
        var buffer = new ArrayBufferWriter<byte>(WirePins.RepGamePatchPayloadSize);
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        writer.WriteWideStringPrefixed("");
        Assert.Equal(WirePins.RepGamePatchPayloadSize, buffer.WrittenCount);

        var frame = WireFrame.Encode(WireOpcodes.S2C.RepGamePatch, buffer.WrittenSpan);
        Assert.Equal(WirePins.RepGamePatchFrameSize, frame.Length);
        Assert.Equal(WirePins.RepGamePatchPlainFrameHex, Convert.ToHexString(frame));
    }

    [Fact]
    public void RepGamePatchEmptyReplyEncryptsToPinnedS2CCiphertext()
    {
        var frame = Convert.FromHexString(WirePins.RepGamePatchPlainFrameHex);
        Assert.Equal(WirePins.RepGamePatchFrameSize, frame.Length);
        SimpleCipher.ServerToClient().EncryptInPlace(frame);
        Assert.Equal(WirePins.RepGamePatchS2CFrameCipherHex, Convert.ToHexString(frame));
    }

    [Fact]
    public void OpcodeTableNotesPinHandshakeLayouts()
    {
        var table = OpcodeTableLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"),
            TestWireDialects.Get("kr-1256").Profile);

        Assert.True(table.TryGet(PacketDirection.ClientToServer, WireOpcodes.C2S.GamePatch, out var request));
        Assert.Equal("gcpReqGamePatch", request!.Name);
        Assert.Equal(Evidence.Verified, request.Evidence);
        Assert.Equal("sub_99DD00", request.Source);
        Assert.NotNull(request.Source);
        Assert.NotNull(request.Notes);
        Assert.Contains("96-byte", request.Notes, StringComparison.Ordinal);

        Assert.True(table.TryGet(PacketDirection.ServerToClient, WireOpcodes.S2C.RepGamePatch, out var reply));
        Assert.Equal("gspRepGamePatch", reply!.Name);
        Assert.Equal(Evidence.Verified, reply.Evidence);
        Assert.NotNull(reply.Source);
        Assert.Contains("0x15B699C", reply.Source, StringComparison.Ordinal);
        Assert.Contains("sub_9A1250", reply.Source, StringComparison.Ordinal);
        Assert.NotNull(reply.Notes);
        Assert.Contains("i32 requiresPatch", reply.Notes, StringComparison.Ordinal);
    }
}
