using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks native unavailable directory slots, immutable membership and bounded auxiliary lifetimes.</summary>
public sealed class UnavailableCenterTests
{
    /// <summary>Initial and refreshed Automatic snapshots use actual pool membership instead of a fixed entry-count ceiling.</summary>
    [Fact]
    public void Automatic_pool_above_32_preserves_every_original_member_and_accepts_refresh()
    {
        var centers = Enumerable.Range(1, 65).Select(i => new CenterEntry
        {
            Id = "center-" + i, Name = "Center " + i, BackendId = "backend-" + i,
            NativeCenterId = i + 10, Host = "192.0.2." + i, Port = 7110,
        }).ToArray();
        var candidates = centers.Select(center => AutomaticCenterTests.Candidate(center.Id, 1, 0)).ToArray();
        var auto = AutomaticCenterTests.Auto(candidates);
        new CenterSelectorOptions { Build = "cn-324", Centers = centers, AutomaticEntry = auto }.Validate();
        var state = new AutomaticCenterState(auto);
        Assert.Equal("accepted", state.Handle(new GatewayCommand
        {
            Type = "refresh-automatic", LaunchId = auto.LaunchId, GrantId = auto.GrantId,
            Revision = 1, Candidates = candidates,
        }).Status);
        Assert.Equal(65, auto.AdmittedCenterIds.Length);
    }

    /// <summary>Every supported dialect preserves the original identity and disables a failed destination without redirecting.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("id-1809")]
    [InlineData("tw-454")]
    [InlineData("kr-1256")]
    public async Task Failed_destination_keeps_its_native_status_and_manual_directory(string build)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), options.Validate());
        var directory = await DirectoryAsync(bootstrap, session.Token);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(directory));
        Assert.Equal(new byte[] { 192, 0, 2, 10 }, directory.AsSpan(16, 4).ToArray());
        Assert.Equal(7110, BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(20, 2)));
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, directory.AsSpan(34, 4).ToArray());
        var port = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(38, 2));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, session.Token);
        using var native = new AutomaticCenterTests.NativeSocket(client, options.Validate());
        if (native.Profile.Marker is { } marker) await native.SendAsync(marker, [], session.Token);
        var protocol = native.Profile.Automatic!;
        await native.SendAsync(protocol.StatusRequest, [], session.Token, fragmented: true);
        var status = Assert.Single(await native.ReadAsync(1, session.Token));
        Assert.Equal(protocol.StatusReply, status.Opcode);
        // Pin independent field order and exact length, including the client's omitted CN2011 label.
        var reader = new PacketReader(status.Payload);
        Assert.Equal(3u, reader.ReadUInt32());
        if (build != "cn-279") Assert.Equal("Failed target", reader.ReadWideStringPrefixed());
        Assert.Equal(1462u, reader.ReadUInt32());
        Assert.Equal(731u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        await native.SendAsync(protocol.StatusRequest, [], session.Token);
        Assert.Equal(status.Payload, Assert.Single(await native.ReadAsync(1, session.Token)).Payload);
        await native.SendAsync(protocol.UniquenessRequest, new byte[4], session.Token);
        var uniqueness = Assert.Single(await native.ReadAsync(1, session.Token));
        Assert.Equal(protocol.UniquenessReply, uniqueness.Opcode);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, uniqueness.Payload);
        native.Dispose();
        Assert.False(session.Work.IsCompleted);
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.DoesNotContain("redirect sent", session.Log.ToString());
    }

    /// <summary>A forced login to the disabled slot closes only that socket and never authenticates or redirects.</summary>
    [Fact]
    public async Task Disabled_login_is_validated_erased_and_rejected()
    {
        var options = Options("cn-324");
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), options.Validate());
        var directory = await DirectoryAsync(bootstrap, session.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(38, 2)), session.Token);
        using var native = new AutomaticCenterTests.NativeSocket(client, options.Validate());
        var protocol = native.Profile.Automatic!;
        await native.SendAsync(protocol.StatusRequest, [], session.Token);
        await native.ReadAsync(1, session.Token);
        await native.SendAsync(protocol.LoginRequest, protocol.Login.Write(new Dictionary<string, object?>
        {
            ["credential"] = "private-disabled-ticket", ["adapters"] = Array.Empty<IReadOnlyDictionary<string, object?>>(),
        }), session.Token);
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], session.Token));
        Assert.False(session.Work.IsCompleted);
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.DoesNotContain("private-disabled-ticket", session.Log.ToString());
        Assert.DoesNotContain("redirect sent", session.Log.ToString());
    }

    /// <summary>Bootstrap release cancels all unaccepted status sockets, including directories larger than the removed entry-count limit.</summary>
    [Fact]
    public async Task All_failed_entries_remain_in_directory_and_release_without_accepting_auxiliaries()
    {
        var options = new CenterSelectorOptions
        {
            Build = "cn-324", Centers = Enumerable.Range(1, 65).Select(i => new CenterEntry
            {
                Id = "failed-" + i, Name = "Failed " + i, Host = "target-" + i + ".invalid", Port = 7110,
                NativeCenterId = i, UnavailableCapacity = 512,
            }).ToArray(),
        };
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        Assert.Equal("bootstrap", session.Ready.Rows[0].Mode);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), options.Validate());
        var directory = await DirectoryAsync(bootstrap, session.Token);
        Assert.Equal(65, BinaryPrimitives.ReadInt32LittleEndian(directory));
        Assert.Equal(4 + 18 * 65, directory.Length);
        var ports = Enumerable.Range(0, 65).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(20 + i * 18, 2))).ToArray();
        Assert.Equal(65, ports.Distinct().Count());
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
        var remaining = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        Assert.DoesNotContain(remaining, endpoint => IPAddress.IsLoopback(endpoint.Address) && ports.Contains((ushort)endpoint.Port));
    }

    /// <summary>A failed member stays authorized but cannot be included in active launch or refresh snapshots.</summary>
    [Fact]
    public void Failed_member_preserves_immutable_admission_but_rejects_active_snapshots()
    {
        var manual = Options("cn-324");
        var auto = AutomaticCenterTests.Auto([AutomaticCenterTests.Candidate("healthy", 1, 0)], admitted: ["healthy", "failed"]);
        var centers = new[]
        {
            new CenterEntry { Id = "healthy", Name = "Healthy", BackendId = "healthy-backend", NativeCenterId = 2, Host = "192.0.2.10", Port = 7110 },
            Failed(),
        };
        var options = new CenterSelectorOptions { Build = manual.Build, Centers = centers, AutomaticEntry = auto };
        options.Validate();
        var state = new AutomaticCenterState(auto, ["failed"]);
        var rejected = state.Handle(new GatewayCommand
        {
            Type = "refresh-automatic", LaunchId = auto.LaunchId, GrantId = auto.GrantId, Revision = 1,
            Candidates = [AutomaticCenterTests.Candidate("failed", 1, 0)],
        });
        Assert.Equal("rejected", rejected.Status);
        Assert.Equal("healthy", state.Select(DateTimeOffset.UtcNow, false).CenterId);
        Assert.Contains("failed", auto.AdmittedCenterIds);
        var invalid = new CenterSelectorOptions
        {
            Build = manual.Build, Centers = centers,
            AutomaticEntry = AutomaticCenterTests.Auto([AutomaticCenterTests.Candidate("failed", 1, 0)], admitted: ["healthy", "failed"]),
        };
        Assert.Throws<GatewayConfigurationException>(() => invalid.Validate());
    }

    /// <summary>The native sentinel accepts only positive capacities that cannot overflow when doubled.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1073741824)]
    public void Invalid_capacity_is_rejected(int capacity)
        => Assert.Throws<GatewayConfigurationException>(() => Failed(capacity: capacity).Validate());

    /// <summary>Only explicitly failed destinations can retain an unresolved original DNS name.</summary>
    [Fact]
    public void Dns_and_single_failed_entry_require_explicit_native_unavailable_contract()
    {
        Assert.IsType<DnsEndPoint>(Failed().Validate());
        var options = new CenterSelectorOptions { Build = "cn-324", Centers = [Failed()] };
        options.Validate();
        Assert.Throws<GatewayConfigurationException>(() => new CenterEntry
        {
            Id = "failed", Name = "Failed target", Host = "target.invalid", Port = 7110,
        }.Validate());
        Assert.Throws<GatewayConfigurationException>(() => new CenterEntry
        {
            Id = "failed", Name = "Failed target", Host = "target.invalid", Port = 7110, UnavailableCapacity = 731,
        }.Validate());
    }

    private static CenterSelectorOptions Options(string build) => new()
    {
        Build = build, Centers =
        [
            new CenterEntry { Id = "healthy", Name = "Healthy", Host = "192.0.2.10", Port = 7110, BackendId = "healthy-backend", NativeCenterId = 2 },
            Failed(),
        ],
    };

    private static CenterEntry Failed(int capacity = 731) => new()
    {
        Id = "failed", Name = "Failed target", Host = "target.invalid", Port = 7110,
        BackendId = "failed-backend", NativeCenterId = 3, UnavailableCapacity = capacity,
    };

    private static async Task<byte[]> DirectoryAsync(AutomaticCenterTests.NativeSocket bootstrap, CancellationToken ct)
    {
        var profile = bootstrap.Profile;
        await bootstrap.SendAsync(profile.PatchRequest, profile.FirstConnect.Write(new Dictionary<string, object?>
        {
            ["client_version"] = profile.Major,
        }), ct);
        var frames = await bootstrap.ReadAsync(2, ct);
        Assert.Equal(profile.CenterList, frames[1].Opcode);
        return frames[1].Payload;
    }
}
