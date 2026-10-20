using System.Buffers;

namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>A per-connection, per-direction stateful byte transform.</summary>
public interface IStreamTransform
{
    /// <summary>Transforms bytes in place and advances this stream's state.</summary>
    /// <param name="data">The next contiguous bytes in stream order.</param>
    void Apply(Span<byte> data);
}

/// <summary>A frame detached from its input buffer, with an owned payload.</summary>
/// <param name="Opcode">The opcode in this connection's direction.</param>
/// <param name="Payload">The decoded payload bytes.</param>
public readonly record struct DecodedFrame(ushort Opcode, byte[] Payload);

/// <summary>Build-specific stream operations shared by server plugins and offline tools.</summary>
public interface IWireDialect
{
    /// <summary>Gets the unique deployment identifier, equal to <see cref="Profile"/>.Id.</summary>
    string Id { get; }
    /// <summary>Gets the client identity served by this dialect.</summary>
    ClientProfile Profile { get; }
    /// <summary>Gets a human-readable description of the stream format.</summary>
    string Description { get; }
    /// <summary>Gets the maximum frame bytes beyond the configured payload limit.</summary>
    int MaxFrameOverheadBytes { get; }
    /// <summary>Gets the plaintext prefix length before encryption begins.</summary>
    /// <param name="direction">The stream direction.</param>
    /// <returns>The prefix byte count.</returns>
    int PreambleLength(PacketDirection direction);
    /// <summary>Builds this client's plaintext server greeting, validating all supplied values.</summary>
    /// <param name="status">The configured bootstrap status.</param>
    /// <param name="commandLine">The configured bootstrap command line.</param>
    /// <returns>The complete plaintext prefix.</returns>
    byte[] BuildServerPreamble(ushort status, string commandLine);
    /// <summary>Creates independent receive state at the start of a direction.</summary>
    /// <param name="direction">The stream direction.</param>
    /// <returns>A fresh decryptor.</returns>
    IStreamTransform CreateDecryptor(PacketDirection direction);
    /// <summary>Creates independent send state at the start of a direction.</summary>
    /// <param name="direction">The stream direction.</param>
    /// <returns>A fresh encryptor.</returns>
    IStreamTransform CreateEncryptor(PacketDirection direction);
    /// <summary>Consumes exactly one complete frame, or leaves the input unchanged if incomplete.</summary>
    /// <param name="plaintext">The decrypted input, advanced only on success.</param>
    /// <param name="maxPayloadBytes">The deployment's payload bound.</param>
    /// <param name="frame">The detached frame on success.</param>
    /// <returns>Whether a complete frame was consumed.</returns>
    /// <exception cref="PacketFormatException">Integrity or length validation failed.</exception>
    bool TryReadFrame(ref ReadOnlySequence<byte> plaintext, int maxPayloadBytes, out DecodedFrame frame);
    /// <summary>Encodes a plaintext frame including its integrity bytes.</summary>
    /// <param name="opcode">The opcode to encode.</param>
    /// <param name="payload">The unframed payload.</param>
    /// <returns>The complete plaintext frame.</returns>
    byte[] EncodeFrame(ushort opcode, ReadOnlySpan<byte> payload);
}

/// <summary>Exports stateless dialects without requiring a running plugin host.</summary>
public interface IWireDialectProvider
{
    /// <summary>Gets the fixed dialect set supplied by this provider; each transform is created per connection.</summary>
    IReadOnlyList<IWireDialect> Dialects { get; }
}

/// <summary>Deployment-scoped dialect registrations; absent or ambiguous builds fail explicitly.</summary>
public interface IWireDialectRegistry
{
    /// <summary>Registers a dialect; duplicate identifiers and build identities are rejected.</summary>
    /// <param name="dialect">The stateless dialect implementation.</param>
    /// <returns>An idempotent handle owning only this registration.</returns>
    IDisposable Register(IWireDialect dialect);
    /// <summary>Gets a snapshot ordered by dialect identifier.</summary>
    IReadOnlyList<IWireDialect> All { get; }
    /// <summary>Resolves an explicit dialect identifier, with no built-in fallback.</summary>
    /// <param name="id">The deployed identifier.</param>
    /// <returns>The registered dialect.</returns>
    IWireDialect Get(string id);
    /// <summary>Resolves the exact region, executable major and resource identity declared by the profile.</summary>
    /// <param name="build">The configured client build.</param>
    /// <returns>The unique dialect serving this build.</returns>
    IWireDialect Resolve(ClientBuild build);
}
