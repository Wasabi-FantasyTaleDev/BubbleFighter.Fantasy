using BubbleFighter.Fantasy.Wire;
using System.Reflection;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Pins <see cref="SimpleCipher"/> tables, seeds (<c>sub_10B9720</c>), and the documented
/// 8-byte C2S vector from <c>sub_7B4DE0</c>/<c>sub_7B4E50</c>.
/// </summary>
public sealed class SimpleCipherTests
{
    [Fact]
    public void TablesMatchDocumentedHexDumps()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var tableA = (byte[])typeof(SimpleCipher).GetField("TableA", flags)!.GetValue(null)!;
        var tableB = (byte[])typeof(SimpleCipher).GetField("TableB", flags)!.GetValue(null)!;

        Assert.Equal(WirePins.TableAHex, Convert.ToHexString(tableA));
        Assert.Equal(WirePins.TableBHex, Convert.ToHexString(tableB));
        Assert.Equal(WirePins.TableAHex.Length / 2, tableA.Length);
        Assert.Equal(24, tableB.Length);
        // The protocol note labels table A as 48 bytes; the shipped hex dump is 49.
        Assert.Equal(49, tableA.Length);
    }

    [Fact]
    public void ClientToServerSeedDecryptsKnownVector()
    {
        // Reference wire bytes decrypt to a frame header: length 96, opcode 1, integrity 0x8DEC.
        var cipher = SimpleCipher.ClientToServer();
        var bytes = Convert.FromHexString(WirePins.GamePatchC2SHeaderCipherHex);
        Assert.Equal(WirePins.FrameHeaderSize, bytes.Length);
        cipher.DecryptInPlace(bytes);
        Assert.Equal(WirePins.GamePatchPlainHeaderHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public void ClientToServerSeedEncryptsKnownVector()
    {
        var cipher = SimpleCipher.ClientToServer();
        var bytes = Convert.FromHexString(WirePins.GamePatchPlainHeaderHex);
        Assert.Equal(WirePins.FrameHeaderSize, bytes.Length);
        cipher.EncryptInPlace(bytes);
        Assert.Equal(WirePins.GamePatchC2SHeaderCipherHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public void ServerToClientSeedDecryptsKnownVector()
    {
        // Cross-check the (5,3) seed produces deterministic, direction-specific output.
        var cipher = SimpleCipher.ServerToClient();
        var bytes = Convert.FromHexString(WirePins.GamePatchC2SHeaderCipherHex);
        cipher.DecryptInPlace(bytes);
        Assert.Equal("A7F7ABC794FDE49F", Convert.ToHexString(bytes));
    }

    [Fact]
    public void EncryptDecryptRoundTripsWithContinuousState()
    {
        var plain = new byte[257];
        new Random(1256).NextBytes(plain);

        var encryptor = SimpleCipher.ClientToServer();
        var wire = (byte[])plain.Clone();
        // Encrypt in two chunks to prove state is continuous across calls.
        encryptor.EncryptInPlace(wire.AsSpan(0, 100));
        encryptor.EncryptInPlace(wire.AsSpan(100));

        Assert.NotEqual(plain, wire);

        var decryptor = SimpleCipher.ClientToServer();
        var recovered = (byte[])wire.Clone();
        decryptor.DecryptInPlace(recovered.AsSpan(0, 100));
        decryptor.DecryptInPlace(recovered.AsSpan(100));
        Assert.Equal(plain, recovered);
    }

    [Fact]
    public void SameSeedProducesSameStream()
    {
        var a = SimpleCipher.ServerToClient();
        var b = new SimpleCipher(5, 3);
        var data = new byte[64];
        var copy = (byte[])data.Clone();
        a.EncryptInPlace(data);
        b.EncryptInPlace(copy);
        Assert.Equal(data, copy);
    }

    [Fact]
    public void DirectionsProduceDifferentStreams()
    {
        var plain = Convert.FromHexString(WirePins.GamePatchPlainHeaderHex);
        var c2s = (byte[])plain.Clone();
        var s2c = (byte[])plain.Clone();
        SimpleCipher.ClientToServer().EncryptInPlace(c2s);
        SimpleCipher.ServerToClient().EncryptInPlace(s2c);
        Assert.Equal(plain.Length, c2s.Length);
        Assert.NotEqual(c2s, s2c);
    }

    [Fact]
    public void SingleByteEncryptDecryptRoundTrips()
    {
        var encryptor = SimpleCipher.ClientToServer();
        var decryptor = SimpleCipher.ClientToServer();
        Assert.Equal((byte)0x12, decryptor.Decrypt(encryptor.Encrypt(0x12)));
    }

    [Fact]
    public void EmptySpanDoesNotAdvanceOrThrow()
    {
        var cipher = SimpleCipher.ClientToServer();
        cipher.EncryptInPlace([]);
        var header = Convert.FromHexString(WirePins.GamePatchPlainHeaderHex);
        cipher.EncryptInPlace(header);
        Assert.Equal(WirePins.GamePatchC2SHeaderCipherHex, Convert.ToHexString(header));
    }

    [Fact]
    public void ZeroBuffersPinFirstSixteenCipherBytes()
    {
        var c2s = new byte[16];
        var s2c = new byte[16];
        SimpleCipher.ClientToServer().EncryptInPlace(c2s);
        SimpleCipher.ServerToClient().EncryptInPlace(s2c);
        Assert.Equal(16, c2s.Length);
        Assert.Equal(WirePins.ClientToServerZeros16Hex, Convert.ToHexString(c2s));
        Assert.Equal(WirePins.ServerToClientZeros16Hex, Convert.ToHexString(s2c));
    }
}
