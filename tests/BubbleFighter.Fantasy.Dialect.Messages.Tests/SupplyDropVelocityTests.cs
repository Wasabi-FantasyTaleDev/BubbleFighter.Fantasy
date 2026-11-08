using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the initial velocity sent by cn-279 throws and consumed by its item-box creation handler.</summary>
public sealed class SupplyDropVelocityTests
{
    private const string NativeDropHex = "0000803F000000C000004040000090C00000A8400000D8C000000000";

    [Fact]
    public async Task Cn279NativeEnvelopePreservesThrowVelocity()
    {
        // cn-279 sub_7C41A0 sends six floats and kind; cn-279 sub_81FF70 applies velocity through cn-279 sub_40A9F0.
        var body = Convert.FromHexString(NativeDropHex);
        var request = SupplyBinding.ReadDrop(TestBuilds.Packets, "cn-279", body);
        Assert.Equal(28, body.Length);
        Assert.Equal(new RoundSupplyPoint(1, -2, 3, 0, 0, 0), request.Point);
        Assert.Equal(new Vector3(-4.5f, 5.25f, -6.75f), request.Velocity);
        Assert.Equal(10000, request.CreationClass);

        var session = new SupplySession();
        await Binding("cn-279").SendAsync(session, new RoundSupplyCreated(0x62000001, request.Point)
            { CreationClass = request.CreationClass, Velocity = request.Velocity }, default);
        var packet = Assert.Single(session.Sent);
        Assert.Equal(TestBuilds.Opcodes("cn-279").S2C("gspGameCreateGameItem"), packet.Opcode);
        Assert.Equal(36, packet.Body.Length);
        Assert.Equal(Convert.FromHexString("01000062102700000000803F000000C00000404000000000000090C00000A8400000D8C0"), packet.Body);
        var decoded = PacketCodec.ReadScalars(TestBuilds.Packets, "cn-279", "SupplyCreate", packet.Body);
        Assert.Equal(request.Velocity.X, Assert.IsType<float>(decoded["velocity_x"]));
        Assert.Equal(request.Velocity.Y, Assert.IsType<float>(decoded["velocity_y"]));
        Assert.Equal(request.Velocity.Z, Assert.IsType<float>(decoded["velocity_z"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Cn279DropRejectsNonFiniteKnownAxes(int axis)
    {
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var body = Convert.FromHexString(NativeDropHex);
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(axis * 4), invalid);
            Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(TestBuilds.Packets, "cn-279", body));
        }
    }

    [Fact]
    public void Cn279DropRequiresExactEnvelope()
    {
        var body = Convert.FromHexString(NativeDropHex);
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(TestBuilds.Packets, "cn-279", body[..^1]));
        Assert.Throws<PacketFormatException>(() => SupplyBinding.ReadDrop(TestBuilds.Packets, "cn-279", [.. body, 0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Cn279CreationRejectsNonFiniteTransformsBeforeSending(int axis)
    {
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var values = new[] { 1f, -2f, 3f, -4.5f, 5.25f, -6.75f };
            values[axis] = invalid;
            var message = new RoundSupplyCreated(0x62000001, new(values[0], values[1], values[2], 0, 0, 0))
                { Velocity = new(values[3], values[4], values[5]) };
            var session = new SupplySession();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Binding("cn-279").SendAsync(session, message, default).AsTask());
            Assert.Empty(session.Sent);
        }
    }

    [Theory]
    [InlineData("kr-812", 40)]
    [InlineData("cn-324", 40)]
    [InlineData("cn-717", 40)]
    [InlineData("id-400", 40)]
    [InlineData("id-1809", 40)]
    [InlineData("kr-1256", 37)]
    [InlineData("cn-774", 37)]
    [InlineData("tw-454", 37)]
    public async Task OtherBuildsKeepDefaultVelocityAndUnknownCreationWords(string build, int creationBytes)
    {
        var tables = TestBuilds.Packets;
        var body = PacketCodec.Write(tables, build, "SupplyDropRequest", new Dictionary<string, object?>
            { ["x"] = 1f, ["y"] = -2f, ["z"] = 3f, ["kind"] = 0, ["unknown_36"] = 10000, ["creation_class"] = 10000 });
        var request = SupplyBinding.ReadDrop(tables, build, body);
        Assert.Equal(Vector3.Zero, request.Velocity);
        Assert.Equal(new RoundSupplyPoint(1, -2, 3, 0, 0, 0), request.Point);
        Assert.Equal(10000, request.CreationClass);
        Assert.DoesNotContain(tables.Resolve(build, "SupplyCreate"), field => field.Name.StartsWith("velocity_", StringComparison.Ordinal));

        var message = new RoundSupplyCreated(0x62000001, request.Point);
        Assert.Equal(Vector3.Zero, message.Velocity);
        var session = new SupplySession();
        await Binding(build).SendAsync(session, message, default);
        // A vector whose outgoing meaning is unproven must not replace the build's unknown words.
        await Binding(build).SendAsync(session, message with { Velocity = new(1, -2, 3) }, default);
        var expected = new byte[creationBytes];
        Convert.FromHexString("01000062102700000000803F000000C00000404000000000").CopyTo(expected, 0);
        Assert.Equal(2, session.Sent.Count);
        Assert.All(session.Sent, packet => Assert.Equal(expected, packet.Body));
    }

    [Fact]
    public async Task DefaultTypedBoxesStayStationary()
    {
        var point = new RoundSupplyPoint(1, -2, 3, 0, 0, 0);
        Assert.Equal(Vector3.Zero, new RoundSupplyDropRequested(point, 10000).Velocity);
        var created = new RoundSupplyCreated(0x62000001, point);
        Assert.Equal(Vector3.Zero, created.Velocity);
        var session = new SupplySession();
        await Binding("cn-279").SendAsync(session, created, default);
        Assert.Equal(Convert.FromHexString("01000062102700000000803F000000C00000404000000000000000000000000000000000"),
            Assert.Single(session.Sent).Body);
    }

    private static SupplyBinding Binding(string build) => new(build + "/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build, 128);

    private sealed class SupplySession : ISession
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public long Id => 1;
        public ClientProfile Profile => new("cn-279", "cn", 279, 0, 0, "cn-279");
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }
}
