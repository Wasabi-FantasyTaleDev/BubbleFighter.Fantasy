namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>Marks a decoded message shared by a build adapter and game handlers.</summary>
public interface IMessage;

/// <summary>Advertises an installed inbound wire adapter for a shared message contract.</summary>
/// <typeparam name="T">The decoded contract produced by the adapter.</typeparam>
public interface IInboundMessageBinding<T> where T : IMessage
{
    /// <summary>Gets the connection protocol scope in which this adapter is installed.</summary>
    string Protocol { get; }
}

/// <summary>Encodes a shared outbound message for one deployed protocol scope.</summary>
/// <typeparam name="T">The shared outbound contract.</typeparam>
public interface IMessageSender<in T> where T : IMessage
{
    /// <summary>Gets the connection protocol scope served by this encoder.</summary>
    string Protocol { get; }
    /// <summary>Encodes and sends the message through the session's continuous wire stream.</summary>
    /// <param name="session">The destination session.</param>
    /// <param name="message">The outbound message.</param>
    /// <param name="ct">Cancellation for this send.</param>
    ValueTask SendAsync(ISession session, T message, CancellationToken ct);
}

/// <summary>Processes a decoded message without depending on opcode values or wire layout.</summary>
/// <typeparam name="T">The shared message contract.</typeparam>
/// <param name="session">The originating session.</param>
/// <param name="message">The decoded message.</param>
/// <param name="ct">Cancellation for this dispatch.</param>
public delegate ValueTask MessageHandler<in T>(ISession session, T message, CancellationToken ct) where T : IMessage;

/// <summary>Wraps message processing; omitting next intercepts the message. Next may be invoked once before this call returns.</summary>
/// <typeparam name="T">The shared message contract.</typeparam>
/// <param name="session">The originating session.</param>
/// <param name="message">The decoded message.</param>
/// <param name="next">The remaining pipeline, valid only during this invocation.</param>
/// <param name="ct">Cancellation for this dispatch.</param>
public delegate ValueTask MessageMiddleware<T>(ISession session, T message, MessageHandler<T> next, CancellationToken ct) where T : IMessage;

/// <summary>Registration methods bound to one plugin activation and its override declarations.</summary>
public interface IMessageRoutes
{
    /// <summary>Registers the sole base handler for a protocol and message type.</summary>
    /// <typeparam name="T">The shared message contract.</typeparam>
    /// <param name="protocol">The connection protocol scope.</param>
    /// <param name="handler">The base handler.</param>
    /// <returns>A registration to give to the owner's Effect method.</returns>
    IDisposable On<T>(string protocol, MessageHandler<T> handler) where T : IMessage;

    /// <summary>Replaces the currently selected handler owned by an explicitly declared target row.</summary>
    /// <typeparam name="T">The shared message contract.</typeparam>
    /// <param name="protocol">The connection protocol scope.</param>
    /// <param name="targetRowId">The selected handler's owner. The declaration is rowId:fully.qualified.MessageType.</param>
    /// <param name="handler">The replacement handler.</param>
    /// <returns>A registration whose removal restores the preceding handler.</returns>
    IDisposable Override<T>(string protocol, string targetRowId, MessageHandler<T> handler) where T : IMessage;

    /// <summary>Adds middleware; higher priorities run outermost and ties retain registration order.</summary>
    /// <typeparam name="T">The shared message contract.</typeparam>
    /// <param name="protocol">The connection protocol scope.</param>
    /// <param name="middleware">The around-handler callback.</param>
    /// <param name="priority">The execution priority.</param>
    /// <returns>A registration to give to the owner's Effect method.</returns>
    IDisposable Use<T>(string protocol, MessageMiddleware<T> middleware, int priority = 0) where T : IMessage;
}

/// <summary>A handler registration snapshot used for lifecycle validation and diagnostics.</summary>
/// <param name="Id">Registration identity within this router instance.</param>
/// <param name="OwnerRowId">The plugin activation that installed the handler.</param>
/// <param name="Protocol">The connection protocol scope.</param>
/// <param name="MessageType">The shared CLR message contract.</param>
/// <param name="Overrides">The declared target row and full type name, or null for a base handler.</param>
public sealed record MessageHandlerRegistration(long Id, string OwnerRowId, string Protocol, Type MessageType, string? Overrides);

/// <summary>Routes decoded messages through a deployment's typed handlers and middleware.</summary>
public interface IMessageRouter
{
    /// <summary>Binds registrations to a plugin activation; the owner must track each returned effect.</summary>
    /// <param name="owner">The registering plugin's context.</param>
    /// <returns>Registration methods using the owner's row identity and declarations.</returns>
    IMessageRoutes For(IPluginContext owner);
    /// <summary>Gets a snapshot of installed base and override handlers.</summary>
    IReadOnlyList<MessageHandlerRegistration> Handlers { get; }
    /// <summary>Dispatches using one immutable handler/middleware snapshot, retained across awaits.</summary>
    /// <typeparam name="T">The decoded message contract.</typeparam>
    /// <param name="protocol">The connection protocol scope.</param>
    /// <param name="session">The originating session.</param>
    /// <param name="message">The decoded message.</param>
    /// <param name="ct">Cancellation for this dispatch.</param>
    /// <returns>True when a base or override handler existed, including when middleware intercepted it.</returns>
    ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage;
}

/// <summary>Reports a decoded message with no typed handler, separately from unknown wire opcodes.</summary>
/// <param name="Session">The originating session.</param>
/// <param name="Protocol">The connection protocol scope.</param>
/// <param name="MessageType">The known message contract without a handler.</param>
public sealed record MessageUnhandledEvent(ISession Session, string Protocol, Type MessageType) : IEvent;
