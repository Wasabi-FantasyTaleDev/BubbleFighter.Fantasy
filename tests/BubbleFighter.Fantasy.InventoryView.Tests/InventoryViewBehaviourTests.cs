using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InventoryView.Handlers;
using BubbleFighter.Fantasy.InventoryView.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Exercises the C2S 806 handler end to end against a recording session: the
/// request is parsed strictly and logged, and no frame is ever sent back because
/// the client code proves no S2C reply for this opcode.
/// </summary>
public sealed class InventoryViewBehaviourTests
{
    private static InventoryViewHandlers NewHandlers() => new(NullLogger.Instance);

    private static InboundPacket Request(byte[] payload) =>
        new(806, payload);

    [Fact]
    public async Task EmptyRequestIsLoggedFireAndForgetWithoutReplying()
    {
        var session = new RecordingSession(1);
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(0);
        });

        await NewHandlers().HandleViewRequestAsync(session, Request(payload), CancellationToken.None);

        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task PopulatedRequestIsParsedAndNotAnswered()
    {
        var session = new RecordingSession(2);
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1);
            w.WriteInt32(2);
            w.WriteUInt32(2);
            w.WriteWideStringPrefixed("AB");
            w.WriteWideStringPrefixed(string.Empty);
        });

        await NewHandlers().HandleViewRequestAsync(session, Request(payload), CancellationToken.None);

        Assert.Empty(session.Sent);
        var parsed = InventoryViewRequest.Parse(payload);
        Assert.Equal(new[] { "AB", string.Empty }, parsed.Records);
    }

    [Fact]
    public async Task MalformedRequestThrowsBeforeAnythingIsSent()
    {
        var session = new RecordingSession(3);
        var payload = WireLengthSupport.Written(w => w.WriteInt32(7));

        await Assert.ThrowsAsync<PacketFormatException>(async () =>
            await NewHandlers().HandleViewRequestAsync(session, Request(payload), CancellationToken.None));
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task TrailingBytesThrowAndNothingIsSent()
    {
        var session = new RecordingSession(4);
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(0);
            w.WriteByte(0);
        });

        await Assert.ThrowsAsync<PacketFormatException>(async () =>
            await NewHandlers().HandleViewRequestAsync(session, Request(payload), CancellationToken.None));
        Assert.Empty(session.Sent);
    }
}
