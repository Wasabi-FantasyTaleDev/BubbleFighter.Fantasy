using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Archives;
using BubbleFighter.Fantasy.Admin.Http;
using BubbleFighter.Fantasy.Contracts.Installation;
using BubbleFighter.Fantasy.Installation;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.MySql;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

/// <summary>Exercises durable claims and account recovery through the real HTTP, installation and SQLite rows.</summary>
public sealed class InstallationEndpointTests
{
    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("Forwarded")]
    public async Task UnclaimedSetupRefusesForwardingHeaders(string header)
    {
        await using var fixture = await Fixture.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await fixture.Http.GetAsync("/api/setup/status")).StatusCode);
        fixture.Http.DefaultRequestHeaders.Add(header, "proxy-fixture");
        foreach (var path in new[] { "/api/setup/status", "/api/setup/configuration" })
        {
            using var response = await fixture.Http.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                $"{header} {path}: {response.StatusCode}; {await response.Content.ReadAsStringAsync()}");
            Assert.Equal("setup_proxy_refused", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        using var rejected = await fixture.PostAsync("/api/setup/probe", new InstallationSettings { PlayerLogin = "open" });
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        fixture.Http.DefaultRequestHeaders.Remove(header);
        Assert.False((await fixture.GetStatusAsync()).HasAdministrator);
        await fixture.ClaimAsync(new() { PlayerLogin = "open" });
        await fixture.SignInAsync();
        fixture.Http.DefaultRequestHeaders.Add(header, "proxy-fixture");
        Assert.Equal(HttpStatusCode.OK, (await fixture.Http.GetAsync("/api/setup/status")).StatusCode);
    }

    [Fact]
    public async Task ClientFolderAndPackageProduceIndependentDataRoots()
    {
        await using var fixture = await Fixture.StartAsync();
        using var folder = new MultipartFormDataContent();
        folder.Add(new StringContent("folder-data"), "files", "client-data/fixture-build/dataraw/Config/fixture.xml");
        var response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=folder", folder);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var firstRoot = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        var firstFile = Path.Combine(firstRoot, "fixture-build", "dataraw", "Config", "fixture.xml");
        Assert.Equal("folder-data", await File.ReadAllTextAsync(firstFile));

        using var package = ZipForm(("client-data/fixture-build/dataraw/Config/fixture.xml", "replacement"),
            ("client-data/fixture-other/dataraw/Config/fixture.xml", "other version"));
        response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=zip", package);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var secondRoot = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.Equal("folder-data", await File.ReadAllTextAsync(firstFile));
        Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(secondRoot, "fixture-build", "dataraw", "Config", "fixture.xml")));
        Assert.False(System.IO.Directory.Exists(Path.Combine(secondRoot, "fixture-other")));
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.GetDirectoryName(secondRoot)!, ".pending-*"));
        using var clientFolder = new MultipartFormDataContent();
        clientFolder.Add(new ByteArrayContent(ClientPack(("Config/ShopItem2.xml", "shop"))), "files", "Client/Packs/Config_00000.pkn");
        response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=folder", clientFolder);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var clientRoot = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        var generatedPack = Path.Combine(clientRoot, "fixture-build", "data", "Packs", "Config_00000.pkn");
        using var reader = Archive.Open(generatedPack, "Packs/Config_00000.pkn");
        Assert.Equal("shop", Encoding.UTF8.GetString(reader.Read("Config/ShopItem2.xml")));
        Assert.Equal(Path.Combine(fixture.Directory, "client-data"), clientRoot);
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.GetDirectoryName(secondRoot)!, ".pending-*"));
    }

    [Fact]
    public async Task ClientPackageRejectsAmbiguousPathsAndRequiresRequestGuard()
    {
        await using var fixture = await Fixture.StartAsync();
        foreach (var paths in new[]
        {
            new[] { ("fixture-build/dataraw/../outside.xml", "invalid") },
            new[] { ("fixture-build/dataraw/file.xml", "one"), ("fixture-build/dataraw/FILE.xml", "two") },
            new[] { ("fixture-other/dataraw/file.xml", "wrong version") },
        })
        {
            using var package = ZipForm(paths);
            var response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=zip", package);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "administration", "client-imports")));
        fixture.Http.DefaultRequestHeaders.Remove("X-BFF-Request");
        using var blocked = ZipForm(("fixture-build/dataraw/file.xml", "blocked"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=zip", blocked)).StatusCode);
        fixture.Http.DefaultRequestHeaders.Add("X-BFF-Request", "1");
        await fixture.ClaimAsync(new() { PlayerLogin = "open" });
        using var signedOut = ZipForm(("fixture-build/dataraw/file.xml", "blocked"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=zip", signedOut)).StatusCode);
    }

    [Fact]
    public async Task ClientImportLimitsCleanStagingAndPermitASubsequentImport()
    {
        await using var fixture = await Fixture.StartAsync(new() { ["client_data_extracted_bytes"] = 1048576 });
        using var oversized = ZipForm(("fixture-build/dataraw/large.xml", new string('x', 1048577)));
        var response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=zip", oversized);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("client_data_limit", await response.Content.ReadAsStringAsync());
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "administration", "client-imports")));
        using var valid = ZipForm(("fixture-build/dataraw/설정/測試.xml", "Unicode names remain intact."));
        response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=zip", valid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        Assert.True(File.Exists(Path.Combine(root, "fixture-build", "dataraw", "설정", "測試.xml")));
    }

    [Fact]
    public async Task GameFolderExtractsSelectedFilesAndRejectsMissingRequiredFilesWithoutPublishing()
    {
        await using var fixture = await Fixture.StartAsync();
        using var missing = new MultipartFormDataContent();
        missing.Add(new ByteArrayContent(ClientPack(("Maps/Example.xml", "map"))), "files", "Client/Packs/Config_00000.pkn");
        var response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=folder", missing);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Config/ShopItem2.xml", await response.Content.ReadAsStringAsync());
        var output = Path.Combine(fixture.Directory, "client-data", "fixture-build");
        Assert.False(System.IO.Directory.Exists(output));
        using var valid = new MultipartFormDataContent();
        valid.Add(new ByteArrayContent(ClientPack(("Config/ShopItem2.xml", "shop"), ("Maps/Example.xml", "map"),
            ("Textures/Example.dds", "unused"))), "files", "Client/Packs/Config_00000.pkn");
        response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=folder", valid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("map", await File.ReadAllTextAsync(Path.Combine(output, "dataraw", "Maps", "Example.xml")));
        Assert.False(System.IO.Directory.Exists(Path.Combine(output, "data")));
        Assert.False(System.IO.Directory.Exists(Path.Combine(output, "dataraw", "Textures")));
        using var replacement = new MultipartFormDataContent();
        replacement.Add(new ByteArrayContent(ClientPack(("Config/ShopItem2.xml", "replacement"))), "files", "Client/Packs/Config_00000.pkn");
        response = await fixture.Http.PostAsync("/api/setup/client-data?profile=fixture-build&kind=folder", replacement);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("client_data_exists", await response.Content.ReadAsStringAsync());
        Assert.Equal("shop", await File.ReadAllTextAsync(Path.Combine(output, "dataraw", "Config", "ShopItem2.xml")));
    }

    private static byte[] ClientPack(params (string Path, string Text)[] files)
    {
        using var stream = new MemoryStream();
        PknWriter.Write(stream, "packs/config_00000.pkn", files.Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Text))));
        return stream.ToArray();
    }

    private static MultipartFormDataContent ZipForm(params (string Path, string Text)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open());
                writer.Write(file.Text);
            }
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(memory.ToArray()), "files", "client-data.zip");
        return form;
    }

    private const string Username = "관리자_测试";
    private const string Password = "Fixture-only-管理-12345";

    [Theory]
    [InlineData(1024, HttpStatusCode.TooManyRequests)]
    [InlineData(1, HttpStatusCode.OK)]
    public async Task LoginLockoutUsesPeerAddressAndEvictsOldestWindow(int capacity, HttpStatusCode firstPeerStatus)
    {
        await using var fixture = await Fixture.StartAsync(new() { ["login_address_capacity"] = capacity });
        await fixture.ClaimAsync(new() { PlayerLogin = "open" });
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/auth/login",
                new { username = Username, password = "incorrect" })).StatusCode);
        fixture.Http.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.2");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await fixture.PostAsync("/api/auth/login",
            new { username = Username, password = Password })).StatusCode);

        using var other = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        }) { BaseAddress = fixture.Http.BaseAddress };
        other.DefaultRequestHeaders.Add("X-BFF-Request", "1");
        using var allowed = await other.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var first = await fixture.PostAsync("/api/auth/login", new { username = Username, password = Password });
        Assert.Equal(firstPeerStatus, first.StatusCode);
    }

    [Fact]
    public async Task MultipleClientsSurviveRestartAndDeferredImportPreservesDatabase()
    {
        await using var fixture = await Fixture.StartAsync();
        var settings = new InstallationSettings { PlayerLogin = "open", Clients = [
            new("fixture-build", fixture.Directory, false), new("fixture-other", "", true)],
            SqlitePath = Path.Combine(fixture.Directory, "multi.db") };
        await fixture.ClaimAsync(settings);
        await fixture.SignInAsync();
        Assert.True((await fixture.WaitForAsync("ready")).ClientDataDeferred);
        await fixture.RestartAsync();
        await fixture.SignInAsync();
        await fixture.WaitForAsync("ready");
        var saved = await fixture.Http.GetFromJsonAsync<InstallationConfiguration>("/api/setup/configuration");
        Assert.Equal(settings.Clients, saved!.Settings.Clients);
        var updated = saved.Settings with { Clients = [settings.Clients[0], new("fixture-other", fixture.Directory, false)] };
        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/api/setup/configuration")
        { Content = JsonContent.Create(new InstallationUpdate(updated with { SqlitePath = Path.Combine(fixture.Directory, "wrong.db") }, false)) };
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Http.SendAsync(invalid)).StatusCode);
        using var valid = new HttpRequestMessage(HttpMethod.Put, "/api/setup/configuration")
        { Content = JsonContent.Create(new InstallationUpdate(updated, false)) };
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Http.SendAsync(valid)).StatusCode);
        Assert.False((await fixture.WaitForAsync("ready")).ClientDataDeferred);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "wrong.db")));
    }

    [Fact]
    public async Task InvalidClientSelectionsDoNotClaimAnAdministrator()
    {
        await using var fixture = await Fixture.StartAsync();
        InstallationClient ready = new("fixture-build", fixture.Directory, false);
        IReadOnlyList<InstallationClient>[] invalid = [[], [ready, ready], [new("management", "", true)],
            [new("missing", "", true)], [new("fixture-build", "relative", false)], [new("fixture-build", fixture.Directory, true)],
            [new("fixture-preparing", "", true)], [new("fixture-inactive", "", true)]];
        foreach (var clients in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/setup/probe", new InstallationSettings { PlayerLogin = "open", Clients = clients })).StatusCode);
        Assert.False((await fixture.GetStatusAsync()).HasAdministrator);
    }

    [Fact]
    public async Task InstallationIgnoresBearerHeadersAndAcceptsOnlyAccountSignIn()
    {
        var token = new string('t', 40);
        await using var fixture = await Fixture.StartAsync();
        await fixture.ClaimAsync(new InstallationSettings { PlayerLogin = "open" });
        fixture.Http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/setup/directories", new { path = fixture.Directory })).StatusCode);
        Assert.False((await fixture.Http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/status")).GetProperty("authenticated").GetBoolean());
        await fixture.SignInAsync();
        await fixture.WaitForAsync("ready");
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync("/api/setup/directories", new { path = fixture.Directory })).StatusCode);
    }

    [Fact]
    public async Task PlayerSignInModeAndAddressAreValidated()
    {
        await using var fixture = await Fixture.StartAsync();
        InstallationSettings[] invalid =
        [
            new() { PlayerLogin = "launcher", PublicHost = "bad host" },
            new() { PlayerLogin = "lan", PublicHost = "127.0.0.1" },
        ];
        foreach (var settings in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/setup/probe", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync("/api/setup/probe",
            new InstallationSettings { PlayerLogin = "launcher", PublicHost = "play.example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync("/api/setup/probe", new InstallationSettings { PlayerLogin = "launcher" })).StatusCode);
        Assert.False((await fixture.GetStatusAsync()).HasAdministrator);
    }

    [Fact]
    public async Task FolderPickerIsBoundedAndUsesTheSetupAndSessionGuards()
    {
        await using var fixture = await Fixture.StartAsync();
        var root = Path.Combine(fixture.Directory, "folders");
        for (var i = 0; i < 105; i++) Directory.CreateDirectory(Path.Combine(root, "folder-" + i));
        File.WriteAllText(Path.Combine(root, "private.txt"), "not listed");
        var response = await fixture.PostAsync("/api/setup/directories", new { path = root });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<InstallationDirectoryPage>();
        Assert.Equal(root, page!.Path);
        Assert.True(page.Truncated);
        Assert.Equal(100, page.Directories.Count);
        Assert.DoesNotContain(page.Directories, d => d.Name == "private.txt");
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/setup/directories", new { path = "../relative" })).StatusCode);
        fixture.Http.DefaultRequestHeaders.Remove("X-BFF-Request");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostAsync("/api/setup/directories", new { path = root })).StatusCode);
        fixture.Http.DefaultRequestHeaders.Add("X-BFF-Request", "1");
        await fixture.ClaimAsync(new InstallationSettings { PlayerLogin = "open" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/setup/directories", new { path = root })).StatusCode);
        await fixture.SignInAsync();
        await fixture.WaitForAsync("ready");
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync("/api/setup/directories", new { path = root })).StatusCode);
    }

    [Fact]
    public async Task DeferredClientImportRetainsBuildAndStorageAcrossRestart()
    {
        await using var fixture = await Fixture.StartAsync();
        var settings = new InstallationSettings { PlayerLogin = "open", Profile = "fixture-build", DeferClientDataImport = true,
            SqlitePath = Path.Combine(fixture.Directory, "retained.db") };
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync("/api/setup/probe", settings)).StatusCode);
        await fixture.ClaimAsync(settings);
        await fixture.SignInAsync();
        var status = await fixture.WaitForAsync("ready");
        Assert.True(status.ClientDataDeferred);
        Assert.Equal("fixture-build", status.Profile);
        await fixture.RestartAsync();
        await fixture.SignInAsync();
        Assert.True((await fixture.WaitForAsync("ready")).ClientDataDeferred);

        var imported = settings with { ClientDataPath = fixture.Directory, DeferClientDataImport = false };
        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/api/setup/configuration")
        { Content = JsonContent.Create(new InstallationUpdate(imported with { SqlitePath = Path.Combine(fixture.Directory, "different.db") }, false)) };
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Http.SendAsync(invalid)).StatusCode);
        Assert.True((await fixture.GetStatusAsync()).ClientDataDeferred);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "different.db")));

        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/setup/configuration")
        { Content = JsonContent.Create(new InstallationUpdate(imported, false)) };
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Http.SendAsync(update)).StatusCode);
        Assert.False((await fixture.WaitForAsync("ready")).ClientDataDeferred);
        var retained = await fixture.Http.GetFromJsonAsync<InstallationConfiguration>("/api/setup/configuration");
        Assert.Equal(settings.SqlitePath, retained!.Settings.SqlitePath);
        Assert.Equal(imported.ClientDataPath, retained.Settings.ClientDataPath);
    }

    [Fact]
    public async Task MissingClientDataMustBeExplicitlyDeferred()
    {
        await using var fixture = await Fixture.StartAsync();
        var candidate = new InstallationSettings { PlayerLogin = "open", Profile = "fixture-build" };
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/setup/probe", candidate)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/setup/probe",
            candidate with { DeferClientDataImport = true, ClientDataPath = fixture.Directory })).StatusCode);
        Assert.False((await fixture.GetStatusAsync()).HasAdministrator);
    }

    [Fact]
    public async Task SqliteInstallPersistsAndRestartRequiresANewSession()
    {
        await using var fixture = await Fixture.StartAsync();
        var status = await fixture.GetStatusAsync();
        Assert.Equal("language", status.Phase);
        Assert.False(status.HasAdministrator);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Http.GetAsync("/api/plugins")).StatusCode);
        var settings = new InstallationSettings { PlayerLogin = "open", Language = "zh-Hant", ServerName = "多語言 붐힐", SqlitePath = status.DefaultSqlitePath };
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync("/api/setup/probe", settings)).StatusCode);
        await fixture.ClaimAsync(settings);
        await fixture.SignInAsync();
        status = await fixture.WaitForAsync("ready");
        Assert.True(status.Installed);
        Assert.Equal(settings.ServerName, status.ServerName);
        await using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = settings.SqlitePath, Pooling = false }.ConnectionString))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";
            Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) > 3);
        }
        var state = await File.ReadAllTextAsync(fixture.StatePath);
        Assert.DoesNotContain(Password, state);
        Assert.DoesNotContain("bff_admin_session", state);
        var oldCookie = fixture.Cookies.GetCookieHeader(new Uri(fixture.Http.BaseAddress!, "/api/setup/status"));
        Assert.NotEmpty(oldCookie);
        await fixture.RestartAsync();
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
        stale.Headers.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Http.SendAsync(stale)).StatusCode);
        await fixture.SignInAsync();
        Assert.True((await fixture.WaitForAsync("ready")).Installed);
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostAsync("/api/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Http.GetAsync("/api/setup/status")).StatusCode);
    }

    [Fact]
    public async Task FailedDatabaseKeepsAdministratorAndCanBeCorrectedAfterRestart()
    {
        await using var fixture = await Fixture.StartAsync();
        var directoryAsDatabase = Path.Combine(fixture.Directory, "not-a-database");
        Directory.CreateDirectory(directoryAsDatabase);
        await fixture.ClaimAsync(new InstallationSettings { PlayerLogin = "open", SqlitePath = directoryAsDatabase });
        await fixture.SignInAsync();
        var failed = await fixture.WaitForAsync("failed");
        Assert.True(failed.HasAdministrator);
        Assert.False(failed.Installed);
        Assert.Equal("database_failed", failed.ErrorCode);
        await fixture.RestartAsync();
        await fixture.SignInAsync();
        await fixture.WaitForAsync("failed");
        var replacement = new InstallationSettings { PlayerLogin = "open", SqlitePath = Path.Combine(fixture.Directory, "corrected.db") };
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/setup/configuration")
        { Content = JsonContent.Create(new InstallationUpdate(replacement, false)) };
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Http.SendAsync(update)).StatusCode);
        Assert.True((await fixture.WaitForAsync("ready")).Installed);
    }

    [Fact]
    public async Task InstalledDatabaseFailureDoesNotReopenAnUnclaimedInstallation()
    {
        await using var fixture = await Fixture.StartAsync();
        var settings = new InstallationSettings { PlayerLogin = "open", SqlitePath = Path.Combine(fixture.Directory, "save.db") };
        await fixture.ClaimAsync(settings);
        await fixture.SignInAsync();
        await fixture.WaitForAsync("ready");
        await fixture.StopAsync();
        File.Delete(settings.SqlitePath);
        Directory.CreateDirectory(settings.SqlitePath);
        await fixture.RestartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Http.GetAsync("/api/setup/status")).StatusCode);
        await fixture.SignInAsync();
        var failed = await fixture.WaitForAsync("failed");
        Assert.True(failed.Installed);
        Assert.True(failed.HasAdministrator);
        Assert.Equal("database_failed", failed.ErrorCode);
    }

    [Fact]
    public async Task MutationsRequireSameOriginGuardAndHostIsChecked()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Http.DefaultRequestHeaders.Remove("X-BFF-Request");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostAsync("/api/setup/probe", new InstallationSettings { PlayerLogin = "open" })).StatusCode);
        fixture.Http.DefaultRequestHeaders.Add("X-BFF-Request", "1");
        fixture.Http.DefaultRequestHeaders.Add("Origin", "https://unrelated.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostAsync("/api/setup/probe", new InstallationSettings { PlayerLogin = "open" })).StatusCode);
        fixture.Http.DefaultRequestHeaders.Remove("Origin");
        fixture.Http.DefaultRequestHeaders.Host = "unrelated.invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Http.GetAsync("/api/auth/status")).StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"settings\":null}")]
    [InlineData("{\"settings\":{\"language\":null}}")]
    [InlineData("{")]
    public async Task InvalidClaimsNeverReserveTheAdministrator(string json)
    {
        await using var fixture = await Fixture.StartAsync();
        var response = await fixture.Http.PostAsync("/api/setup/complete", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await fixture.GetStatusAsync()).HasAdministrator);
    }

    [Fact]
    public async Task ClaimIsExclusiveAndLoginFailuresAreBounded()
    {
        await using var fixture = await Fixture.StartAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => fixture.PostAsync("/api/setup/complete",
            new InstallationRequest(Guid.NewGuid(), new() { PlayerLogin = "open" }, Username, Password))));
        Assert.Single(claims, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.All(claims.Where(r => r.StatusCode != HttpStatusCode.Accepted), r => Assert.Contains(r.StatusCode,
            new[] { HttpStatusCode.Conflict, HttpStatusCode.Unauthorized }));
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/auth/login", new { username = Username, password = "incorrect" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await fixture.PostAsync("/api/auth/login", new { username = Username, password = Password })).StatusCode);
    }

    [Fact]
    public async Task SavedMySqlPasswordIsRedactedAndCannotBeRetargeted()
    {
        await using var fixture = await Fixture.StartAsync();
        var settings = new InstallationSettings { PlayerLogin = "open", DatabaseKind = "mysql", MySqlPort = 1, MySqlPassword = "only-a-fixture-secret", MySqlSslMode = "None" };
        await fixture.ClaimAsync(settings);
        await fixture.SignInAsync();
        await fixture.WaitForAsync("failed");
        var config = await fixture.Http.GetFromJsonAsync<InstallationConfiguration>("/api/setup/configuration");
        Assert.NotNull(config);
        Assert.True(config.HasDatabasePassword);
        Assert.Empty(config.Settings.MySqlPassword);
        var changed = new InstallationUpdate(config.Settings with { MySqlHost = "192.0.2.1" }, true);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/setup/configuration/probe", changed)).StatusCode);
        var retained = await fixture.PostAsync("/api/setup/configuration/probe", new InstallationUpdate(config.Settings, true));
        Assert.Equal(HttpStatusCode.Conflict, retained.StatusCode);
        Assert.DoesNotContain(settings.MySqlPassword, await retained.Content.ReadAsStringAsync());
    }

    [Plugin("test.installation-runtime", Provides = [typeof(IInstallationRuntime)])]
    public sealed class Runtime : IPlugin, IInstallationRuntime
    {
        public IReadOnlyList<InstallationProfile> Profiles { get; } = [new("management", "Fixture"), new("fixture-build", "Fixture client"), new("fixture-other", "Another client"),
            new("fixture-preparing", "Hidden from setup") { SetupEnabled = false },
            new("fixture-inactive", "Cannot activate") { ActivationEnabled = false }];
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<IInstallationRuntime>(this));
            return ValueTask.CompletedTask;
        }
        public Task WaitForBootstrapAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask ApplyAsync(InstallationSettings settings, IReadOnlyDictionary<string, object?> databaseConfig,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private PluginHost? _host;
        private Dictionary<string, object?> _installationOptions = [];
        private Dictionary<string, object?> _adminOptions = [];
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bff-installation-tests", Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(Directory, "administration", "installation.json");
        public HttpClient Http { get; private set; } = null!;
        public CookieContainer Cookies { get; private set; } = null!;

        public static async Task<Fixture> StartAsync(Dictionary<string, object?>? installationOptions = null,
            Dictionary<string, object?>? adminOptions = null)
        {
            var fixture = new Fixture();
            fixture._installationOptions = installationOptions ?? [];
            fixture._adminOptions = adminOptions ?? [];
            await fixture.RestartAsync();
            return fixture;
        }

        public async Task RestartAsync()
        {
            await StopAsync();
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            PluginRow Row(string plugin, Dictionary<string, object?>? config = null) => new() { Id = plugin, Plugin = plugin, Source = "test", Config = config };
            var catalog = PluginCatalog.ScanAssemblies([typeof(PluginHost).Assembly, typeof(AdminHttpPlugin).Assembly,
                typeof(WebUiPlugin).Assembly, typeof(InstallationPlugin).Assembly, typeof(SqliteSetupPlugin).Assembly,
                typeof(MySqlSetupPlugin).Assembly, typeof(InstallationEndpointTests).Assembly]);
            var installationConfig = new Dictionary<string, object?>(_installationOptions)
            { ["state_directory"] = Directory, ["database_timeout_seconds"] = 3, ["login_attempts"] = 2 };
            _host = new PluginHost([
                Row("test.installation-runtime"), Row("persistence.sqlite.setup"),
                Row("persistence.mysql.setup", new() { ["connect_timeout_seconds"] = 1 }),
                Row("installation", installationConfig),
                Row("webui"), Row("admin.http", new Dictionary<string, object?>(_adminOptions) { ["listen"] = $"127.0.0.1:{port}", ["log_buffer"] = 32 }),
            ], catalog, NullLoggerFactory.Instance);
            await _host.StartAsync(CancellationToken.None);
            Cookies = new CookieContainer();
            Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Http.DefaultRequestHeaders.Add("X-BFF-Request", "1");
        }

        public Task<HttpResponseMessage> PostAsync<T>(string path, T value) => Http.PostAsJsonAsync(path, value);
        public async Task<InstallationStatus> GetStatusAsync() => (await Http.GetFromJsonAsync<InstallationStatus>("/api/setup/status"))!;
        public async Task ClaimAsync(InstallationSettings settings) => Assert.Equal(HttpStatusCode.Accepted,
            (await PostAsync("/api/setup/complete", new InstallationRequest(Guid.NewGuid(), settings, Username, Password))).StatusCode);
        public async Task SignInAsync()
        {
            var response = await PostAsync("/api/auth/login", new { username = Username, password = Password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        }
        public async Task<InstallationStatus> WaitForAsync(string phase)
        {
            var timer = Stopwatch.StartNew();
            InstallationStatus status;
            do
            {
                status = await GetStatusAsync();
                if (status.Phase == phase) return status;
                await Task.Delay(25);
            } while (timer.Elapsed < TimeSpan.FromSeconds(15));
            Assert.Fail($"Expected {phase}; observed {status.Phase}: {status.ErrorCode}");
            return status;
        }
        public async Task StopAsync()
        {
            Http?.Dispose();
            if (_host is null) return;
            await _host.ShutdownAsync(CancellationToken.None);
            _host = null;
        }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
