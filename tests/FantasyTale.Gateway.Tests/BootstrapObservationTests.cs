using System.Buffers.Binary;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks auxiliary native startup requests that must not terminate the directory connection.</summary>
public sealed class BootstrapObservationTests
{
    /// <summary>Indonesian regional requests contain two counted strings; the cash refresh request is empty.</summary>
    [Theory]
    [InlineData("id-400", 9, 8)]
    [InlineData("id-1809", 9, 8)]
    [InlineData("cn-774", 827, 0)]
    public void Auxiliary_layouts_pin_native_lengths_and_validate_string_bounds(string build, int opcode, int length)
    {
        var observation = Assert.Single(CompiledBuildCatalog.Resolve(build).BootstrapObservations);
        Assert.Equal(opcode, observation.Opcode);
        var body = observation.Layout.Write(new Dictionary<string, object?>());
        Assert.Equal(length, body.Length);
        observation.Layout.ValidatePayload(body, 1024, 32);
        Assert.Throws<PacketFormatException>(() => observation.Layout.ValidatePayload([.. body, 0], 1024, 32));
        if (length == 0) return;
        var populated = observation.Layout.Write(new Dictionary<string, object?>
        {
            ["unknown_4"] = "alpha", ["unknown_32"] = "\u4E00",
        });
        Assert.Equal(20, populated.Length);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(populated));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(populated.AsSpan(14)));
        observation.Layout.ValidatePayload(populated, 1024, 32);
        Assert.Throws<PacketFormatException>(() => observation.Layout.ValidatePayload(populated, 4, 32));
        Assert.Throws<PacketFormatException>(() => observation.Layout.ValidatePayload(populated[..^1], 1024, 32));
    }

    /// <summary>Repeated auxiliary requests preserve the encrypted directory and produce no fabricated service replies.</summary>
    [Theory]
    [InlineData("id-400", false)]
    [InlineData("id-400", true)]
    [InlineData("id-1809", false)]
    [InlineData("id-1809", true)]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    public async Task Auxiliary_requests_keep_directory_alive_without_logging_payloads(string build, bool fragmented)
    {
        await using var session = await GatewayLifecycleTests.Session.StartAsync(GatewayConfigurationTests.Options(build));
        using var game = await session.ConnectGameAsync();
        var stream = game.GetStream();
        var profile = CompiledBuildCatalog.Resolve(build);
        var observation = Assert.Single(profile.BootstrapObservations);
        var first = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        var request = profile.Dialect.EncodeFrame(observation.Opcode,
            observation.Layout.Write(new Dictionary<string, object?> { ["unknown_4"] = "private-fixture-value" }));
        var bytes = first.Concat(request).Concat(request).Concat(request).ToArray();
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(bytes);
        if (fragmented)
        {
            await stream.WriteAsync(bytes.AsMemory(0, 3), session.Token);
            await stream.WriteAsync(bytes.AsMemory(3), session.Token);
        }
        else await stream.WriteAsync(bytes, session.Token);
        game.Client.Shutdown(SocketShutdown.Send);
        using var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ServerToClient, 16384);
        var frames = new List<DecodedFrame>();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, session.Token)) != 0)
            frames.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(item => item.Value));
        await session.Work.WaitAsync(session.Token);
        Assert.Equal(new[] { profile.PatchReply, profile.CenterList }, frames.Select(frame => frame.Opcode));
        var log = session.Log.ToString();
        Assert.Contains("bootstrap connection released.", log);
        Assert.Equal(1, log.Split("native auxiliary request observed;", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("private-fixture-value", log);
    }

    /// <summary>An auxiliary opcode cannot bypass first-connect validation or hide a malformed body.</summary>
    [Theory]
    [InlineData("id-400", false)]
    [InlineData("id-400", true)]
    [InlineData("id-1809", false)]
    [InlineData("id-1809", true)]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    public async Task Early_or_malformed_auxiliary_requests_remain_errors(string build, bool afterDirectory)
    {
        await using var session = await GatewayLifecycleTests.Session.StartAsync(GatewayConfigurationTests.Options(build));
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve(build);
        var observation = Assert.Single(profile.BootstrapObservations);
        var body = observation.Layout.Write(new Dictionary<string, object?>());
        var request = profile.Dialect.EncodeFrame(observation.Opcode, afterDirectory ? [.. body, 0] : body);
        var first = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        var bytes = afterDirectory ? first.Concat(request).ToArray() : request;
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(bytes);
        await game.GetStream().WriteAsync(bytes, session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        Assert.DoesNotContain("native auxiliary request observed;", session.Log.ToString());
    }
}
