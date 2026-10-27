namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// The additive stream cipher applied to the whole byte stream after the bootstrap
/// greeting (encryption <c>kr-1256 sub_7B4DE0</c>, decryption <c>kr-1256 sub_7B4E50</c>:
/// <c>*out++ = *in++ ± A[ia] + B[ib]</c> with the table indices advancing modulo their
/// lengths). Two byte tables drive it: per byte <c>delta = A[ia] + B[ib]</c>, encryption adds and
/// decryption subtracts <c>delta</c> modulo 256, then both indices advance modulo their table
/// lengths. State is continuous across frames. The table bytes are build data: the
/// kr-1256 pair below is not present in every binary, so a dialect whose client ships
/// other tables passes them to the table constructor.
/// </summary>
public sealed class SimpleCipher
{
    private static readonly byte[] TableA = Convert.FromHexString(
        "5F3E3272C11846875D2E294E6879866CB58D56F138C148425F0CCD80E8E2FEF0425FF7597E5FA58D3D76E711416267597F");

    private static readonly byte[] TableB = Convert.FromHexString(
        "230024002500730065007200740033003400250077006500");

    private readonly byte[] _tableA;
    private readonly byte[] _tableB;
    private int _indexA;
    private int _indexB;

    /// <summary>Creates a cipher with explicit table indices.</summary>
    /// <param name="seedA">The initial index into table A.</param>
    /// <param name="seedB">The initial index into table B.</param>
    public SimpleCipher(int seedA, int seedB)
        : this(TableA, TableB, seedA, seedB)
    {
    }

    /// <summary>Creates a cipher over build-specific tables with explicit table indices.</summary>
    /// <param name="tableA">The delta table A of this build (49 bytes in kr-1256 and cn-324).</param>
    /// <param name="tableB">The delta table B of this build (24 bytes in kr-1256 and cn-324).</param>
    /// <param name="seedA">The initial index into table A.</param>
    /// <param name="seedB">The initial index into table B.</param>
    /// <exception cref="ArgumentException">Thrown when a table is empty or a seed is outside its table.</exception>
    public SimpleCipher(ReadOnlySpan<byte> tableA, ReadOnlySpan<byte> tableB, int seedA, int seedB)
    {
        if (tableA.IsEmpty || tableB.IsEmpty)
            throw new ArgumentException("SimpleCipher tables must not be empty.");
        if ((uint)seedA >= (uint)tableA.Length || (uint)seedB >= (uint)tableB.Length)
            throw new ArgumentException($"SimpleCipher seeds ({seedA}, {seedB}) fall outside tables of {tableA.Length} and {tableB.Length} bytes.");
        _tableA = tableA.ToArray();
        _tableB = tableB.ToArray();
        _indexA = seedA;
        _indexB = seedB;
    }

    /// <summary>
    /// Creates a cipher for the client-to-server direction. The client's connection constructor
    /// (<c>kr-1256 sub_10B9720</c>) seeds it (0, 1) and stores the cipher object at connection
    /// offset 2460; the send path applies that object's vtable slot +8, the add variant
    /// <c>kr-1256 sub_7B4DE0</c>.
    /// </summary>
    public static SimpleCipher ClientToServer() => new(0, 1);

    /// <summary>
    /// Creates a cipher for the server-to-client direction. The same constructor
    /// (<c>kr-1256 sub_10B9720</c>) seeds it (5, 3) and stores the second cipher at offset 2472;
    /// the receive path applies its vtable slot +12, the subtract variant
    /// <c>kr-1256 sub_7B4E50</c>.
    /// </summary>
    public static SimpleCipher ServerToClient() => new(5, 3);

    /// <summary>Encrypts one byte and advances the cipher state.</summary>
    /// <param name="b">The plaintext byte.</param>
    /// <returns>The ciphertext byte.</returns>
    public byte Encrypt(byte b) => (byte)(b + Delta());

    /// <summary>Decrypts one byte and advances the cipher state.</summary>
    /// <param name="b">The ciphertext byte.</param>
    /// <returns>The plaintext byte.</returns>
    public byte Decrypt(byte b) => (byte)(b - Delta());

    /// <summary>Encrypts a buffer in place, advancing the cipher state.</summary>
    /// <param name="data">The plaintext bytes.</param>
    public void EncryptInPlace(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
            data[i] = Encrypt(data[i]);
    }

    /// <summary>Decrypts a buffer in place, advancing the cipher state.</summary>
    /// <param name="data">The ciphertext bytes.</param>
    public void DecryptInPlace(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
            data[i] = Decrypt(data[i]);
    }

    private int Delta()
    {
        var delta = _tableA[_indexA] + _tableB[_indexB];
        _indexA = (_indexA + 1) % _tableA.Length;
        _indexB = (_indexB + 1) % _tableB.Length;
        return delta;
    }
}
