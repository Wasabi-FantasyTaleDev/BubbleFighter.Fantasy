using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BubbleFighter.Fantasy.Messenger.Http;
using Xunit;

namespace BubbleFighter.Fantasy.Messenger.Tests;

/// <summary>The HTTP contract over a real listener: original operation names, readable JSON, authorization and long polls.</summary>
public sealed class MessengerHttpTests : IAsyncLifetime
{
    private readonly MessengerFixture _fixture = new();
    private MessengerHttpServer _server = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _server = await MessengerHttpServer.StartAsync(new MessengerHttpOptions { Listen = "127.0.0.1:0" }, _fixture.Service, null, default);
        _http = new HttpClient { BaseAddress = new Uri(_server.Addresses.Single()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _fixture.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(string operation, object body, string? session = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "NexonMessenger/" + operation) { Content = JsonContent.Create(body) };
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<string> SignInAsync(string subject)
    {
        var (status, body) = await CallAsync("CNMLoginAuthFunc", new { ticket = _fixture.Ticket(subject) });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(subject, body.GetProperty("user").GetProperty("nickname").GetString());
        var session = body.GetProperty("session").GetString()!;
        Assert.Equal("ok", (await CallAsync("CNMLoginMessengerFunc", new { }, session)).Body.GetProperty("result").GetString());
        return session;
    }

    private async Task<JsonElement> EventsAsync(string session, long after, int wait = 0)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"NexonMessenger/events?after={after}&wait={wait}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task FriendFlowRunsOverOriginalOperationNames()
    {
        var ann = await SignInAsync("ann");
        var bob = await SignInAsync("bob");
        Assert.Equal("ok", (await CallAsync("CNMRequestNewFriendFunc", new { nickname = "bob", message = "hi", requestKey = "r1" }, ann)).Body.GetProperty("result").GetString());

        var events = await EventsAsync(bob, 0);
        var request = events.GetProperty("events")[0];
        Assert.Equal("friend_request", request.GetProperty("kind").GetString());
        Assert.Equal("ann", request.GetProperty("request").GetProperty("from").GetProperty("nickname").GetString());
        var handle = request.GetProperty("request").GetProperty("handle").GetInt32();
        Assert.Equal("ok", (await CallAsync("CNMConfirmNewFriendFunc", new { handle, decision = "accept" }, bob)).Body.GetProperty("result").GetString());

        var (_, list) = await CallAsync("CNMGetFriendListFunc", new { page = 0 }, ann);
        var friend = list.GetProperty("friends")[0];
        Assert.Equal(("bob", "online", (long)MessengerFixture.BubbleFighterKr, 0),
            (friend.GetProperty("nickname").GetString(), friend.GetProperty("status").GetString(), friend.GetProperty("gameCode").GetInt64(), friend.GetProperty("categoryId").GetInt32()));
        Assert.Equal(1, list.GetProperty("totalPages").GetInt32());

        var (_, category) = await CallAsync("CNMAddCategoryFunc", new { name = "Team" }, ann);
        var id = category.GetProperty("category").GetProperty("id").GetInt32();
        Assert.Equal("ok", (await CallAsync("CNMAddFriendToCategoryFunc", new { friendId = friend.GetProperty("id").GetInt64(), categoryId = id }, ann)).Body.GetProperty("result").GetString());
        Assert.Equal("not_empty", (await CallAsync("CNMDeleteCategoryFunc", new { categoryId = id }, ann)).Body.GetProperty("result").GetString());

        var (_, games) = await CallAsync("CNMGetSupportGameListFunc", new { }, ann);
        Assert.Equal("Bubble Fighter", games.GetProperty("games")[0].GetProperty("fullName").GetString());
        Assert.Equal("BF", (await CallAsync("CNMGetGameShortNameFunc", new { code = MessengerFixture.BubbleFighterKr }, ann)).Body.GetProperty("name").GetString());
        Assert.Equal("not_found", (await CallAsync("CNMGetGameFullNameFunc", new { code = 1 }, ann)).Body.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ChatRunsOverOriginalOperationNames()
    {
        var (ann, bob) = await _fixture.FriendsAsync("cat", "dog");
        var (_, opened) = await CallAsync("CNMRequestChatSessionFunc", new { friendId = bob.User.Id.Value }, ann.Token);
        Assert.Equal("ok", opened.GetProperty("result").GetString());
        var chatId = opened.GetProperty("chat").GetProperty("id").GetInt32();
        Assert.Equal(2, opened.GetProperty("chat").GetProperty("members").GetArrayLength());
        Assert.All(opened.GetProperty("chat").GetProperty("members").EnumerateArray(), m => Assert.Equal("online", m.GetProperty("status").GetString()));

        var (_, info) = await CallAsync("CNMGSGetSessionInfoFunc", new { chatId }, ann.Token);
        Assert.Equal(chatId, info.GetProperty("chat").GetProperty("id").GetInt32());
        Assert.Equal("not_found", (await CallAsync("CNMGSGetSessionInfoFunc", new { chatId = chatId + 1 }, ann.Token)).Body.GetProperty("result").GetString());

        var cursor = (await EventsAsync(bob.Token, 0)).GetProperty("next").GetInt64();
        Assert.Equal("ok", (await CallAsync("CNMCSSendChatMessageFunc", new { chatId, text = "hello" }, ann.Token)).Body.GetProperty("result").GetString());
        var events = (await EventsAsync(bob.Token, cursor)).GetProperty("events");
        Assert.Equal("chat_opened", events[0].GetProperty("kind").GetString());
        Assert.Equal(("chat_message", chatId, "cat", "hello"), (events[1].GetProperty("kind").GetString(), events[1].GetProperty("chatId").GetInt32(),
            events[1].GetProperty("from").GetProperty("nickname").GetString(), events[1].GetProperty("text").GetString()));

        Assert.Equal("ok", (await CallAsync("CNMGSWantCloseFunc", new { chatId }, bob.Token)).Body.GetProperty("result").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync("CNMCSSendChatMessageFunc", new { chatId, text = "x", extra = 1 }, ann.Token)).Status);
    }

    [Fact]
    public async Task LongPollWakesOnNews()
    {
        var ann = await SignInAsync("ann");
        var bob = await SignInAsync("bob");
        var poll = EventsAsync(bob, 0, wait: 10);
        await Task.Delay(200);
        Assert.False(poll.IsCompleted);
        await CallAsync("CNMRequestNewFriendFunc", new { nickname = "bob", message = "" }, ann);
        var batch = await poll.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, batch.GetProperty("next").GetInt64());
        Assert.False(batch.GetProperty("reset").GetBoolean());
    }

    [Fact]
    public async Task MalformedOrUnauthorizedRequestsFailLoud()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("CNMLoginAuthFunc", new { ticket = "v1.bogus.sig" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("CNMGetFriendListFunc", new { page = 0 })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("CNMGetFriendListFunc", new { page = 0 }, "forged")).Status);
        var ann = await SignInAsync("ann");
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync("CNMGetFriendListFunc", new { page = 0, extra = 1 }, ann)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync("CNMGetFriendListFunc", new { }, ann)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync("CNMGetFriendListFunc", new { page = 5 }, ann)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync("CNMConfirmNewFriendFunc", new { handle = 1, decision = "maybe" }, ann)).Status);

        using var raw = new HttpRequestMessage(HttpMethod.Post, "NexonMessenger/CNMChangeMyLevelFunc") { Content = new StringContent("{", Encoding.UTF8, "application/json") };
        raw.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ann);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(raw)).StatusCode);
        using var plain = new HttpRequestMessage(HttpMethod.Post, "NexonMessenger/CNMChangeMyLevelFunc") { Content = new StringContent("{\"level\":1}", Encoding.UTF8, "text/plain") };
        plain.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ann);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(plain)).StatusCode);
    }
}
