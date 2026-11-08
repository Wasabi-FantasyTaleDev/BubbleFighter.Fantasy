using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Exercises native auxiliary retirement after the selected directory has already closed.</summary>
public sealed class UnavailableRetirementTests
{
    /// <summary>A partial uniqueness frame survives directory release and the earlier retirement of Automatic.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("kr-812", true)]
    [InlineData("cn-324", false)]
    [InlineData("cn-324", true)]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-279", false)]
    [InlineData("cn-279", true)]
    [InlineData("cn-404", false)]
    [InlineData("cn-404", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-406", true)]
    [InlineData("cn-408", false)]
    [InlineData("cn-408", true)]
    [InlineData("cn-706", false)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", false)]
    [InlineData("cn-717", true)]
    [InlineData("cn-735", false)]
    [InlineData("cn-735", true)]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    [InlineData("id-400", false)]
    [InlineData("id-400", true)]
    [InlineData("id-1809", false)]
    [InlineData("id-1809", true)]
    [InlineData("tw-454", false)]
    [InlineData("tw-454", true)]
    public async Task Accepted_unavailable_replies_to_uniqueness_after_main_release(string build, bool automatic)
    {
        var options = Options(build, automatic);
        options.ConnectionTrace = true;
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), options.Validate());
        var directory = await DirectoryAsync(bootstrap, session.Token);
        using var unavailable = await ConnectAsync(directory, automatic ? 2 : 1, options, session.Token);
        var protocol = unavailable.Profile.Automatic!;
        await unavailable.SendAsync(protocol.StatusRequest, [], session.Token);
        Assert.Equal(protocol.StatusReply, Assert.Single(await unavailable.ReadAsync(1, session.Token)).Opcode);
        using var auto = automatic ? await ConnectAsync(directory, 0, options, session.Token) : null;
        if (auto is not null)
        {
            await auto.SendAsync(protocol.StatusRequest, [], session.Token);
            await auto.ReadAsync(1, session.Token);
        }
        await unavailable.SendAsync(protocol.UniquenessRequest, new byte[4], session.Token, fragmented: true,
            beforeFinalFragment: async () =>
            {
                bootstrap.Dispose();
                if (auto is not null)
                {
                    await auto.SendAsync(protocol.UniquenessRequest, new byte[4], session.Token);
                    Assert.Equal(new byte[] { 1, 0, 0, 0 }, Assert.Single(await auto.ReadAsync(1, session.Token)).Payload);
                    auto.Dispose();
                }
                // Keep the frame incomplete while the main completion gets a chance to cancel siblings.
                await Task.WhenAny(session.Work, Task.Delay(150, session.Token));
            });
        var reply = Assert.Single(await unavailable.ReadAsync(1, session.Token));
        Assert.Equal(protocol.UniquenessReply, reply.Opcode);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, reply.Payload);
        Assert.False(session.Work.IsCompleted);
        unavailable.Dispose();
        await session.Work.WaitAsync(session.Token);
        var unavailablePort = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(20 + (automatic ? 2 : 1) * 18, 2));
        var prefix = $"bf-center-selector: connection role=unavailable port={unavailablePort} event=";
        var events = session.Log.ToString().Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..]).ToArray();
        Assert.Equal(new[] { "listening.", "accepted.", "status-sent.", "uniqueness-received.", "uniqueness-replied.", "eof.", "closed." }, events);
        var profile = options.Validate();
        Assert.Contains($"binding role=unavailable port={unavailablePort} group={profile.Group} sub={profile.ReservedCenter + (automatic ? 3 : 2)}.", session.Log.ToString());
        Assert.DoesNotContain("redirect sent", session.Log.ToString());
    }

    /// <summary>A client that never retires cannot keep the sidecar resident after the directory has closed.</summary>
    [Fact]
    public async Task Accepted_unavailable_has_a_finite_retirement_deadline()
    {
        var options = Options("kr-812", false);
        options.HandshakeTimeoutMs = 1000;
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), options.Validate());
        var directory = await DirectoryAsync(bootstrap, session.Token);
        using var unavailable = await ConnectAsync(directory, 1, options, session.Token);
        var protocol = unavailable.Profile.Automatic!;
        await unavailable.SendAsync(protocol.StatusRequest, [], session.Token);
        await unavailable.ReadAsync(1, session.Token);
        bootstrap.Dispose();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => session.Work.WaitAsync(session.Token));
        Assert.Equal("Unavailable Center retirement deadline expired.", error.Message);
        Assert.Equal(0, await unavailable.Client.GetStream().ReadAsync(new byte[1], session.Token));
    }

    /// <summary>Parent STOP immediately cancels accepted auxiliaries during their retirement grace period.</summary>
    [Fact]
    public async Task Parent_stop_cancels_retirement_without_waiting_for_the_deadline()
    {
        var options = Options("cn-324", false);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), options.Validate());
        var directory = await DirectoryAsync(bootstrap, session.Token);
        using var unavailable = await ConnectAsync(directory, 1, options, session.Token);
        var protocol = unavailable.Profile.Automatic!;
        await unavailable.SendAsync(protocol.StatusRequest, [], session.Token);
        await unavailable.ReadAsync(1, session.Token);
        bootstrap.Dispose();
        await Task.WhenAny(session.Work, Task.Delay(150, session.Token));
        Assert.False(session.Work.IsCompleted);
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" },
            GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(TimeSpan.FromSeconds(2), session.Token);
        Assert.Equal(0, await unavailable.Client.GetStream().ReadAsync(new byte[1], session.Token));
    }

    /// <summary>Several unavailable endpoints retain their late retirement events beyond the ordinary stderr limit.</summary>
    [Fact]
    public async Task Connection_trace_retains_each_endpoint_after_the_default_log_budget()
    {
        var options = new CenterSelectorOptions
        {
            Build = "cn-406",
            ConnectionTrace = true,
            Centers = [new CenterEntry { Id = "healthy", Name = "Healthy", Host = "192.0.2.10", Port = 7110, NativeCenterId = 2 },
                .. Enumerable.Range(1, 8).Select(index => new CenterEntry
                {
                    Id = "failed-" + index, Name = "private-trace-fixture-" + index,
                    Host = "target.invalid", Port = 7110 + index, NativeCenterId = 2 + index, UnavailableCapacity = 100,
                })],
        };
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), options.Validate());
        var directory = await DirectoryAsync(bootstrap, session.Token);
        var sockets = new List<AutomaticCenterTests.NativeSocket>();
        try
        {
            for (var index = 1; index <= 8; index++)
            {
                var socket = await ConnectAsync(directory, index, options, session.Token);
                sockets.Add(socket);
                await socket.SendAsync(socket.Profile.Automatic!.StatusRequest, [], session.Token);
                await socket.ReadAsync(1, session.Token);
            }
            bootstrap.Dispose();
            foreach (var socket in sockets)
            {
                await socket.SendAsync(socket.Profile.Automatic!.UniquenessRequest, new byte[4], session.Token);
                Assert.Equal(new byte[] { 1, 0, 0, 0 }, Assert.Single(await socket.ReadAsync(1, session.Token)).Payload);
                socket.Dispose();
            }
            await session.Work.WaitAsync(session.Token);
            var log = session.Log.ToString();
            Assert.True(log.Length > 2048);
            Assert.DoesNotContain("private-trace-fixture", log);
            for (var index = 1; index <= 8; index++)
            {
                var port = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(20 + index * 18, 2));
                Assert.Contains($"connection role=unavailable port={port} event=uniqueness-replied.", log);
                Assert.Contains($"connection role=unavailable port={port} event=closed.", log);
            }
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }
    }

    private static CenterSelectorOptions Options(string build, bool automatic) => new()
    {
        Build = build,
        Centers =
        [
            new CenterEntry { Id = "healthy", Name = "Healthy", Host = "192.0.2.10", Port = 7110,
                BackendId = "healthy-backend", NativeCenterId = 2 },
            new CenterEntry { Id = "failed", Name = "Unavailable", Host = "target.invalid", Port = 7110,
                BackendId = "failed-backend", NativeCenterId = 3, UnavailableCapacity = 100 },
            new CenterEntry { Id = "unaccepted", Name = "Unaccepted", Host = "unaccepted.invalid", Port = 7110,
                BackendId = "unaccepted-backend", NativeCenterId = 4, UnavailableCapacity = 100 },
        ],
        AutomaticEntry = automatic ? AutomaticCenterTests.Auto([AutomaticCenterTests.Candidate("healthy", 1, 0)]) : null,
    };

    private static async Task<byte[]> DirectoryAsync(AutomaticCenterTests.NativeSocket bootstrap, CancellationToken ct)
    {
        var profile = bootstrap.Profile;
        await bootstrap.SendAsync(profile.PatchRequest, profile.FirstConnect.Write(new Dictionary<string, object?>
        {
            ["client_version"] = profile.Major,
        }), ct);
        return (await bootstrap.ReadAsync(2, ct))[1].Payload;
    }

    private static async Task<AutomaticCenterTests.NativeSocket> ConnectAsync(byte[] directory, int index,
        CenterSelectorOptions options, CancellationToken ct)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(20 + index * 18, 2)), ct);
        return new AutomaticCenterTests.NativeSocket(client, options.Validate());
    }
}
