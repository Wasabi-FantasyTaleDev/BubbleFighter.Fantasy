using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>Resolves the build's encoders for play messages by protocol, once at activation.</summary>
internal static class PlaySenders
{
    /// <summary>
    /// Returns the single encoder for <typeparamref name="T"/> on <paramref name="protocol"/>, or null when the
    /// build's dialect does not publish one (the feature is absent on that build). Two encoders for one
    /// protocol are a configuration error.
    /// </summary>
    public static IMessageSender<T>? Optional<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length switch
        {
            0 => null,
            1 => senders[0],
            _ => throw new ConfigException($"InGame found {senders.Length} encoders for {typeof(T).Name} on '{protocol}'."),
        };
    }

    /// <summary>
    /// Returns the single encoder for <typeparamref name="T"/> on <paramref name="protocol"/>; its absence is a
    /// configuration error, because the feature that needs it is mounted.
    /// </summary>
    public static IMessageSender<T> Required<T>(IPluginContext context, string protocol) where T : IMessage =>
        Optional<T>(context, protocol)
        ?? throw new ConfigException($"InGame needs an encoder for {typeof(T).Name} on '{protocol}'; the build's dialect row publishes none.");
}
