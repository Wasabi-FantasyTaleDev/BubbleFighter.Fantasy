using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>What a <see cref="WireStreamDecoder"/> produced from the bytes it was fed.</summary>
public abstract record WireStreamItem
{
    /// <summary>The plaintext preamble a side wrote before its cipher started.</summary>
    /// <param name="Bytes">The preamble bytes.</param>
    public sealed record Preamble(byte[] Bytes) : WireStreamItem;

    /// <summary>One decoded frame.</summary>
    /// <param name="Value">The frame.</param>
    public sealed record Frame(DecodedFrame Value) : WireStreamItem;
}

/// <summary>
/// Decodes one direction of a connection from raw socket bytes, fed in whatever chunks
/// they arrived in: the dialect's plaintext preamble first, then deciphered frames. The
/// buffered remainder is bounded by the largest frame the dialect accepts.
/// </summary>
public sealed class WireStreamDecoder
{
    private readonly IWireDialect _dialect;
    private readonly IStreamTransform _decryptor;
    private readonly int _maxPayloadBytes;
    private readonly int _maxBuffered;
    private readonly byte[] _preamble;
    private int _preambleFilled;
    private byte[] _buffer;
    private int _buffered;

    /// <summary>Creates a decoder positioned at the start of a connection.</summary>
    /// <param name="dialect">The dialect the connection speaks.</param>
    /// <param name="direction">The direction of the bytes this decoder is fed.</param>
    /// <param name="maxPayloadBytes">The largest payload accepted before the stream is rejected.</param>
    public WireStreamDecoder(IWireDialect dialect, PacketDirection direction, int maxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        _dialect = dialect;
        _decryptor = dialect.CreateDecryptor(direction);
        _maxPayloadBytes = maxPayloadBytes;
        if (dialect.MaxFrameOverheadBytes is < 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(dialect), "Frame overhead must be in 0..65536 bytes.");
        _maxBuffered = checked(maxPayloadBytes + dialect.MaxFrameOverheadBytes);
        var prefixLength = dialect.PreambleLength(direction);
        if (prefixLength is < 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(dialect), "Preamble length must be in 0..65536 bytes.");
        _preamble = new byte[prefixLength];
        _buffer = new byte[Math.Min(4096, _maxBuffered)];
    }

    /// <summary>Feeds raw bytes and returns every item they completed, in stream order.</summary>
    /// <param name="raw">The next raw bytes of this direction.</param>
    /// <returns>The completed items; empty when the bytes only extended a partial frame.</returns>
    /// <exception cref="PacketFormatException">Thrown when a frame fails the dialect's checks;
    /// the decoder is unusable afterwards.</exception>
    public IReadOnlyList<WireStreamItem> Feed(ReadOnlySpan<byte> raw)
    {
        var items = new List<WireStreamItem>();
        if (_preambleFilled < _preamble.Length)
        {
            var take = Math.Min(raw.Length, _preamble.Length - _preambleFilled);
            raw[..take].CopyTo(_preamble.AsSpan(_preambleFilled));
            _preambleFilled += take;
            raw = raw[take..];
            if (_preambleFilled == _preamble.Length)
                items.Add(new WireStreamItem.Preamble(_preamble.ToArray()));
        }
        if (raw.IsEmpty)
            return items;

        while (!raw.IsEmpty)
        {
            var take = Math.Min(raw.Length, _maxBuffered - _buffered);
            if (take == 0)
                throw new PacketFormatException("Incomplete frame exhausted the dialect's maximum frame size", _buffered);
            Append(raw[..take]);
            raw = raw[take..];
            var sequence = new ReadOnlySequence<byte>(_buffer, 0, _buffered);
            while (true)
            {
                var previous = sequence.Length;
                if (!_dialect.TryReadFrame(ref sequence, _maxPayloadBytes, out var frame)) break;
                if (sequence.Length >= previous)
                    throw new PacketFormatException("Dialect returned a frame without consuming input", _buffered);
                items.Add(new WireStreamItem.Frame(frame));
            }
            var consumed = _buffered - (int)sequence.Length;
            if (consumed > 0)
            {
                _buffer.AsSpan(consumed, _buffered - consumed).CopyTo(_buffer);
                _buffered -= consumed;
            }
        }
        return items;
    }

    /// <summary>Gets the number of decrypted bytes held that do not yet form a whole frame.</summary>
    public int PendingBytes => _buffered;

    private void Append(ReadOnlySpan<byte> raw)
    {
        if (_buffered + raw.Length > _maxBuffered)
            throw new PacketFormatException(
                $"stream holds {_buffered + raw.Length} undecoded bytes, more than one maximum frame", _buffered);
        if (_buffered + raw.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Min(_maxBuffered, Math.Max(_buffer.Length * 2, _buffered + raw.Length)));
        var target = _buffer.AsSpan(_buffered, raw.Length);
        raw.CopyTo(target);
        _decryptor.Apply(target);
        _buffered += raw.Length;
    }
}
