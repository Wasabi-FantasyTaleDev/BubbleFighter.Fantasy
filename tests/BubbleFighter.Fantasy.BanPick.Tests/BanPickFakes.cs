using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging.Abstractions;

namespace BubbleFighter.Fantasy.BanPick.Tests;

internal sealed class FakeSession(long id) : ISession
{
    public long Id => id;
    public IConnection Connection { get; } = new FakeConnection(id);
    public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "kr-812");
    public ConcurrentDictionary<string, object> Items { get; } = new();
    public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;

    private sealed class FakeConnection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}

/// <summary>Keys and arguments instead of translations, so tests read what was said, not how.</summary>
internal sealed class KeyTexts : IGameText
{
    public string Format(string region, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
        arguments is null || arguments.Count == 0 ? key : key + "|" + string.Join("|", arguments.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));

    public bool TryFormat(string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null)
    {
        text = Format(region, key, arguments);
        return true;
    }
}

internal sealed class RecordingNotices : IMessageSender<ServerNotice>
{
    public string Protocol => "kr-812/game";
    public List<(long Session, string Text)> Sent { get; } = [];
    internal Func<ValueTask>? AfterSend { get; set; }
    public ValueTask SendAsync(ISession session, ServerNotice message, CancellationToken ct)
    {
        Sent.Add((session.Id, message.Text));
        return AfterSend?.Invoke() ?? ValueTask.CompletedTask;
    }
    public IEnumerable<string> To(long session) => Sent.Where(entry => entry.Session == session).Select(entry => entry.Text);
}

internal sealed class RecordingChat : IMessageSender<RoomChatReceived>
{
    public string Protocol => "kr-812/game";
    public List<(long Session, RoomChatReceived Line)> Sent { get; } = [];
    public ValueTask SendAsync(ISession session, RoomChatReceived message, CancellationToken ct)
    {
        Sent.Add((session.Id, message));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeRooms : IRoomPolicyHost
{
    public string Protocol => "kr-812/game";
    public string ClientBuild => "kr-812";
    public RoomPolicyRoom Room { get; set; } = null!;
    public Dictionary<long, FakeSession> Sessions { get; } = [];
    public List<(Guid Instance, long Host)> Resumed { get; } = [];
    public IDisposable Register(IRoomPolicy policy) => throw new NotSupportedException();
    public RoomPolicyRoom? Find(long sessionId) => Room.Seats.Any(seat => seat.SessionId == sessionId) ? Room : null;
    public ISession? Session(long sessionId) => Sessions.GetValueOrDefault(sessionId);
    public ValueTask<bool> ResumeHeldStartAsync(Guid instance, long requesterSessionId, CancellationToken ct)
    {
        Resumed.Add((instance, requesterSessionId));
        return ValueTask.FromResult(true);
    }
}

internal sealed class FakeCompanion(params long[] supported) : ICompanionHub
{
    public List<(long Session, string Type, string Json)> Sent { get; } = [];
    public IDisposable Route(string type, int version, CompanionHandler handler) => throw new NotSupportedException();
    public IDisposable Offer(string type, int version) => throw new NotSupportedException();
    public bool Supports(ISession session, string type, int version) => supported.Contains(session.Id);
    public bool Send(ISession session, string type, int version, object? payload)
    {
        Sent.Add((session.Id, type, JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })));
        return true;
    }
    public IReadOnlyList<ISession> BoundSessions() => [];
}

internal sealed class FakeOnline(Dictionary<int, ISession> players) : IOnlinePlayers
{
    public ISession? FindByUserNum(int userNum) => players.GetValueOrDefault(userNum);
    public ISession? FindByNickname(string nickname) => null;
}

internal sealed class ManualClock : TimeProvider
{
    public long Now { get; set; } = 1_000_000;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Now;
}

internal sealed class FakeHub(BanPickSettings settings, IBanPickPolicyStore store, ICompanionHub? companion) : IBanPickHub
{
    internal static BanPickSettings Defaults { get; } = new(25, 10_000, 30_000, 4096, 10);
    public BanPickSettings Settings => settings;
    public IBanPickPolicyStore Store => store;
    public ICompanionHub? Companion => companion;
    public IDisposable Register(IBanPickScope scope) => throw new NotSupportedException();
}

internal static class BanPickFixtures
{
    // Sniper (sub 304) 10001 and 10002, rifle (sub 302) 10003, character 300001, pet 301001.
    internal static BanPickItemCatalog Catalog() => new([
        new(10001, "Long Sniper", 300, 304, 0),
        new(10002, "Short Sniper", 300, 304, 0),
        new(10003, "Rifle A", 300, 302, 0),
        new(300001, "Dao", 400, 402, 0),
        new(301001, "Cat", 500, 504, 10001),
    ]);

    internal static BanPickGroup Sniper { get; } = new("sniper", [], [304], [], []);
    internal static BanPickGroup Pet { get; } = new("pet", [], [], [10001], []);
    internal static BanPickGroup Character { get; } = new("character", [400], [], [], []);

    internal static BanPickPolicy Policy(params BanPickPolicyEntry[] policies) => new([Sniper, Pet, Character], policies);

    internal static BanPickPolicyEntry Entry(string id, string type, BanPickScope? scope = null, int[]? items = null, string[]? groups = null) =>
        new(id, null, null, scope ?? BanPickScope.Everywhere, [new BanPickRule(type, type, items ?? [], groups ?? [])]);

    internal static RoomPolicySeat Seat(long session, int slot, string nickname, int character, bool host, params int[] items) =>
        new(session, slot, (int)session, nickname, character, items, host);

    internal static WorkbookFile Workbook(string shopItemRows, string mainRows, string subRows)
    {
        static string Sheet(string name, string rows) => $"<Worksheet ss:Name=\"{name}\"><Table>{rows}</Table></Worksheet>";
        var xml = "<?xml version=\"1.0\"?><Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" " +
            "xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">" +
            Sheet("ShopItem", shopItemRows) + Sheet("MainCategory", mainRows) + Sheet("SubCategory", subRows) + "</Workbook>";
        return WorkbookFile.FromBytes("Config/ShopItem2.xml", Encoding.UTF8.GetBytes(xml));
    }

    internal static string Row(params string[] cells) =>
        "<Row>" + string.Concat(cells.Select(cell => $"<Cell><Data ss:Type=\"String\">{cell}</Data></Cell>")) + "</Row>";

    internal static BanPickRoomRules Rules(BanPickPolicy policy, FakeRooms rooms, RecordingNotices notices, ICompanionHub? companion = null,
        ManualClock? clock = null, IOnlinePlayers? online = null, string? statePath = null)
    {
        var store = new BanPickPolicyStore(policy, statePath, 1_048_576);
        return new BanPickRoomRules(new FakeHub(FakeHub.Defaults, store, companion), rooms, Catalog(), notices, null, new KeyTexts(),
            online, clock ?? new ManualClock(), NullLogger.Instance);
    }
}
