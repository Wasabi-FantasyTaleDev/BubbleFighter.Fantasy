# Opcode contribution policy

Each profile owns separate client-to-server and server-to-client opcode spaces. Game and relay connections are separate too: declare `connection: game`, `relay` or `both`, and resolve names with `ForProtocol`. UDP datagrams (`connection: udp`) and the clan-server connection (`connection: clan`) are further spaces of their own and never inherit a `both` row.

A row documents its evidence honestly:

- `verified`: the build's client reader, writer or dispatch path establishes the claim.
- `inferred`: related client paths support the claim, with the remaining uncertainty stated.
- `unverified`: the claim has not been established; do not enable a handler on this basis.
- `captured`: a test session establishes observed bytes; do not promote the result to client verification.

Use the build's own packet label first. A matched reader or writer may justify a label from another build, recorded by `name_source`. A purpose proved by client code may receive a coined name ending in `_`; otherwise keep `UnknownNNN`. Supported name sources are `label@<profile>`, `mirrored`, `coined` and `none`.

The clan-server connection is the one exception to the suffix. No client build labels any packet on it (one generic packet class carries the whole space), so every name there is coined: its rows use plain names such as `ClanLogin` with `name_source: coined`, without the trailing `_`, and `UnknownNNN` with `none` where the purpose is not proven. Clan packets on the game connection follow the ordinary rule.

Document each codec's field order, widths and build-qualified client addresses, for example `kr-812 sub_810C20`. Keep unknown fields unknown. Layout differences belong in packet tables and profile capabilities; see [the packet format](../packets/README.md). Do not add version branches to game handlers for a difference the tables can express.

Every codec change needs tests pinning byte length and relevant field values. Unknown inbound opcodes are logged and are never decoded optimistically. Generated opcode catalogs are review inputs, not authority to activate a handler. Automated packet checks and scenario tests do not establish real-client acceptance.
