using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Diagnostics;

internal static class ConnectionStageLog
{
    internal static string Accepted(ConnectionAcceptedEvent e) =>
        FormattableString.Invariant($"connection-stage stage=accepted conn={e.Connection.Id} listener={Token(e.ListenerId)}");

    internal static string Greeting(BootstrapGreetingWrittenEvent e) =>
        FormattableString.Invariant($"connection-stage stage=greeting-written conn={e.ConnectionId} profile={Token(e.ProfileId)} protocol={Token(e.ProtocolName)} dialect={Token(e.DialectId)} bytes={e.ByteLength} status={e.Status}");

    internal static string FirstFrame(FirstInboundFrameEvent e) =>
        Frame("first-client-opcode", e.ConnectionId, e.ProfileId, e.ProtocolName, e.Opcode, e.PayloadLength);

    internal static string? CenterFrame(IOpcodeTableRegistry? tables, ISession session, string protocol,
        PacketDirection direction, ushort opcode, int bytes)
    {
        if (tables is null || !tables.TryForProtocol(protocol, out var table) || table is null
            || table.Connection != OpcodeConnection.Game || !table.TryGet(direction, opcode, out var entry))
            return null;
        var expected = direction == PacketDirection.ClientToServer ? "gcpReqCenterUserCnt" : "gspRepCenterUserCnt";
        if (entry?.Name != expected) return null;
        return Frame(direction == PacketDirection.ClientToServer ? "center-status-request" : "center-status-response",
            session.Connection.Id, session.Profile.Id, protocol, opcode, bytes);
    }

    internal static string Closed(ConnectionClosedEvent e) =>
        FormattableString.Invariant($"connection-stage stage=closed conn={e.Connection.Id} reason={CloseReason(e.Reason)}");

    private static string Frame(string stage, long connection, string profile, string protocol, ushort opcode, int bytes) =>
        FormattableString.Invariant($"connection-stage stage={stage} conn={connection} profile={Token(profile)} protocol={Token(protocol)} opcode={opcode} bytes={bytes}");

    private static string Token(string value)
    {
        // Identities come from configuration, not client strings; bound and escape the single-line token.
        var length = Math.Min(96, value.Length);
        return length == 0 ? "-" : string.Create(length, value, static (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = char.IsAsciiLetterOrDigit(source[i]) || source[i] is '-' or '_' or '/' or '.' ? source[i] : '_';
        });
    }

    private static string CloseReason(string reason) => reason switch
    {
        "peer completed inbound stream" => "peer-eof",
        "protocol ended" => "protocol-ended",
        "protocol unloading" => "protocol-unloading",
        "listener stopping" => "listener-stopping",
        "idle timeout" => "idle-timeout",
        "server full" => "server-full",
        "inbound transport read failed" => "transport-read-failed",
        "inbound stream failure" => "inbound-stream-failed",
        "bootstrap greeting write stopped" => "greeting-write-stopped",
        "send queue full" or "send queue byte limit" => "send-queue-limit",
        "alternate handshake sequence was interrupted" => "alternate-handshake-interrupted",
        "alternate handshake sequence packets must have empty payloads" => "alternate-handshake-payload",
        _ when reason.StartsWith("peer ended with an incomplete frame (", StringComparison.Ordinal) => "incomplete-frame",
        _ when reason.StartsWith("first packet must be ", StringComparison.Ordinal) => "handshake-rejected",
        _ when reason.StartsWith("handler failure:", StringComparison.Ordinal) => "handler-failed",
        _ when reason.StartsWith("error:", StringComparison.Ordinal) => "transport-or-protocol-error",
        // Exception messages and caller-supplied reasons may contain credentials; never copy them.
        _ => "other",
    };
}
