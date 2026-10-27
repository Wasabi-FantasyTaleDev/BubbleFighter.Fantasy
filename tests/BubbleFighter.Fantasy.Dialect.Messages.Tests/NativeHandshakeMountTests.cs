using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Exercises modern native handshake rows against each owning build's tables: the capability and the
/// account admission of the same relay join, the scene report and the reconnect map. The reply frames those
/// admissions produce are pinned by RelayHandshakeTableParityTests (MatchConnection.Tests).
/// </summary>
public sealed class NativeHandshakeMountTests
{
    private const string Build = "kr-1256";

    [Theory]
    [InlineData("kr-1256", 1256, "capability")]
    [InlineData("kr-1256", 1256, "account")]
    [InlineData("cn-774", 774, "capability")]
    [InlineData("cn-774", 774, "account")]
    [InlineData("tw-454", 454, "capability")]
    [InlineData("tw-454", 454, "account")]
    public async Task TheNativeJoinReachesItsAdmissionsOwnTypedRoute(string build, int version, string mode)
    {
        using var fixture = Fixture(build);
        var context = fixture.Context(build, build + "/relay", extra: new() { ["native_handshake"] = mode });
        await new MatchMessagesPlugin().ActivateAsync(context, default);
        try
        {
            Assert.Equal(3, fixture.Packets.Handlers.Count);
            var session = new MatchMessagesMountTests.Session(Profile(build));
            await fixture.Packets.DispatchAsync(build + "/relay", session, new(1, Join()), default);
            if (mode == "account")
            {
                // The account admission keeps the key and the join's own account word and nickname
                // (kr-1256 sub_95FD30 writes both after the key).
                var join = Assert.IsType<MatchAuthenticate>(Assert.Single(fixture.Messages.Received));
                Assert.Equal(new byte[20], join.Ticket.ToArray());
                Assert.Equal(7, join.UserNumber);
                Assert.Equal("host", join.Nickname);
                Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<MatchAuthenticate>>());
                Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<MatchSceneRegistered>>());
                Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchCapabilityAuthenticate>>());
                Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchSceneIdentityRegistered>>());
            }
            else
            {
                // The capability admission keeps only the key, as the typed-round admission consumes it.
                var join = Assert.IsType<MatchCapabilityAuthenticate>(Assert.Single(fixture.Messages.Received));
                Assert.Equal(new byte[20], join.Ticket.ToArray());
                Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<MatchCapabilityAuthenticate>>());
                Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<MatchSceneIdentityRegistered>>());
                Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchAuthenticate>>());
                Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchSceneRegistered>>());
            }
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<MatchMapNamed>>());

            if (mode == "account" && build != "kr-1256")
                // cn-774 sub_B593D0 writes no account word, so an account handshake cannot admit the scene.
                await Assert.ThrowsAsync<PacketFormatException>(() =>
                    fixture.Packets.DispatchAsync(build + "/relay", session, new(12, Scene(build)), default).AsTask());
            else
                await fixture.Packets.DispatchAsync(build + "/relay", session, new(12, Scene(build)), default);
            if (mode == "account" && build == "kr-1256")
                Assert.Equal(new MatchSceneRegistered(7, 1000000, "host"), fixture.Messages.Received[^1]);
            else if (mode != "account")
                Assert.Equal(new MatchSceneIdentityRegistered(1000000, "host"), fixture.Messages.Received[^1]);

            await fixture.Packets.DispatchAsync(build + "/relay", session, new(3, Map(version)), default);
            Assert.Equal(new MatchMapNamed(1000000, "host", "map"), fixture.Messages.Received[^1]);
            // The reconnect map carries the client's own version word; another word is not the frame.
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                fixture.Packets.DispatchAsync(build + "/relay", session, new(3, Map(812)), default).AsTask());

            var other = new MatchMessagesMountTests.Session(Profile("cn-324"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Packets.DispatchAsync(build + "/relay", other, new(1, Join()), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchMapNamed>>());
    }

    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task TheNativeHandshakeConsumesTheJoinAndSceneBodiesExactly(string build)
    {
        using var fixture = Fixture(build);
        var context = fixture.Context(build, build + "/relay", extra: new() { ["native_handshake"] = "capability" });
        await new MatchMessagesPlugin().ActivateAsync(context, default);
        try
        {
            var session = new MatchMessagesMountTests.Session(Profile(build));
            var join = Join();
            for (var length = 0; length < join.Length; length++)
                await Assert.ThrowsAsync<PacketFormatException>(() =>
                    fixture.Packets.DispatchAsync(build + "/relay", session, new(1, join[..length]), default).AsTask());
            byte[] joinTrailing = [.. join, 0];
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                fixture.Packets.DispatchAsync(build + "/relay", session, new(1, joinTrailing), default).AsTask());
            var scene = Scene(build);
            for (var length = 0; length < scene.Length; length++)
                await Assert.ThrowsAsync<PacketFormatException>(() =>
                    fixture.Packets.DispatchAsync(build + "/relay", session, new(12, scene[..length]), default).AsTask());
            byte[] sceneTrailing = [.. scene, 0];
            await Assert.ThrowsAsync<PacketFormatException>(() =>
                fixture.Packets.DispatchAsync(build + "/relay", session, new(12, sceneTrailing), default).AsTask());
            Assert.Empty(fixture.Messages.Received);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task AnUnknownAdmissionOrMissingSceneRowFailsBeforePublishingEffects()
    {
        using var fixture = Fixture();
        var context = fixture.Context(Build, Build + "/relay", extra: new() { ["native_handshake"] = "ticket" });
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() => new MatchMessagesPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("native_handshake", error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }

        // Without the coined relay C2S 12 row the handshake cannot resolve its scene report.
        var entries = new List<OpcodeEntry> { Entry(1, "rcpReqJoinRelaySession"), Entry(3, "rcpReqRelayReconnectMap_") };
        using var bare = new MatchMessagesMountTests.Fixture(new OpcodeTable(Profile(), entries));
        var missing = bare.Context(Build, Build + "/relay", extra: new() { ["native_handshake"] = "capability" });
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() => new MatchMessagesPlugin().ActivateAsync(missing, default).AsTask());
            Assert.Contains("rcpReqReportSceneData_", error.Message, StringComparison.Ordinal);
            Assert.Empty(bare.Packets.Handlers);
        }
        finally { await missing.UnwindAsync(); }
    }

    private static MatchMessagesMountTests.Fixture Fixture(string build = Build)
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), Profile(build));
        return new(table.ForConnection(OpcodeConnection.Relay));
    }

    private static ClientProfile Profile(string build = Build) =>
        new(build, build[..2], int.Parse(build[3..]), 0, 0, build);

    private static OpcodeEntry Entry(ushort value, string name) => new(value, PacketDirection.ClientToServer, name,
        Evidence.Verified, null, null) { Connection = OpcodeConnection.Relay };

    // kr-1256 sub_95FD30: the twenty-byte session key, a byte, two words, then the account and nickname.
    private static byte[] Join() => Bytes(w =>
    {
        w.WriteBytes(new byte[20]);
        w.WriteByte(0);
        w.WriteInt32(0);
        w.WriteInt32(7);
        w.WriteWideStringPrefixed("host");
    });

    // kr-1256 sub_9F02F0: the version word, a widened byte, the map string, the entity word, the nickname
    // string and two words.
    private static byte[] Map(int version) => Bytes(w =>
    {
        w.WriteInt32(version);
        w.WriteInt32(0);
        w.WriteWideStringPrefixed("map");
        w.WriteInt32(1000000);
        w.WriteWideStringPrefixed("host");
        w.WriteInt32(0);
        w.WriteInt32(0);
    });

    // Populated native writers are pinned independently in ModernSceneTableTests; here the real routes
    // consume the build's resolved scene, including the CN/TW identity order and equipped-object containers.
    private static byte[] Scene(string build = Build) => PacketCodec.Write(TestBuilds.Packets, build,
        "RelaySceneReport", new Dictionary<string, object?>
        {
            [build == "kr-1256" ? "account" : "unknown_28"] = 7, ["entity_id"] = 1000000, ["nickname"] = "host",
            ["equipment"] = new Dictionary<string, object?> { ["character_id"] = 300001 },
            ["unknown_128"] = "scene",
        });

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }
}
