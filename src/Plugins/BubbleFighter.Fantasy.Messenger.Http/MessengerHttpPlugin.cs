using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Messenger;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Messenger.Http;

/// <summary>Serves the messenger core over HTTP on its own listener, so it can run inside a game server or alone.</summary>
[Plugin("messenger.http", Category = "social", Names = ["en=Messenger service", "zh-Hans=好友聊天服务", "zh-Hant=好友聊天服務", "ko=메신저 서비스"], Description = "HTTP API for messenger clients, one route per native messenger operation.",
    Requires = [typeof(IMessengerService)])]
public sealed class MessengerHttpPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MessengerHttpOptions>();
        var server = await MessengerHttpServer.StartAsync(options, context.Services.Get<IMessengerService>(), context.Logger, cancellationToken).ConfigureAwait(false);
        context.Effect(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult());
        context.Logger.LogInformation("Messenger API listening on {Address}.", options.Listen);
    }
}
