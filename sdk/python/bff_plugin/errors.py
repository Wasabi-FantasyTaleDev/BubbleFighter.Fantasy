"""Error types and JSON-RPC error codes of the SDK plugin protocol."""

from __future__ import annotations

from typing import Any

# Codes defined by the protocol (spec section 7).
ACTIVATION_NOT_LIVE = -32001
SESSION_UNKNOWN = -32002
TYPE_NOT_ALLOWED = -32003
NO_BINDING = -32004
SERVICE_UNAVAILABLE = -32005
SERVICE_ERROR = -32010
# JSON-RPC standard codes the SDK also emits.
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603
# Code for an exception raised by plugin code (activate, deliver handlers, disposers).
PLUGIN_ERROR = -32000


class RpcError(Exception):
    """An error object received from, or about to be sent to, the other side."""

    def __init__(self, code: int, message: str, data: Any = None) -> None:
        super().__init__(f"[{code}] {message}")
        self.code = code
        self.message = message
        self.data = data


class ProtocolError(Exception):
    """The host broke the protocol in a way the SDK cannot recover from."""
