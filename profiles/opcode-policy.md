# Opcode contribution policy

Each profile owns separate client-to-server and server-to-client opcode spaces. Game and relay connections are separate too: declare `connection: game`, `relay` or `both`, and resolve names with `ForProtocol`.

A row documents its evidence honestly:

- `verified`: the build's client reader, writer or dispatch path establishes the claim.
- `inferred`: related client paths support the claim, with the remaining uncertainty stated.
- `unverified`: the claim has not been established; do not enable a handler on this basis.
- `captured`: a test session establishes observed bytes; do not promote the result to client verification.

Use the build's own packet label first. A matched reader or writer may justify a label from another build, recorded by `name_source`. A purpose proved by client code may receive a coined name ending in `_`; otherwise keep `UnknownNNN`. Supported name sources are `label@<profile>`, `mirrored`, `coined` and `none`.

Document each codec's field order, widths and build-qualified client addresses, for example `kr-812 sub_810C20`. Keep unknown fields unknown. Layout differences belong in packet tables and profile capabilities; see [the packet format](../packets/README.md). Do not add version branches to game handlers for a difference the tables can express.

Every codec change needs tests pinning byte length and relevant field values. Unknown inbound opcodes are logged and are never decoded optimistically. Generated opcode catalogs are review inputs, not authority to activate a handler. Automated packet checks and scenario tests do not establish real-client acceptance.
