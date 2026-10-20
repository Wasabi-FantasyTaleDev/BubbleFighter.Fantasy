namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>Thrown when a packet payload cannot be decoded.</summary>
public sealed class PacketFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description of the malformed field.</param>
    /// <param name="offset">The byte offset within the payload where decoding failed.</param>
    public PacketFormatException(string message, int offset)
        : base($"Malformed packet at offset {offset}: {message}") => Offset = offset;

    /// <summary>Gets the byte offset within the payload where decoding failed.</summary>
    public int Offset { get; }
}
