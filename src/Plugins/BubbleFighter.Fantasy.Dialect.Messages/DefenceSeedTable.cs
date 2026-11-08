using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The integer reduction percents one actor's local defence roll can produce. The client truncates
/// <c>min * 100</c> and <c>max * 100</c> and draws <c>low + (int)(u * (high - low))</c> for u in [0, 1)
/// (kr-812 sub_557230 with kr-812 sub_A59D60), so the percents are every integer from the low endpoint toward the
/// high one, the high endpoint excluded unless both are equal. Finite native RNG output is approximated uniformly.
/// </summary>
/// <param name="Low">The truncated minimum percent.</param>
/// <param name="High">The truncated maximum percent.</param>
internal readonly record struct DefencePercentRange(int Low, int High)
{
    /// <summary>Gets the smallest percent the roll can return.</summary>
    internal int First => High >= Low ? Low : High + 1;

    /// <summary>Gets how many consecutive percents the roll returns with equal probability.</summary>
    internal int Count => High == Low ? 1 : Math.Abs(High - Low);
}

/// <summary>
/// Builds one actor's 1000 ordinary-defence decisions. The table branch scales a byte by the same constant the local
/// branch scales its integer roll by (kr-812 sub_557230, kr-812 0xD98108), so a byte equal to a local roll yields the identical
/// reduction. The bytes are stratified over the local roll's percents and then shuffled: every percent occurs
/// floor or ceil of 1000/count times. This approximates the local roll's distribution; it does not reproduce its RNG.
/// </summary>
internal static class DefenceSeedTable
{
    /// <summary>The decisions in one table; the consumer wraps its cursor at 1000 (kr-812 sub_557230).</summary>
    internal const int Length = 1000;

    /// <summary>Builds the table of one actor from a range and a per-actor seed.</summary>
    /// <param name="range">The local roll's percents.</param>
    /// <param name="seed">The bytes that select the stratification offset and the order.</param>
    /// <returns>The 1000 bytes in native order.</returns>
    /// <exception cref="InvalidOperationException">A percent does not fit the native byte.</exception>
    internal static byte[] Build(DefencePercentRange range, ReadOnlySpan<byte> seed)
    {
        if (range.First < 0 || range.First + range.Count - 1 > byte.MaxValue)
            throw new InvalidOperationException(
                $"Defence percents {range.Low}..{range.High} do not fit the native one-byte table.");
        var random = new Xoshiro(seed);
        // Decision i covers the slice [(i + u) / 1000, (i + 1 + u) / 1000) of the range; one shared offset u keeps each
        // percent's expected share exactly 1/count when 1000 is not a multiple of the count.
        var offset = random.Next() >> 32;
        var table = new byte[Length];
        for (var i = 0; i < Length; i++)
            table[i] = (byte)(range.First + (int)(((((ulong)i << 32) + offset) * (ulong)range.Count) / ((ulong)Length << 32)));
        for (var i = Length - 1; i > 0; i--)
        {
            var j = (int)random.Below((ulong)i + 1);
            (table[i], table[j]) = (table[j], table[i]);
        }
        return table;
    }

    // xoshiro256** keyed by a SHA-256 of the seed; the same seed always gives the same table.
    private struct Xoshiro
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;

        internal Xoshiro(ReadOnlySpan<byte> seed)
        {
            Span<byte> state = stackalloc byte[32];
            SHA256.HashData(seed, state);
            _s0 = BinaryPrimitives.ReadUInt64LittleEndian(state);
            _s1 = BinaryPrimitives.ReadUInt64LittleEndian(state[8..]);
            _s2 = BinaryPrimitives.ReadUInt64LittleEndian(state[16..]);
            _s3 = BinaryPrimitives.ReadUInt64LittleEndian(state[24..]);
            if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 1;
        }

        internal ulong Next()
        {
            var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            var shifted = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= shifted;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            return result;
        }

        // An unbiased integer below the bound (multiply-and-reject).
        internal ulong Below(ulong bound)
        {
            var high = Math.BigMul(Next(), bound, out var low);
            if (low < bound)
            {
                var threshold = (0 - bound) % bound;
                while (low < threshold)
                    high = Math.BigMul(Next(), bound, out low);
            }
            return high;
        }
    }
}
