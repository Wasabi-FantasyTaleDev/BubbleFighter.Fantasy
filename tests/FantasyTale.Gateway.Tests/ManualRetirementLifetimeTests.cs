using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Separates ended automatic admission from the native cleanup of an unselected local connection.</summary>
public sealed class ManualRetirementLifetimeTests
{
    /// <summary>Withdrawing all automatic candidates preserves a status-complete auxiliary until its uniqueness exchange.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("tw-454")]
    public Task Withdrawn_automatic_admission_keeps_manual_cleanup_alive(string build) => RunAsync(build, expired: false);

    /// <summary>An expired grant rejects new admission but still answers native cleanup on the accepted auxiliary.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-774")]
    public Task Expired_automatic_admission_keeps_manual_cleanup_alive(string build) => RunAsync(build, expired: true);

    private static async Task RunAsync(string build, bool expired)
    {
        var original = AutomaticCenterTests.Options(build);
        var options = new CenterSelectorOptions
        {
            Build = build, Centers = original.Centers, ConnectionTrace = true,
            AutomaticEntry = AutomaticCenterTests.Auto(original.AutomaticEntry!.Candidates,
                unavailableCapacity: 100, grantExpiresAtUtc: expired ? DateTimeOffset.UtcNow.AddSeconds(2) : null),
        };
        var profile = options.Validate();
        var protocol = profile.Automatic!;
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = new AutomaticCenterTests.NativeSocket(await session.ConnectGameAsync(), profile);
        await bootstrap.SendAsync(profile.PatchRequest, profile.FirstConnect.Write(new Dictionary<string, object?>
        {
            ["client_version"] = profile.Major,
        }), session.Token);
        var directory = (await bootstrap.ReadAsync(2, session.Token))[1].Payload;
        var automaticPort = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(20, 2));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, automaticPort, session.Token);
        using var auxiliary = new AutomaticCenterTests.NativeSocket(client, profile);
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        Assert.Equal(protocol.StatusReply, Assert.Single(await auxiliary.ReadAsync(1, session.Token)).Opcode);
        if (expired)
        {
            var remaining = options.AutomaticEntry.GrantExpiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(20), session.Token);
        }
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand
        {
            Type = "refresh-automatic", RowId = "selector", LaunchId = options.AutomaticEntry.LaunchId,
            GrantId = options.AutomaticEntry.GrantId, Revision = 1, Candidates = [],
        }, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        var body = await ControlMessageStream.ReadAsync(session.Parent, session.Token);
        var result = JsonSerializer.Deserialize(body!, GatewayJsonContext.Strict.GatewayCommandResult)!;
        Assert.Equal(expired ? "rejected" : "accepted", result.Status);
        Assert.Equal(expired ? "grant-expired" : "updated", result.Reason);
        await Task.WhenAny(session.Work, Task.Delay(100, session.Token));
        Assert.False(session.Work.IsCompleted);
        await auxiliary.SendAsync(protocol.UniquenessRequest, new byte[4], session.Token, fragmented: true);
        var reply = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.UniquenessReply, reply.Opcode);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, reply.Payload);
        Assert.False(session.Work.IsCompleted);
        auxiliary.Dispose();
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
        var trace = session.Log.ToString();
        Assert.Contains($"connection role=automatic port={automaticPort} event=uniqueness-replied.", trace);
        Assert.Contains($"connection role=automatic port={automaticPort} event=eof.", trace);
        Assert.DoesNotContain($"connection role=automatic port={automaticPort} event=cancelled.", trace);
    }
}
