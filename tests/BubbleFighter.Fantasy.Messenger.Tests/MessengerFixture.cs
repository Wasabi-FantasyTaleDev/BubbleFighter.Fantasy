using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.Messenger;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Messenger.Tests;

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A messenger core over the in-memory store with two tenants, each with its own ticket issuer.</summary>
internal sealed class MessengerFixture : IDisposable
{
    internal const uint BubbleFighterKr = (uint)NexonGameCode.bubblefighter;
    internal const uint BubbleFighterCn2 = (uint)NexonGameCode.bubblefighter_cn2;
    private readonly List<IDisposable> _owned = [];

    internal ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero));
    internal MemoryMessengerStore Store { get; } = new(64);
    internal MessengerOptions Options { get; }
    internal MessengerService Service { get; }
    internal Dictionary<string, MessengerTicketIssuer> Issuers { get; } = [];

    internal MessengerFixture(Action<MessengerOptions>? configure = null)
    {
        Options = new MessengerOptions
        {
            Games = [new() { Code = NexonGameCode.bubblefighter, Aliases = [NexonGameCode.bubblefighter_cn2, NexonGameCode.bubblefighter_0102220F], FullName = "Bubble Fighter", ShortName = "BF" }],
        };
        configure?.Invoke(Options);
        var tenants = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        foreach (var tenant in new[] { "alpha", "beta" })
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _owned.Add(key);
            Issuers[tenant] = new MessengerTicketIssuer(new MessengerTicketOptions
            {
                Tenant = tenant, SigningKey = "ephemeral", TicketSeconds = 300,
                GameCodes = [new() { Build = "kr-812", Code = NexonGameCode.bubblefighter }],
            }, key, Clock);
            var verify = ECDsa.Create();
            verify.ImportSubjectPublicKeyInfo(Issuers[tenant].PublicKey.Span, out _);
            _owned.Add(verify);
            tenants[tenant] = verify;
        }
        Service = new MessengerService(Options, Store, tenants, Clock);
    }

    internal string Ticket(string subject, string? nickname = null, string tenant = "alpha") =>
        Issuers[tenant].Issue(subject, nickname ?? subject, "kr-812")!.Value;

    internal async Task<MessengerSession> SignInAsync(string subject, string? nickname = null, string tenant = "alpha", bool messenger = true, uint? gameCode = null)
    {
        var session = await Service.LoginAuthAsync(Ticket(subject, nickname, tenant), gameCode, default) ?? throw new InvalidOperationException("sign-in refused");
        if (messenger) await Service.LoginMessengerAsync(session.Token, default);
        return session;
    }

    internal async Task<IReadOnlyList<MessengerEvent>> DrainAsync(MessengerSession session, long after = 0) =>
        (await Service.WaitEventsAsync(session.Token, after, TimeSpan.Zero, default)).Events;

    internal async Task<(MessengerSession A, MessengerSession B)> FriendsAsync(string a = "ann", string b = "bob")
    {
        var sa = await SignInAsync(a);
        var sb = await SignInAsync(b);
        Assert.Equal(MessengerResult.Ok, await Service.RequestNewFriendAsync(sa.Token, null, b, "hi", default));
        var request = (await DrainAsync(sb)).OfType<MessengerFriendRequestEvent>().Single();
        Assert.Equal(MessengerResult.Ok, await Service.ConfirmNewFriendAsync(sb.Token, null, request.Request.Handle, MessengerFriendDecision.Accept, default));
        return (sa, sb);
    }

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
    }
}
