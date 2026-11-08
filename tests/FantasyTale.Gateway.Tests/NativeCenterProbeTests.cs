using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Reproduces TCP-ready endpoints whose native auxiliary protocol cannot complete login cleanup.</summary>
public sealed class NativeCenterProbeTests
{
    /// <summary>Every supported dialect retains a real endpoint only after its fragmented native status response arrives.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task Fragmented_status_proves_native_readiness(string build)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = Options(build, listener);
        var profile = options.Validate();
        var server = ServeAsync(listener, profile, "ready", deadline.Token);
        var output = new StringWriter();
        var result = Assert.Single(await NativeCenterProbe.ResolveAsync(options, profile, new GatewayLog(output), deadline.Token));
        await server;
        Assert.Same(options.Centers[0], result);
        Assert.Null(result.UnavailableCapacity);
        Assert.Contains("center=2 result=ready", output.ToString());
        Assert.DoesNotContain("private-native-status", output.ToString());
    }

    /// <summary>A reachable TCP service without a valid native status becomes a locally retiring unavailable entry.</summary>
    [Theory]
    [InlineData("silent")]
    [InlineData("greeting-only")]
    [InlineData("wrong-opcode")]
    [InlineData("wrong-greeting")]
    [InlineData("truncated-status")]
    [InlineData("eof")]
    public async Task Tcp_accept_is_not_native_readiness(string fault)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = Options("tw-454", listener);
        options.NativeProbeTimeoutMs = 300;
        var profile = options.Validate();
        var server = ServeAsync(listener, profile, fault, deadline.Token);
        var output = new StringWriter();
        var result = Assert.Single(await NativeCenterProbe.ResolveAsync(options, profile, new GatewayLog(output), deadline.Token));
        await server;
        Assert.Equal(731, result.UnavailableCapacity);
        Assert.Equal(options.Centers[0].Host, result.Host);
        Assert.Equal(options.Centers[0].Port, result.Port);
        Assert.Null(options.Centers[0].UnavailableCapacity);
        Assert.DoesNotContain("private-native-status", output.ToString());
    }

    /// <summary>A protocol-failed destination remains visible but its replacement answers late uniqueness cleanup locally.</summary>
    [Fact]
    public async Task Protocol_failure_uses_native_unavailable_retirement_instead_of_a_dead_remote_auxiliary()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = Options("tw-454", listener);
        options.NativeProbeTimeoutMs = 300;
        var profile = options.Validate();
        var server = ServeAsync(listener, profile, "greeting-only", deadline.Token);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        await server;
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), profile);
        await bootstrap.SendAsync(profile.PatchRequest, profile.FirstConnect.Write(new Dictionary<string, object?>
        {
            ["client_version"] = profile.Major,
        }), session.Token);
        var directory = (await bootstrap.ReadAsync(2, session.Token))[1].Payload;
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, directory.AsSpan(16, 4).ToArray());
        var port = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(20, 2));
        Assert.NotEqual(((IPEndPoint)listener.LocalEndpoint).Port, port);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, session.Token);
        using var auxiliary = new AutomaticCenterTests.NativeSocket(client, profile);
        var protocol = profile.Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        var status = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.StatusReply, status.Opcode);
        var reader = new PacketReader(status.Payload);
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal("private-native-status", reader.ReadWideStringPrefixed());
        Assert.Equal(1462u, reader.ReadUInt32());
        Assert.Equal(731u, reader.ReadUInt32());
        bootstrap.Dispose();
        await auxiliary.SendAsync(protocol.UniquenessRequest, new byte[4], session.Token, fragmented: true);
        var reply = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.UniquenessReply, reply.Opcode);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, reply.Payload);
        auxiliary.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Transport probes cannot revive an endpoint whose native protocol failed, even when refreshed RTT remains healthy.</summary>
    [Fact]
    public void Automatic_selection_excludes_native_probe_failures_before_and_after_refresh()
    {
        var options = AutomaticCenterTests.Auto([
            AutomaticCenterTests.Candidate("first", 1, 0), AutomaticCenterTests.Candidate("second", 2, 0)],
            admitted: ["first", "second"]);
        var state = new AutomaticCenterState(options, nativeProbeFailures: ["first"]);
        Assert.Equal("second", state.Select(DateTimeOffset.UtcNow, false).CenterId);
        var reply = state.Handle(new GatewayCommand
        {
            Type = "refresh-automatic", RowId = "selector", LaunchId = options.LaunchId, GrantId = options.GrantId,
            Revision = 1, Candidates = [AutomaticCenterTests.Candidate("first", 0, 0), AutomaticCenterTests.Candidate("second", 3, 0)],
        });
        Assert.Equal("accepted", reply.Status);
        Assert.Equal("second", state.Select(DateTimeOffset.UtcNow, true).CenterId);
    }

    /// <summary>Parent cancellation propagates instead of converting an aborted launch into an unavailable endpoint.</summary>
    [Fact]
    public async Task Parent_cancellation_is_not_a_health_result()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = Options("tw-454", listener);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeCenterProbe.ResolveAsync(
            options, options.Validate(), new GatewayLog(new StringWriter()), cancellation.Token));
    }

    /// <summary>Malformed probe budgets fail before any socket or listener is opened.</summary>
    [Theory]
    [InlineData(-1, 4)]
    [InlineData(99, 4)]
    [InlineData(5001, 4)]
    [InlineData(1000, 0)]
    [InlineData(1000, 17)]
    public void Probe_limits_are_validated(int timeout, int parallelism)
    {
        var options = GatewayConfigurationTests.Options("tw-454");
        options.NativeProbeTimeoutMs = timeout;
        options.NativeProbeParallelism = parallelism;
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>One shared deadline bounds queued probes rather than multiplying a timeout by the directory size.</summary>
    [Fact]
    public async Task Shared_deadline_bounds_queued_probes_and_preserves_parent_unavailable_rows()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var original = Options("tw-454", listener);
        var options = new CenterSelectorOptions
        {
            Build = original.Build, NativeProbeTimeoutMs = 300, NativeProbeParallelism = 1,
            Centers = [original.Centers[0], new CenterEntry
            {
                Id = "second", Name = "Second", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port + 1,
                NativeCenterId = 3, StatusCapacity = 532,
            }, new CenterEntry
            {
                Id = "third", Name = "Third", Host = "unresolved.invalid", Port = 7110,
                NativeCenterId = 4, StatusCapacity = 632, UnavailableCapacity = 632,
            }],
        };
        var profile = options.Validate();
        var server = ServeAsync(listener, profile, "silent", deadline.Token);
        var pending = NativeCenterProbe.ResolveAsync(options, profile, new GatewayLog(new StringWriter()), deadline.Token);
        var results = await pending.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
        await server;
        Assert.Equal([731, 532, 632], results.Select(entry => entry.UnavailableCapacity!.Value));
        Assert.Same(options.Centers[2], results[2]);
    }

    private static CenterSelectorOptions Options(string build, TcpListener listener) => new()
    {
        Build = build, NativeProbeTimeoutMs = 2000, ConnectionTrace = true,
        Centers = [new CenterEntry
        {
            Id = "first", Name = "private-native-status", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            NativeCenterId = 2, StatusCapacity = 731,
        }],
    };

    private static async Task ServeAsync(TcpListener listener, BubbleFighterBuildProfile profile, string fault, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        if (fault == "eof") return;
        var greeting = profile.Dialect.BuildServerPreamble(profile.GreetingStatus, "");
        if (fault == "wrong-greeting") greeting[0] ^= 0x7F;
        if (fault != "silent") await stream.WriteAsync(greeting, ct);
        if (fault is "silent" or "greeting-only" or "wrong-greeting")
        {
            while (await stream.ReadAsync(new byte[4096], ct) != 0) { }
            return;
        }
        using var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ClientToServer, 16384);
        var receive = new byte[4096];
        var requests = new List<DecodedFrame>();
        while (requests.All(frame => frame.Opcode != profile.Automatic!.StatusRequest))
        {
            var count = await stream.ReadAsync(receive, ct);
            Assert.NotEqual(0, count);
            requests.AddRange(decoder.Feed(receive.AsSpan(0, count)).OfType<WireStreamItem.Frame>().Select(item => item.Value));
        }
        Assert.Equal(profile.Marker is { } marker ? [marker, profile.Automatic!.StatusRequest] : [profile.Automatic!.StatusRequest],
            requests.Select(frame => frame.Opcode));
        Assert.All(requests, frame => Assert.Empty(frame.Payload));
        var body = profile.Automatic!.Status.Write(new Dictionary<string, object?>
        {
            ["center_id"] = 2, ["name"] = "private-native-status", ["users"] = 1, ["capacity"] = 731,
        });
        if (fault == "truncated-status") body = body[..^1];
        var response = profile.Dialect.EncodeFrame(fault == "wrong-opcode" ? profile.PatchReply : profile.Automatic.StatusReply, body);
        profile.Dialect.CreateEncryptor(PacketDirection.ServerToClient).Apply(response);
        await stream.WriteAsync(response.AsMemory(0, 3), ct);
        await stream.WriteAsync(response.AsMemory(3), ct);
    }
}
