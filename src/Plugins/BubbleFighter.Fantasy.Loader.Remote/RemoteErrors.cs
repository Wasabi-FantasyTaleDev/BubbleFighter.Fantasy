using System.Text.Json;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>SDK message and service call errors, plus the standard JSON-RPC error codes.</summary>
internal static class RpcCodes
{
    /// <summary>The call named an activation that is not live.</summary>
    public const int ActivationNotLive = -32001;

    /// <summary>The call named a session that is unknown or closed.</summary>
    public const int SessionUnknown = -32002;

    /// <summary>The message type is not in the definition's handles or sends list.</summary>
    public const int TypeNotAllowed = -32003;

    /// <summary>The protocol has no sender or binding for the message type.</summary>
    public const int NoBinding = -32004;

    /// <summary>A declared service is not currently available in the host registry.</summary>
    public const int ServiceUnavailable = -32005;

    /// <summary>A declared service threw while serving a remote call.</summary>
    public const int ServiceError = -32010;

    /// <summary>JSON-RPC standard: the method does not exist.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>JSON-RPC standard: the parameters are invalid.</summary>
    public const int InvalidParams = -32602;

    /// <summary>JSON-RPC standard: the host failed while serving the call.</summary>
    public const int InternalError = -32603;

    /// <summary>JSON-RPC server error: the plugin exceeded the bounded number of in-flight plugin-to-host calls.</summary>
    public const int Overloaded = -32000;
}

/// <summary>An error that travels as a JSON-RPC error object, raised by either side.</summary>
internal sealed class RpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    /// <summary>Gets the JSON-RPC error code.</summary>
    public int Code { get; } = code;

    /// <summary>Gets the optional structured error data.</summary>
    public JsonElement? ErrorData { get; } = data;
}

/// <summary>The child process ended or broke the protocol, so no further calls can succeed.</summary>
internal sealed class RemoteProcessException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A host-to-plugin request got no response within <c>request_timeout_ms</c>.</summary>
internal sealed class RemoteTimeoutException(string message) : TimeoutException(message);

/// <summary>The host refused to dispatch because the process already has <c>max_in_flight</c> outstanding requests.</summary>
internal sealed class RemoteBackpressureException(string message) : InvalidOperationException(message);
