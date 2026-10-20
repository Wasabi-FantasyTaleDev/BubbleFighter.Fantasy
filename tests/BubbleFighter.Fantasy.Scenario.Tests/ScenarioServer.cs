using BubbleFighter.Fantasy.Tests;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// A kr-1256 regression row set started in process on free
/// loopback ports, with in-memory persistence and fixture game data in place of MySQL and the
/// unpacked client files. Scenarios drive it with simulated clients over real TCP.
/// </summary>
internal sealed class ScenarioServer : IAsyncDisposable
{
    private readonly PluginHost _host;
    private readonly List<SimClient> _clients = [];
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    private ScenarioServer(PluginHost host, int gamePort, int relayPort)
    {
        _host = host;
        GamePort = gamePort;
        RelayPort = relayPort;
    }

    public int GamePort { get; }

    /// <summary>Gets the match relay listener's port.</summary>
    public int RelayPort { get; }

    public CancellationToken Ct => _cts.Token;

    public static readonly SimFixtures Fixtures =
        SimFixtures.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "client-requests.jsonl"));

    public static Task<ScenarioServer> StartAsync() => StartAsync(Rows);

    /// <summary>
    /// Starts a server whose rows come from <paramref name="rows"/>, called with the free game and relay ports;
    /// scenarios that need other option values than <see cref="Rows"/> build their own list from it.
    /// </summary>
    public static async Task<ScenarioServer> StartAsync(Func<int, int, List<PluginRow>> rows)
    {
        var (game, relay) = FreePorts();
        var host = new PluginHost(rows(game, relay),
            PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try { await host.StartAsync(CancellationToken.None); }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Scenario listener startup failed (game port {game}, relay port {relay}).", error);
        }
        return new ScenarioServer(host, game, relay);
    }

    /// <summary>Connects a client whose launcher-login session is <paramref name="name"/>.</summary>
    public async Task<SimActor> ConnectAsync(string name)
    {
        var client = await SimClient.ConnectAsync(name, new DnsEndPoint("127.0.0.1", GamePort),
            TestWireDialects.Get("kr-1256"), 4 * 1024 * 1024, Ct);
        _clients.Add(client);
        return new SimActor(client, Fixtures, TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>Opens a match relay connection for the client named <paramref name="name"/>.</summary>
    public async Task<SimActor> ConnectRelayAsync(string name)
    {
        var client = await SimClient.ConnectAsync(name + "-relay", new DnsEndPoint("127.0.0.1", RelayPort),
            TestWireDialects.Get("kr-1256"), 4 * 1024 * 1024, Ct);
        _clients.Add(client);
        return new SimActor(client, Fixtures, TimeSpan.FromSeconds(10), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await _host.ShutdownAsync(CancellationToken.None);
        _cts.Dispose();
    }

    internal static List<PluginRow> Rows(int gamePort, int relayPort) =>
    [
        Row("core.protocol-registry", "core.protocol-registry"),
        Row("core.session-registry", "core.session-registry"),
        Row("core.packet-router", "core.packet-router"),
        Row("core.message-router", "core.message-router"),
        Row("core.client-profiles", "core.client-profiles"),
        Row("core.opcode-tables", "core.opcode-tables"),
        Row("core.wire-dialects", "core.wire-dialects"),
        Row("persistence.memory", "persistence.memory"),
        Row("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
        Row("dialect.modern.messages", "dialect.modern.messages", TestWireDialects.ModernMessagesConfig()),
        SharedDialectRows.Kr1256(new() { ["inventory"] = true }),
        Row("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml") }),
        // The build's client data: a loose-file tree holding only the synthetic shop workbook. The store is
        // visible to the shop row alone because a visible store makes the in-game row load its map pools
        // (ServerShared map lists) from it, which this test tree does not carry; the scenarios keep the in-game
        // row on its shipped map keys, as they did before the workbook moved into client data.
        Row("clientdata", "clientdata", TestClientData.RowConfig(TestClientData.SyntheticShop()),
            isolatedServices: ["BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore"]),
        Row("game-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{gamePort}", ["protocol"] = "kr-1256/game" }),
        Row("relay-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{relayPort}", ["protocol"] = "kr-1256/relay" }),
        Row("lobby", "lobby", new()
        {
            ["protocol"] = "kr-1256/game", ["auto_create_players"] = true, ["fallback_username"] = "guest",
            // The store-backed resolvers, as the MySQL deployment configures them (the value names the resolver, not a database).
            ["inventory_source"] = "mysql", ["money_source"] = "mysql",
        }),
        Row("equipment", "equipment", new() { ["protocol"] = "kr-1256/game", ["client_profile"] = "kr-1256", ["version_major"] = 1256 }),
        Row("inventoryview", "inventoryview", new()
        {
            ["protocol"] = "kr-1256/game", ["client_profile"] = "kr-1256", ["version_major"] = 1256,
            ["enabled"] = true, ["publish_equipped_view_push"] = true,
        }),
        Row("match-connection", "match-connection", new() { ["protocol"] = "kr-1256/relay" }),
        // Explicit test settings keep scenarios independent of deployment-local overrides.
        Row("ingame", "ingame", new()
        {
            ["game_protocol"] = "kr-1256/game", ["relay_protocol"] = "kr-1256/relay", ["version_major"] = 1256,
            ["relay_advertise_ipv4"] = "127.0.0.1", ["relay_advertise_port"] = relayPort,
            ["republish_full_room_on_team_change"] = false,
            ["relay_character_positions"] = true, ["start_matches"] = true,
            ["send_stage_seat_records"] = false, ["send_waiting_dialog"] = false, ["send_in_session_spawn"] = false,
            ["send_match_arena_block"] = false, ["start_flag0"] = 1,
            ["send_room_list"] = true, ["republish_room_ui_on_position"] = true, ["republish_room_ui_on_join"] = true,
            ["broadcast_room_chat"] = true, ["send_plaza_snapshot"] = true, ["enter_game_stage_on_create"] = false,
            ["room_state"] = 2, ["mode_override"] = 0, ["prime_session_state_before_install"] = true,
        }),
        Row("match-relay", "match-relay", new()
        {
            ["game_protocol"] = "kr-1256/game", ["relay_protocol"] = "kr-1256/relay", ["broadcast_movement_samples"] = true,
        }),
        Row("shop", "shop", new()
        {
            ["protocol"] = "kr-1256/game", ["client_profile"] = "kr-1256", ["version_major"] = 1256,
            ["shop_item_client_path"] = "Config/ShopItem2.xml",
        }, parentId: "clientdata"),
    ];

    internal static PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null,
        string? parentId = null, string[]? isolatedServices = null) =>
        new()
        {
            Id = id, Plugin = plugin, Source = "scenario-test", ClientBuild = new("kr", 1256, 1304), Family = "modern",
            Config = config, ParentId = parentId, IsolatedServices = isolatedServices ?? [],
        };

    private static (int Game, int Relay) FreePorts()
    {
        using var game = new TcpListener(IPAddress.Loopback, 0);
        using var relay = new TcpListener(IPAddress.Loopback, 0);
        game.Start();
        relay.Start();
        // Keep both probes bound so the OS cannot choose the game port again for relay.
        return (((IPEndPoint)game.LocalEndpoint).Port, ((IPEndPoint)relay.LocalEndpoint).Port);
    }
}
