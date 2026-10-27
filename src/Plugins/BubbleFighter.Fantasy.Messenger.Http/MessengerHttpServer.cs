using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Messenger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Messenger.Http;

/// <summary>
/// The messenger HTTP API. Every route is <c>POST /NexonMessenger/&lt;operation&gt;</c> named after the native operation it
/// serves, with a JSON body and a JSON reply; <c>GET /NexonMessenger/events</c> long-polls change events. Requests other
/// than <c>CNMLoginAuthFunc</c> carry the session as <c>Authorization: Bearer</c>. Replies hold only fields with an
/// established meaning; native transport details stay in the client adapters.
/// </summary>
public sealed class MessengerHttpServer : IAsyncDisposable
{
    internal const string Prefix = "/NexonMessenger/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly WebApplication _app;
    private readonly SemaphoreSlim _workers, _polls;
    private readonly MessengerHttpOptions _options;

    private MessengerHttpServer(WebApplication app, SemaphoreSlim workers, SemaphoreSlim polls, MessengerHttpOptions options)
    {
        _app = app;
        _workers = workers;
        _polls = polls;
        _options = options;
    }

    /// <summary>Gets the bound addresses, including the port chosen when listen uses zero.</summary>
    public IReadOnlyCollection<string> Addresses => _app.Urls.ToArray();

    /// <summary>Binds the listener and serves <paramref name="messenger"/>.</summary>
    public static async Task<MessengerHttpServer> StartAsync(MessengerHttpOptions options, IMessengerService messenger, ILogger? logger, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPEndPoint.Parse(options.Listen));
            k.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            k.Limits.MaxConcurrentConnections = options.MaxConnections;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
        });
        var app = builder.Build();
        var workers = new SemaphoreSlim(options.MaxConcurrentRequests);
        var polls = new SemaphoreSlim(options.MaxWaitingPolls);
        var budget = new MessengerRequestBudget(options.RequestsPerMinute, options.MaxRateLimitAddresses);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var gate = context.Request.Path.Equals(Prefix + "events", StringComparison.Ordinal) ? polls : workers;
            if (!budget.TryTake(context.Connection.RemoteIpAddress) || !await gate.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
            {
                await Reply(context, 429, new { error = "rate_limited" }).ConfigureAwait(false);
                return;
            }
            try { await next(context).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) when (!context.Response.HasStarted)
            { await Reply(context, 401, new { error = "unauthorized" }).ConfigureAwait(false); }
            catch (Exception e) when (!context.Response.HasStarted && e is JsonException or BadHttpRequestException or ArgumentException or InvalidDataException)
            { await Reply(context, 400, new { error = "invalid_request" }).ConfigureAwait(false); }
            catch (InvalidOperationException e) when (!context.Response.HasStarted && e.Message.Contains("capacity", StringComparison.Ordinal))
            {
                logger?.LogWarning("Messenger request refused: {Reason}", e.Message);
                await Reply(context, 503, new { error = "capacity" }).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        });

        Map<LoginAuthRequest>(app, "CNMLoginAuthFunc", authenticated: false, async (_, body, token) =>
        {
            var session = await messenger.LoginAuthAsync(body.Ticket, body.GameCode, token).ConfigureAwait(false) ?? throw new UnauthorizedAccessException();
            return new { session = session.Token, session.ExpiresAt, user = User(session.User) };
        });
        Map<Empty>(app, "CNMLogoutAuthFunc", true, async (s, _, t) => { await messenger.LogoutAuthAsync(s, t).ConfigureAwait(false); return Ok(MessengerResult.Ok); });
        Map<Empty>(app, "CNMLoginMessengerFunc", true, async (s, _, t) => { await messenger.LoginMessengerAsync(s, t).ConfigureAwait(false); return Ok(MessengerResult.Ok); });
        Map<Empty>(app, "CNMLogoutMessengerFunc", true, async (s, _, t) => { await messenger.LogoutMessengerAsync(s, t).ConfigureAwait(false); return Ok(MessengerResult.Ok); });
        Map<LevelRequest>(app, "CNMChangeMyLevelFunc", true, async (s, b, t) => { await messenger.ChangeMyLevelAsync(s, b.Level, t).ConfigureAwait(false); return Ok(MessengerResult.Ok); });
        Map<PageRequest>(app, "CNMGetFriendListFunc", true, async (s, b, t) =>
        {
            var page = await messenger.GetFriendListAsync(s, b.Page, t).ConfigureAwait(false);
            return new
            {
                page.Page, page.TotalPages, page.TotalFriends,
                categories = page.Categories.Select(c => new { c.Id, c.Name, c.GameCode }),
                friends = page.Friends.Select(f => new
                {
                    id = f.User.Id.Value, f.User.Nickname, f.User.GameCode, f.CategoryId, f.Blocked,
                    status = f.Presence.Status, f.Presence.Level,
                }),
            };
        });
        Map<FriendRequest>(app, "CNMRequestNewFriendFunc", true, async (s, b, t) =>
            Ok(await messenger.RequestNewFriendAsync(s, b.RequestKey, b.Nickname, b.Message, t).ConfigureAwait(false)));
        Map<ConfirmRequest>(app, "CNMConfirmNewFriendFunc", true, async (s, b, t) =>
            Ok(await messenger.ConfirmNewFriendAsync(s, b.RequestKey, b.Handle, b.Decision, t).ConfigureAwait(false)));
        Map<BlockRequest>(app, "CNMBlockFriendFunc", true, async (s, b, t) =>
            Ok(await messenger.BlockFriendAsync(s, b.RequestKey, new(b.FriendId), b.Blocked, t).ConfigureAwait(false)));
        Map<FriendCategoryRequest>(app, "CNMAddFriendToCategoryFunc", true, async (s, b, t) =>
            Ok(await messenger.AddFriendToCategoryAsync(s, b.RequestKey, new(b.FriendId), b.CategoryId, t).ConfigureAwait(false)));
        Map<FriendCategoryRequest>(app, "CNMDeleteFriendFromCategoryFunc", true, async (s, b, t) =>
            Ok(await messenger.DeleteFriendFromCategoryAsync(s, b.RequestKey, new(b.FriendId), b.CategoryId, t).ConfigureAwait(false)));
        Map<MoveRequest>(app, "CNMMoveFriendCategoryFunc", true, async (s, b, t) =>
            Ok(await messenger.MoveFriendCategoryAsync(s, b.RequestKey, new(b.FriendId), b.FromCategoryId, b.ToCategoryId, t).ConfigureAwait(false)));
        Map<CategoryNameRequest>(app, "CNMAddCategoryFunc", true, async (s, b, t) =>
        {
            var (result, category) = await messenger.AddCategoryAsync(s, b.RequestKey, b.Name, t).ConfigureAwait(false);
            return new { result, category = category is null ? null : new { category.Id, category.Name, category.GameCode } };
        });
        Map<CategoryRequest>(app, "CNMDeleteCategoryFunc", true, async (s, b, t) =>
            Ok(await messenger.DeleteCategoryAsync(s, b.RequestKey, b.CategoryId, t).ConfigureAwait(false)));
        Map<RenameRequest>(app, "CNMChangeCategoryNameFunc", true, async (s, b, t) =>
            Ok(await messenger.ChangeCategoryNameAsync(s, b.RequestKey, b.CategoryId, b.Name, t).ConfigureAwait(false)));
        Map<Empty>(app, "CNMGetSupportGameListFunc", true, async (s, _, t) =>
            new { games = (await messenger.GetSupportGameListAsync(s, t).ConfigureAwait(false)).Select(g => new { g.Code, g.FullName, g.ShortName }) });
        Map<GameRequest>(app, "CNMGetGameFullNameFunc", true, async (s, b, t) => await GameName(messenger, s, b.Code, full: true, t).ConfigureAwait(false));
        Map<GameRequest>(app, "CNMGetGameShortNameFunc", true, async (s, b, t) => await GameName(messenger, s, b.Code, full: false, t).ConfigureAwait(false));

        Map<ChatOpenRequest>(app, "CNMRequestChatSessionFunc", true, async (s, b, t) =>
        {
            var (result, chat) = await messenger.RequestChatSessionAsync(s, new(b.FriendId), t).ConfigureAwait(false);
            return new { result, chat = chat is null ? null : Chat(chat) };
        });
        Map<ChatRequest>(app, "CNMGSGetSessionInfoFunc", true, async (s, b, t) =>
            await messenger.GetChatSessionAsync(s, b.ChatId, t).ConfigureAwait(false) is { } chat
                ? new { result = MessengerResult.Ok, chat = Chat(chat) } : Ok(MessengerResult.NotFound));
        Map<ChatLineRequest>(app, "CNMCSSendChatMessageFunc", true, async (s, b, t) =>
            Ok(await messenger.SendChatMessageAsync(s, b.ChatId, b.Text, t).ConfigureAwait(false)));
        Map<ChatRequest>(app, "CNMGSWantCloseFunc", true, async (s, b, t) =>
            Ok(await messenger.CloseChatSessionAsync(s, b.ChatId, t).ConfigureAwait(false)));

        app.MapGet(Prefix + "events", async (HttpContext context) =>
        {
            var session = Bearer(context);
            var after = Number(context.Request.Query["after"], 0, long.MaxValue);
            var wait = Number(context.Request.Query["wait"], 0, options.MaxWaitSeconds);
            var batch = await messenger.WaitEventsAsync(session, after, TimeSpan.FromSeconds(wait), context.RequestAborted).ConfigureAwait(false);
            await Reply(context, 200, new { batch.Next, batch.Reset, events = batch.Events.Select(Event) }).ConfigureAwait(false);
        });
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            return new MessengerHttpServer(app, workers, polls, options);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            workers.Dispose();
            polls.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ShutdownSeconds));
        try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            _workers.Dispose();
            _polls.Dispose();
        }
    }

    private static void Map<TBody>(WebApplication app, string operation, bool authenticated, Func<string, TBody, CancellationToken, Task<object>> handle) where TBody : class
    {
        app.MapPost(Prefix + operation, async (HttpContext context) =>
        {
            var session = authenticated ? Bearer(context) : "";
            if (!(context.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            var body = await JsonSerializer.DeserializeAsync<TBody>(context.Request.Body, Json, context.RequestAborted).ConfigureAwait(false) ?? throw new InvalidDataException();
            await Reply(context, 200, await handle(session, body, context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });
    }

    private static async Task<object> GameName(IMessengerService messenger, string session, uint code, bool full, CancellationToken ct)
    {
        var game = await messenger.FindGameAsync(session, code, ct).ConfigureAwait(false);
        return game is null ? Ok(MessengerResult.NotFound) : new { result = MessengerResult.Ok, name = full ? game.FullName : game.ShortName };
    }

    private static object Ok(MessengerResult result) => new { result };

    private static object User(MessengerUser user) => new { id = user.Id.Value, user.Nickname, user.GameCode, user.Tenant };

    private static object Chat(MessengerChat chat) => new
    {
        chat.Id,
        members = chat.Members.Select(m => new { id = m.User.Id.Value, m.User.Nickname, m.User.GameCode, m.User.Tenant, status = m.Presence.Status }),
    };

    private static object Event(MessengerEvent e) => e switch
    {
        MessengerFriendRequestEvent request => new
        {
            seq = e.Sequence, kind = "friend_request",
            request = new { request.Request.Handle, from = User(request.Request.From), request.Request.Message, request.Request.CreatedAt },
        },
        MessengerFriendListChangedEvent => new { seq = e.Sequence, kind = "friend_list_changed" },
        MessengerPresenceChangedEvent presence => new
        {
            seq = e.Sequence, kind = "presence_changed", friendId = presence.Friend.Value, status = presence.Presence.Status, presence.Presence.Level,
        },
        MessengerChatOpenedEvent opened => new { seq = e.Sequence, kind = "chat_opened", chat = Chat(opened.Chat) },
        MessengerChatMessageEvent line => new { seq = e.Sequence, kind = "chat_message", line.ChatId, from = User(line.From), line.Text },
        _ => throw new InvalidOperationException($"Unmapped messenger event {e.GetType().Name}."),
    };

    private static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && header.Length is > 7 and <= 256 ? header[7..] : throw new UnauthorizedAccessException();
    }

    private static long Number(string? text, long minimum, long maximum) =>
        string.IsNullOrEmpty(text) ? minimum : long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
            ? value : throw new ArgumentException("Query value out of range.");

    private static Task Reply(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body, body.GetType(), Json, context.RequestAborted);
    }

    private sealed record Empty;
    private sealed record LoginAuthRequest(string Ticket, uint? GameCode = null);
    private sealed record LevelRequest(int Level);
    private sealed record PageRequest(int Page);
    private sealed record GameRequest(uint Code);
    private sealed record FriendRequest(string Nickname, string Message, string? RequestKey = null);
    private sealed record ConfirmRequest(int Handle, MessengerFriendDecision Decision, string? RequestKey = null);
    private sealed record BlockRequest(long FriendId, bool Blocked, string? RequestKey = null);
    private sealed record FriendCategoryRequest(long FriendId, int CategoryId, string? RequestKey = null);
    private sealed record MoveRequest(long FriendId, int FromCategoryId, int ToCategoryId, string? RequestKey = null);
    private sealed record CategoryNameRequest(string Name, string? RequestKey = null);
    private sealed record CategoryRequest(int CategoryId, string? RequestKey = null);
    private sealed record RenameRequest(int CategoryId, string Name, string? RequestKey = null);
    private sealed record ChatOpenRequest(long FriendId);
    private sealed record ChatRequest(int ChatId);
    private sealed record ChatLineRequest(int ChatId, string Text);
}

// Only the transport peer is trusted; forwarding headers do not allocate request windows.
internal sealed class MessengerRequestBudget(int requestsPerMinute, int maximumAddresses, TimeProvider? clock = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, (long Start, int Requests)> _windows = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal bool TryTake(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (!_windows.TryGetValue(address, out var window))
            {
                if (_windows.Count >= maximumAddresses) _windows.Remove(_windows.MinBy(pair => pair.Value.Start).Key);
                window = (now, 0);
            }
            else if (_clock.GetElapsedTime(window.Start, now) >= TimeSpan.FromMinutes(1)) window = (now, 0);
            if (window.Requests >= requestsPerMinute) return false;
            _windows[address] = (window.Start, window.Requests + 1);
            return true;
        }
    }
}
