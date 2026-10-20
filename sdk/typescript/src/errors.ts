/** Error types and JSON-RPC error codes of the SDK plugin protocol. */

// Codes defined by the protocol (spec section 7).
export const ACTIVATION_NOT_LIVE = -32001;
export const SESSION_UNKNOWN = -32002;
export const TYPE_NOT_ALLOWED = -32003;
export const NO_BINDING = -32004;
/** A declared service has no available provider. */
export const SERVICE_UNAVAILABLE = -32005;
/** The selected host provider failed. */
export const SERVICE_ERROR = -32010;
// JSON-RPC standard codes the SDK also emits.
export const METHOD_NOT_FOUND = -32601;
export const INVALID_PARAMS = -32602;
export const INTERNAL_ERROR = -32603;
// Code for an exception raised by plugin code (activate, deliver handlers, disposers).
export const PLUGIN_ERROR = -32000;

/** An error object received from, or about to be sent to, the other side. */
export class RpcError extends Error {
  constructor(
    readonly code: number,
    public override readonly message: string,
    readonly data: unknown = null,
  ) {
    super(`[${code}] ${message}`);
    this.name = "RpcError";
  }
}

/** The host broke the protocol in a way the SDK cannot recover from. */
export class ProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ProtocolError";
  }
}
