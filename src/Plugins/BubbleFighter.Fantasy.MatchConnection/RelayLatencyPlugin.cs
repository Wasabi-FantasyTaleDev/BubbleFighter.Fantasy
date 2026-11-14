using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.MatchConnection;

/// <summary>Replies once to each delay probe admitted by the owning authenticated relay lifecycle.</summary>
[Plugin("match-latency", Category = "network", Names = ["en=Match latency", "zh-Hans=对局延迟", "zh-Hant=對局延遲", "ko=매치 지연"], Description = "Passive authenticated relay delay acknowledgements.",
    Requires = [typeof(IMessageRouter)],
    RequiresMany = [typeof(IMatchConnectionOperations), typeof(IInboundMessageBinding<RelayLatencyRequested>),
        typeof(IMessageSender<RelayLatencyAcknowledged>)])]
public sealed class RelayLatencyPlugin : IPlugin
{
    /// <summary>Binds relay probes to exactly one authenticated operation owner and one reply sender in their scope.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<RelayLatencyOptions>();
        var operations = context.Services.GetAll<IMatchConnectionOperations>()
            .Where(item => item.Protocol == options.Protocol).ToArray();
        var bindings = context.Services.GetAll<IInboundMessageBinding<RelayLatencyRequested>>()
            .Where(item => item.Protocol == options.Protocol).ToArray();
        var senders = context.Services.GetAll<IMessageSender<RelayLatencyAcknowledged>>()
            .Where(item => item.Protocol == options.Protocol).ToArray();
        if (operations.Length != 1 || bindings.Length != 1 || senders.Length != 1)
            throw new ConfigException($"Relay latency for '{options.Protocol}' requires exactly one operation service, inbound binding and reply sender; found {operations.Length}, {bindings.Length}, {senders.Length}.");
        var replies = senders[0];
        var owner = operations[0];
        var reply = new RelayLatencyAcknowledged(options.ReplyWord);
        context.Effect(context.Services.Get<IMessageRouter>().For(context)
            .On<RelayLatencyRequested>(options.Protocol, (session, _, ct) =>
                owner.RunAuthenticatedAsync(session, token => replies.SendAsync(session, reply, token), ct)));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Selects the owning relay scope and the opaque word used for passive probe replies.</summary>
public sealed class RelayLatencyOptions : IValidatableOptions
{
    /// <summary>Gets or sets the relay protocol shared by the request dialect and authentication owner.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the server-selected word discarded by the client's reply handler; it is not an echoed tick.</summary>
    public uint ReplyWord { get; set; }

    /// <summary>Rejects protocol names that omit a client build or select another connection kind.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (string.IsNullOrEmpty(Protocol) || !Protocol.EndsWith("/relay", StringComparison.Ordinal)
            || Protocol.Length == "/relay".Length)
            yield return new ValidationResult("protocol must name a client build's relay scope.", [nameof(Protocol)]);
    }
}
