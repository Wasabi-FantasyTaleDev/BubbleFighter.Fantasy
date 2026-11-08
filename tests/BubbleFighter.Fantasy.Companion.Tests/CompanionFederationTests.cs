using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BubbleFighter.Fantasy.Contracts.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Companion.Tests;

public sealed partial class CompanionLoopbackTests
{
    [Fact]
    public async Task AuthorityLaunchRedirectsToTheExactWorkerSessionAndRenewsThere()
    {
        var workerOptions = new CompanionOptions { Listen = "127.0.0.1:0" };
        var workerLaunches = new CompanionLaunches(workerOptions, _clock, NullLogger.Instance);
        var workerHub = new CompanionHub(workerLaunches);
        using var worker = new CompanionFederation(workerLaunches, workerOptions, _clock, NullLogger.Instance);
        using var authority = new CompanionFederation(_launches, _options, _clock, NullLogger.Instance);
        await using var workerServer = await CompanionServer.StartAsync(workerOptions, workerLaunches, workerHub,
            NullLogger.Instance, default, worker);
        var workerOrigin = new Uri(workerServer.Addresses.Single());
        workerOptions.PublicUrl = $"ws://{{host}}:{workerOrigin.Port}{CompanionServer.Path}";
        var key = new string('k', 32);
        using var trust = worker.TrustAuthority(key, ["cn-324"]);
        var grant = authority.Open("alice", "cn-324");
        await using var original = await Client.ConnectAsync(_endpoint, grant.Ticket);
        Assert.Equal("pending_game", (await original.HelloAsync("cn-324", grant.LaunchId, []))
            .GetProperty("payload").GetProperty("state").GetString());

        Assert.False(await authority.RedirectAsync("bob", "cn-324", grant.LaunchId, "worker", workerOrigin, key, default));
        Assert.True(await authority.RedirectAsync("alice", "cn-324", grant.LaunchId, "worker", workerOrigin, key, default));
        Assert.Equal("companion.node-changed", await ClosedReason(original));
        Assert.Null(_launches.Renew("alice", grant.LaunchId));
        Assert.Null(await authority.RenewAsync("bob", grant.LaunchId, default));
        var forwarded = Assert.IsType<BubbleFighter.Fantasy.Contracts.Companion.CompanionGrant>(
            await authority.RenewAsync("alice", grant.LaunchId, default));
        Assert.Equal(grant.LaunchId, forwarded.LaunchId);
        Assert.Equal(workerOrigin.Port, new Uri(forwarded.Url).Port);
        var session = CommandSession();
        var input = new CommandInput(session);
        var context = CommandContext();
        context.Effect(context.Services.RegisterMany<IGameCommandInput>(input));
        try
        {
            using var commands = new CompanionCommands(context, new());
            using var route = workerHub.Route(CompanionCommands.MessageType, 1, commands.HandleAsync);
            await using var launcher = await Client.ConnectAsync(new Uri(forwarded.Url), forwarded.Ticket);
            var welcome = await launcher.HelloAsync("cn-324", grant.LaunchId, new() { [CompanionCommands.MessageType] = 1 });
            Assert.Equal("pending_game", welcome.GetProperty("payload").GetProperty("state").GetString());
            workerLaunches.Bind(new(grant.LaunchId, "alice", "cn-324", session));
            Assert.Equal("connected", (await launcher.ReceiveAsync()).GetProperty("payload").GetProperty("state").GetString());
            await launcher.SendAsync(new { kind = "request", id = "remote-plaza", type = CompanionCommands.MessageType,
                version = 1, epoch = welcome.GetProperty("epoch").GetString(), payload = new { operation_id = "leave", command = "plaza" } });
            Assert.Equal("success", (await launcher.ReceiveAsync()).GetProperty("payload").GetProperty("status").GetString());
            Assert.Equal(1, input.Calls);
            Assert.Empty(_hub.BoundSessions());
            Assert.Same(session, Assert.Single(workerHub.BoundSessions()));
            workerLaunches.End(session.Connection);
            Assert.Null(await authority.RenewAsync("alice", grant.LaunchId, default));
            Assert.Equal(0, _launches.Count);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task RemotePreparationRequiresAnExplicitAuthorityAndAllowedBuild()
    {
        using var federation = new CompanionFederation(_launches, _options, _clock, NullLogger.Instance);
        await using var server = await CompanionServer.StartAsync(new() { Listen = "127.0.0.1:0" }, _launches, _hub,
            NullLogger.Instance, default, federation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
        var identity = new CompanionFederation.Identity("alice", "cn-324", new string('a', 32));
        using var anonymous = await http.PostAsJsonAsync(CompanionFederation.Prefix + "launches", identity);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var trust = federation.TrustAuthority(new string('k', 32), ["cn-324"]);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('k', 32));
        using var unsupported = await http.PostAsJsonAsync(CompanionFederation.Prefix + "launches", identity with { Build = "kr-1256" });
        Assert.Equal(HttpStatusCode.Unauthorized, unsupported.StatusCode);
        using var imported = await http.PostAsJsonAsync(CompanionFederation.Prefix + "launches", identity);
        Assert.Equal(HttpStatusCode.NoContent, imported.StatusCode);
        using var conflict = await http.PostAsJsonAsync(CompanionFederation.Prefix + "launches", identity with { Account = "bob" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var foreignRenew = await http.PostAsJsonAsync(CompanionFederation.Prefix + "tickets", identity with { Account = "bob" });
        Assert.Equal(HttpStatusCode.NotFound, foreignRenew.StatusCode);
        using var secondTrust = federation.TrustAuthority(new string('l', 32), ["cn-324"]);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('l', 32));
        using var otherAuthorityImport = await http.PostAsJsonAsync(CompanionFederation.Prefix + "launches", identity);
        Assert.Equal(HttpStatusCode.Conflict, otherAuthorityImport.StatusCode);
        using var otherAuthorityRenew = await http.PostAsJsonAsync(CompanionFederation.Prefix + "tickets", identity);
        Assert.Equal(HttpStatusCode.NotFound, otherAuthorityRenew.StatusCode);
        using var otherAuthorityStatus = await http.PostAsJsonAsync(CompanionFederation.Prefix + "status", identity);
        Assert.Equal(HttpStatusCode.NotFound, otherAuthorityStatus.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('k', 32));
        trust.Dispose();
        using var revoked = await http.PostAsJsonAsync(CompanionFederation.Prefix + "tickets", identity);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    private static async Task<string?> ClosedReason(Client client)
    {
        await client.ReceiveCloseAsync();
        return client.CloseDescription;
    }
}
