using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InventoryView.Handlers;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Exercises the view request handler end to end against a recording session: the body is read
/// through the packet tables (<c>InventoryUseRequest</c>, builder <c>kr-1256 sub_80DA80</c>) and
/// logged, and no frame is ever sent back because the client code proves no S2C reply for this
/// opcode.
/// </summary>
public sealed class InventoryViewBehaviourTests
{
    private static readonly PacketTables Tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());

    private static InventoryViewHandlers NewHandlers() => new(NullLogger.Instance, Tables, "kr-1256");

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
    public async Task PopulatedRequestIsReadAndNotAnswered()
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
        var fields = PacketCodec.Read(Tables, "kr-1256", "InventoryUseRequest", payload);
        Assert.Equal(1, Convert.ToInt32(fields["item_id"], CultureInfo.InvariantCulture));
        Assert.Equal(["AB", ""], Assert.IsAssignableFrom<object?[]>(fields["parameters"]));
    }

    /// <summary>Table migration must preserve the original request reader's string boundary.</summary>
    [Theory]
    [InlineData(1025)]
    [InlineData(16384)]
    public async Task PreviouslyAcceptedStringsRemainAccepted(int characters)
    {
        var session = new RecordingSession(5);
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(-1);
            w.WriteInt32(6);
            w.WriteUInt32(1);
            w.WriteWideStringPrefixed(new string('A', characters));
        });
        await NewHandlers().HandleViewRequestAsync(session, Request(payload), CancellationToken.None);
        Assert.Empty(session.Sent);
    }

    /// <summary>The maximum empty-string list still fits the native frame and must remain accepted.</summary>
    [Fact]
    public async Task FrameSizedListRemainsAccepted()
    {
        var session = new RecordingSession(6);
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(16380);
            for (var i = 0; i < 16380; i++)
                w.WriteWideStringPrefixed(string.Empty);
        });
        Assert.Equal(65532, payload.Length);
        await NewHandlers().HandleViewRequestAsync(session, Request(payload), CancellationToken.None);
        Assert.Empty(session.Sent);
    }

    /// <summary>Enforce the original writer bound on a complete body, not merely a truncated one.</summary>
    [Fact]
    public async Task StringAboveOriginalClientBoundIsRejected()
    {
        var session = new RecordingSession(7);
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(5);
            w.WriteInt32(6);
            w.WriteUInt32(1);
            w.WriteWideStringPrefixed(new string('A', 16385));
        });
        await Assert.ThrowsAsync<PacketFormatException>(async () =>
            await NewHandlers().HandleViewRequestAsync(session, Request(payload), CancellationToken.None));
        Assert.Empty(session.Sent);
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
