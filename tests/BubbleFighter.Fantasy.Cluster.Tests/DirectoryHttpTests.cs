using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class DirectoryHttpTests
{
    [Theory]
    [InlineData("Upper")]
    [InlineData("under_score")]
    [InlineData("dot.name")]
    public void BackendIdentifiersMustMatchTheNativeSelectorContract(string id)
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node(id, "formal", []));
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void SelectorIdsAndLabelsFailAtConfigurationTimeInsteadOfDuringLaunch()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        options.Selectors[0].Id = 0;
        Assert.NotEmpty(options.Validate());
        options.Selectors[0].Id = 1;
        options.Selectors[0].Name = options.Selectors[1].Name;
        Assert.NotEmpty(options.Validate());
        options.Selectors[0].Name = "broken" + (char)0xD800;
        Assert.NotEmpty(options.Validate());
        options.Selectors[0].Name = "Formal automatic";
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void NestedAuthenticationMetadataBindsFromDeploymentConfiguration()
    {
        var options = new PluginConfig(new PluginRow
        {
            Id = "directory", Plugin = "cluster.directory", Source = "test",
            Config = new Dictionary<string, object?>
            {
                ["nodes"] = new object[] { new Dictionary<string, object?>
                {
                    ["id"] = "node", ["name"] = "Node", ["heartbeat_key"] = new string('a', 32),
                    ["endpoints"] = new object[] { new Dictionary<string, object?> { ["build"] = "cn-324", ["host"] = "node.example", ["port"] = 7140 } },
                    ["authentication"] = new Dictionary<string, object?> { ["authority_id"] = "formal", ["api_url"] = "https://entry.example", ["mode"] = "master-issued", ["policy_mode"] = "authority" },
                } },
                ["selectors"] = new object[] { new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Manual", ["nodes"] = new[] { "node" } } },
            },
        }).Bind<DirectoryOptions>();
        Assert.Equal("formal", Assert.Single(options.Nodes).Authentication!.AuthorityId);
        Assert.Equal("authority", options.Nodes[0].Authentication!.PolicyMode);
    }

    [Fact]
    public void AuthenticationAuthorityIsExplicitAndCannotBeInferredFromSharedData()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []));
        var directory = new ServerDirectory(options);
        Assert.Null(directory.ListServers("kr-1256")[1].Targets[0].Authentication);
        options.Nodes[0].Authentication = new() { AuthorityId = "formal", ApiUrl = "https://entry.example", Mode = "master-issued" };
        options.Nodes[1].Authentication = new() { AuthorityId = "formal", ApiUrl = "https://different.example", Mode = "master-issued" };
        Assert.NotEmpty(options.Validate());
        options.Nodes[1].Authentication = options.Nodes[0].Authentication;
        Assert.Empty(options.Validate());
        Assert.Equal("formal", directory.ListServers("kr-1256")[1].Targets[0].Authentication!.AuthorityId);
        options.Nodes[1].Authentication = new() { AuthorityId = "other", ApiUrl = "http://other.example", Mode = "own-authority" };
        Assert.Empty(options.Validate());
    }

    [Fact]
    public async Task RealHttpRequiresNodeCredentialsAndFencesReplayedReports()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        var registration = new NodeRegistration(Guid.NewGuid().ToString("N"));
        using var anonymous = await http.PostAsJsonAsync("/v1/nodes/a/register", registration);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Nodes[0].HeartbeatKey);
        using var registered = await http.PostAsJsonAsync("/v1/nodes/a/register", registration);
        registered.EnsureSuccessStatusCode();
        var grant = Assert.IsType<LeaseGrant>(await registered.Content.ReadFromJsonAsync<LeaseGrant>());
        var heartbeat = new NodeHeartbeat(grant.Token, grant.Epoch, 1, false, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", [new("kr-1256", true, 2)], []);
        using var accepted = await http.PutAsJsonAsync("/v1/nodes/a/heartbeat", heartbeat);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var replay = await http.PutAsJsonAsync("/v1/nodes/a/heartbeat", heartbeat);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var malformed = await http.PutAsync("/v1/nodes/a/heartbeat", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(2, directory.ListServers("kr-1256")[0].Targets.Single().Players);
        using var release = new HttpRequestMessage(HttpMethod.Delete, "/v1/nodes/a/lease") { Content = JsonContent.Create(new NodeRelease(grant.Token)) };
        using var released = await http.SendAsync(release);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
    }

    [Fact]
    public async Task ServerSelectorProjectionIsReadableBeforeNodeAuthentication()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };

        using var response = await http.GetAsync("/v1/servers/kr-1256");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"schema\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"selectors\"", body, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(body))
        {
            Assert.Equal(TimeSpan.Zero, document.RootElement.GetProperty("generated_at_utc").GetDateTimeOffset().Offset);
            var target = document.RootElement.GetProperty("selectors")[1].GetProperty("targets")[0];
            Assert.Equal(JsonValueKind.Null, target.GetProperty("observed_at_utc").ValueKind);
            Assert.Equal(JsonValueKind.Null, target.GetProperty("expires_at_utc").ValueKind);
            Assert.Equal(JsonValueKind.Null, target.GetProperty("status_revision").ValueKind);
        }

        using var malformed = await http.GetAsync("/v1/servers/");
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        using var invalid = await http.GetAsync("/v1/servers/%01");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task InvalidJsonAndMediaTypesCannotMutateTheDirectory()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        using var http = new HttpClient { BaseAddress = new(server.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Nodes[0].HeartbeatKey);
        var instance = Guid.NewGuid().ToString("N");
        foreach (var json in new[]
        {
            "{\"instance\":\"" + instance + "\",\"instance\":\"" + instance + "\"}",
            "{\"instance\":\"" + instance + "\",\"extra\":true}",
        })
        {
            using var invalid = await http.PostAsync("/v1/nodes/a/register", new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var text = await http.PostAsync("/v1/nodes/a/register", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        using var oversized = await http.PostAsync("/v1/nodes/a/register", new StringContent(new string(' ', options.MaxBodyBytes + 1), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var valid = await http.PostAsJsonAsync("/v1/nodes/a/register", new NodeRegistration(instance));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
    }

    [Fact]
    public void AutomaticPoolRequiresExactAuthenticationMetadata()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []));
        options.Selectors[0].Nodes = ["a", "b"];
        options.Nodes[0].Authentication = new() { AuthorityId = "formal", ApiUrl = "http://entry.example", Mode = "master-issued" };
        Assert.NotEmpty(options.Validate());
        options.Nodes[1].Authentication = options.Nodes[0].Authentication;
        Assert.Empty(options.Validate());
        options.Nodes[1].Authentication = new() { AuthorityId = "other", ApiUrl = "http://other.example", Mode = "own-authority" };
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void AutomaticPoolCannotCrossIndependentAccountDomains()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "independent", []));
        options.Selectors[0].Nodes = ["a", "b"];
        Assert.Contains(options.Validate(), error => error.ErrorMessage!.Contains("persistent data domain", StringComparison.Ordinal));
        options.Nodes[1].DataScope = "formal";
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void NodeAgentAcceptsExplicitHttpOrHttpsOriginsOnly()
    {
        var options = new NodeOptions { NodeId = "a", Builds = ["kr-1256"], HeartbeatKey = new string('a', 32), DirectoryUrl = "http://remote.example" };
        Assert.Empty(options.Validate());
        options.DirectoryUrl = "http://127.0.0.1:8092";
        Assert.Empty(options.Validate());
        options.DirectoryUrl = "https://directory.example";
        Assert.Empty(options.Validate());
        foreach (var invalid in new[] { "ftp://directory.example", "http://user@directory.example", "http://directory.example/path", "http://directory.example?key=value" })
        {
            options.DirectoryUrl = invalid;
            Assert.Contains(options.Validate(), error => error.MemberNames.Contains(nameof(NodeOptions.DirectoryUrl)));
        }
    }
}
