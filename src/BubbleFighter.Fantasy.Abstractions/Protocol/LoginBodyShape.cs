using System.Buffers.Binary;

namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>
/// Discriminates the two proven C2S 3 login body shapes by their leading dword.
/// The reconnect shape (builder <c>sub_9F02F0</c>) starts with the literal
/// <see cref="ReconnectMarker"/>; a launcher body (builder <c>sub_85FF00</c>)
/// starts with the session character count, which every plausible identity
/// keeps far below the marker. Both the lobby login parser and the in-game
/// match-connection seam need this test, so the marker lives here rather than
/// in either plugin.
/// </summary>
public static class LoginBodyShape
{
    /// <summary>
    /// The reconnect body's leading dword. Both reconnect call sites store the
    /// literal 1256 (0x4E8 = the client's major version) there
    /// (<c>sub_9E7080</c>, S2C 607 case 0x25F; <c>sub_9E1EB0</c>, S2C 574 case
    /// 0x23E).
    /// </summary>
    public const int ReconnectMarker = 1256;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="body"/> begins with
    /// the <see cref="ReconnectMarker"/> dword. A body shorter than the marker
    /// is never a reconnect login.
    /// </summary>
    /// <param name="body">The decrypted C2S 3 payload.</param>
    /// <returns><see langword="true"/> for the reconnect shape.</returns>
    public static bool IsReconnectShape(ReadOnlySpan<byte> body) =>
        body.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(body) == ReconnectMarker;
}
