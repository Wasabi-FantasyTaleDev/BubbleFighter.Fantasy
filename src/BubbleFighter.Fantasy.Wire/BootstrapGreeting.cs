using System.Buffers.Binary;
using System.Text;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// The fixed-size message the server sends immediately after a connection is accepted,
/// consumed by the client's <c>ServerConnection::OnFirstConnected</c> (<c>sub_10BB110 -&gt;
/// sub_99EAD0</c>): a little-endian <c>u16 status</c> followed by a 128-character UTF-16LE
/// field holding up to 127 command-line characters, zero padded. It travels in plaintext
/// before the stream cipher starts.
/// </summary>
public static class BootstrapGreeting
{
    /// <summary>The greeting byte length: 2 status bytes plus 128 UTF-16LE character slots.</summary>
    public const int Size = 258;

    /// <summary>The maximum number of command-line characters that fit.</summary>
    public const int MaxCommandLineChars = 127;

    /// <summary>Builds the greeting.</summary>
    /// <param name="status">The status word sent to the client.</param>
    /// <param name="commandLine">The command-line string, at most <see cref="MaxCommandLineChars"/> characters.</param>
    /// <returns>The 258-byte greeting.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the command line is too long.</exception>
    public static byte[] Build(ushort status, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (commandLine.Length > MaxCommandLineChars)
            throw new ArgumentOutOfRangeException(nameof(commandLine), commandLine.Length,
                $"The bootstrap command line is limited to {MaxCommandLineChars} characters.");
        var greeting = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(greeting.AsSpan(0), status);
        Encoding.Unicode.GetBytes(commandLine).CopyTo(greeting.AsSpan(2));
        return greeting;
    }
}
