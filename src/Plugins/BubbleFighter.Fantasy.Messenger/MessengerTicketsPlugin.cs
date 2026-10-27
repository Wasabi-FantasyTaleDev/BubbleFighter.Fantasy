using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Messenger;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Messenger;

/// <summary>Signs this server's messenger tickets so a local or remote messenger can authenticate its players.</summary>
[Plugin("messenger.tickets", Description = "Signs short-lived messenger tickets for this server's accounts.",
    Provides = [typeof(IMessengerTicketIssuer)])]
public sealed class MessengerTicketsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MessengerTicketOptions>();
        var key = options.SigningKey == "ephemeral" ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : MessengerTicketCodec.ImportKey(options.SigningKey, requirePrivate: true);
        context.Effect(key);
        context.Effect(context.Services.Register<IMessengerTicketIssuer>(new MessengerTicketIssuer(options, key, TimeProvider.System)));
        context.Logger.LogInformation("Messenger tickets signed for tenant {Tenant} with a {KeyKind} key.", options.Tenant,
            options.SigningKey == "ephemeral" ? "process-local ephemeral" : "configured");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Signs tickets with one P-256 key.</summary>
internal sealed class MessengerTicketIssuer(MessengerTicketOptions options, ECDsa key, TimeProvider clock) : IMessengerTicketIssuer
{
    private readonly Dictionary<string, uint> _codes = options.GameCodes.ToDictionary(g => g.Build, g => (uint)g.Code, StringComparer.Ordinal);

    /// <inheritdoc />
    public string Tenant => options.Tenant;

    /// <inheritdoc />
    public string PublicUrl => options.EffectivePublicUrl;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> PublicKey { get; } = key.ExportSubjectPublicKeyInfo();

    /// <inheritdoc />
    public MessengerTicket? Issue(string subject, string nickname, string build)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        if (!_codes.TryGetValue(build, out var code)) return null;
        var now = clock.GetUtcNow();
        var expires = now.AddSeconds(options.TicketSeconds);
        var claims = new MessengerTicketClaims(options.Tenant, subject, nickname, code, build, now.ToUnixTimeSeconds(), expires.ToUnixTimeSeconds(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        string ticket;
        lock (key) ticket = MessengerTicketCodec.Sign(key, claims);
        return new(ticket, DateTimeOffset.FromUnixTimeSeconds(claims.ExpiresAt));
    }
}
