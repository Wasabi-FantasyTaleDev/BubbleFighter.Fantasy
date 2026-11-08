# Fantasy Tale Gateway

Set `connection_trace: true` on a `bf-center-selector` row to record socket lifecycle events by
role and local listener port. `connection_trace_max_bytes` bounds that row's ASCII stderr output
to 2048..1048576 bytes, with a default of 65536. Trace events contain fixed state names and local
ports, including status, uniqueness request/reply, EOF and closure. They contain no packet bodies
or credentials. A binding event maps each local auxiliary port to its native group and sub-key,
which remain useful when the client has cleared a disconnected socket's address and port.
Exhaustion emits a fixed marker and leaves the process-error log budget available.
The parent must retain these lines to correlate them with the client's auxiliary connection list.
Connection tracing defaults off.

`native_probe_timeout_ms` enables an additional native auxiliary status check before READY. It defaults
to zero (disabled) and accepts 100..5000 ms as one shared deadline for the whole directory.
`native_probe_parallelism` bounds concurrent probes to 1..16 (default 4). Each entry must provide a
positive `native_center_id` and `status_capacity` (1..1073741823). The probe sends the build's empty
status request, including its optional marker, and requires a valid greeting and encrypted status
reply. It does not send account credentials or login requests. Failed destinations retain their
identity as existing local unavailable listeners; they still answer the client's uniqueness cleanup.
Automatic selection excludes those failed native endpoints for the remainder of that launch, even
when a later parent TCP measurement succeeds. Healthy endpoints retain direct native connections.
This prevents a TCP accept during incomplete server activation from leaving an unresponsive auxiliary
in the client directory. In tw-454, sub_A7FAD0 removes an auxiliary on the uniqueness reply, while
sub_B53670 repeatedly reports an unretired auxiliary after its login cleanup timer expires.

Fantasy Tale Gateway is a hidden, parent-owned Native AOT executable for temporary game protocol
capabilities. It currently contains `bf-center-selector`. It has no database, HTTP server, UI, runtime
plugin discovery, localization resources or installed .NET runtime dependency. Future messenger work
belongs in another statically linked plugin row of this executable. No messenger protocol or `nmco`
capability is implemented or advertised by this revision.

## Native selection behavior

Start a fresh game process against the endpoint returned by Gateway. For two or more targets, Gateway
answers the client's patch request and supplies a native Center directory containing real IPv4
endpoints. The client opens its own connections to those targets and promotes its selected connection
into the game connection. Gateway never proxies gameplay or issues authentication tickets.
The player chooses the target in the game's original UI. The Launcher supplies the eligible directory;
it does not select a target on the player's behalf.

The directory uses the client's zero-initialized group and excludes its reserved current sub-key.
It omits Gateway's endpoint, which would otherwise become a selectable server. This path is established
by kr-812 `sub_8DE5C0`, `sub_8E6A40`, `sub_8E7110`, `sub_8E7600`; cn-324 `sub_786B10`,
`sub_78CE60`, `sub_78D580`, `sub_78DA40`; and cn-279 `sub_6D6D00`, `sub_6D8C20`, `sub_6D8800`.
The build manifest contains these 14 adapters: `kr-1256`, `kr-812`, `id-400`, `id-1809`, `cn-324`,
`cn-717`, `cn-774`, `tw-454`, `cn-279`, `cn-406`, `cn-706`, `cn-404`, `cn-408` and `cn-735`.
Unsupported builds fail before READY. Every adapter is exercised through encrypted bootstrap in a
published process; that check does not prove its complete original-client chooser and authentication path.

The modern `kr-1256`, `cn-774` and `tw-454` directory adapters are experimental. Their canonical tables
prove the five-field record widths, while Gateway projects legacy grouping keys and IPv4/port into
the corresponding positions. Those fields retain unknown names because their modern meanings have
not been established. Modern first-connect requests contain two scalar words and 22 counted UTF-16
strings, as written by kr-1256 `sub_10C6440`, cn-774 `sub_105BE90` and tw-454 `sub_ECB490`.
The 96-byte all-empty form and bounded nonempty strings are both accepted.
The KR2026 native selector and automatic handoff have completed an original-client check. The other
modern adapters have encrypted socket and published-process coverage; their original-client display
and resumed login remain separate acceptance scopes. Gateway coverage does not enable target-server behavior.

Greeting status, greeting command line, patch-request opcode and permitted pre-handshake opcodes come
from each composed protocol profile at build time. The patch reply remains a false update flag and
an empty command line. Known startup prelude frames are accepted only before first-connect, within
the existing payload and handshake deadline bounds.

The catalog `name` is an upstream label. The native selector gets names and population from the target
server where supported; cn-279 obtains names from client resources. Targets must implement their build's
auxiliary Center connection, status and selection/login behavior. A target that only accepts the
ordinary first-connect handshake is insufficient. All targets must speak the selected build and accept
the credentials supplied by the game's existing login integration. Gateway cannot turn a ticket issued
for one independent server into a ticket for another. In cn-324, the login integration must also resume
authentication after the native chooser callback.

Each ordinary Center entry may also carry an optional `short_name`. It is compact display metadata for
the automatic choice, not a second identity or a routing rule. When the parent supplies an automatic
entry, its optional `short_name` takes precedence. Otherwise Gateway uses the first
`admitted_center_ids` entry as its stable display anchor: the label is `自动选择（<short_name>）`
when that Center has a short name, or `自动选择（<name>）` using its full name when omitted.
Admission and runtime target selection remain controlled by the parent snapshot;
the label does not change when RTT or load selects a different worker.
Gateway applies no provider, hostname or display-name exclusions to that admitted pool. An upstream
policy can omit a worker from automatic admission while retaining it as an ordinary manual choice.

With one target, READY returns `mode: "direct"` and that target's endpoint. The row completes immediately;
no TCP listener is created. The Launcher may avoid starting Gateway entirely for this case.

The bootstrap connection remains open until the client releases it or the parent sends STOP or closes
the control pipe. By default, the Launcher owns that lifetime so a player can remain in the native
selector or complete login without an arbitrary process deadline. `selection_timeout_ms` can opt into
an absolute deadline. Releasing the bootstrap connection is not a login success receipt.
The process exits when all rows complete; a future messenger row can keep the same process alive after
selection. A row failure stops the process and closes other rows' resources.

During selection, cn-324 sends an empty `gcpReqKeepAlivePing` on its current connection every ten seconds
(cn-324 `sub_78B580`, cn-324 `sub_793E20`). Gateway accepts it after first-connect without replying or
extending an explicitly configured selection deadline. Only the first ping is logged. Other builds require their own verified
opcode mapping; a nonempty body or a ping before first-connect fails the bootstrap exchange.

The native startup stage can also send auxiliary service requests on the directory connection.
For id-400 and id-1809, `gcpReqIDPCRoomAuth` carries two counted UTF-16 strings; their stage setup
continues immediately after sending (id-400 `sub_5E9050`, id-1809 `sub_632750`). For cn-774,
the empty cash refresh request is sent by the cash UI and timer (cn-774 `sub_63ED80`, cn-774
`sub_641B80`). The compiled per-build manifest explicitly admits these requests after first-connect,
validates their canonical layouts and logs each opcode once. Gateway provides no balance or regional
authorization reply; those services belong to the target. Invalid bodies, early requests and other
opcodes remain errors. These observations do not extend the configured selection deadline.

## Launcher control contract, schema 1

The Launcher creates a random local named pipe with `PipeDirection.InOut`,
`PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`, byte transmission, and one client. It starts:

```text
FantasyTale.Gateway.exe --pipe fantasy-tale-gateway-<random-128-bit-identifier>
```

Use `UseShellExecute = false`, `CreateNoWindow = true`, and redirect and drain stderr. The executable
uses the Windows GUI subsystem, so a console window is not allocated even if started independently.
Only the random pipe name appears in the command line. The child connects locally with current-user
access restriction. No config files, TCP control ports or credentials in arguments are needed. Failure
to connect or configure within ten seconds terminates the child.

Each control message is a four-byte unsigned little-endian body length followed by UTF-8 JSON.
Length must be 1..65536 bytes. Messages are framed even if the OS combines or splits pipe writes.
Unknown JSON properties, duplicate properties, null required values and unsupported schema versions
are errors. Send the launch configuration first:

```json
{
  "schema_version": 1,
  "rows": [
    {
      "id": "selector",
      "plugin": "bf-center-selector",
      "config": {
        "build": "kr-812",
        "centers": [
          { "id": "server-a", "name": "Server A", "host": "192.0.2.10", "port": 7110 },
          { "id": "server-b", "name": "Server B", "host": "198.51.100.20", "port": 7210 }
        ]
      }
    }
  ]
}
```

The Launcher filters the catalog by build and authentication compatibility and resolves DNS to IPv4.
In legacy mode catalog identities and IPv4 endpoints must be unique. IPv6 and DNS names cannot be encoded in this
native gate format. Names and identities are at most 128 characters; directories preserve every supplied entry without a fixed entry-count limit.
The process accepts 1..8 uniquely identified plugin rows.

Directory acquisition and authentication belong to the Launcher. Keep the directory source, selected
authentication API and policy, real game targets and local bootstrap endpoint distinct. Substitute
READY only into the game's transport arguments. A master-issued ticket works only when the selected
target independently accepts it; a directory-only source does not establish shared authentication.
This control schema emits no selection event because the game connects directly to its targets.
Obtaining a different server's ticket after native selection requires an additional authentication
bridge; the selector does not implement that exchange.

In cn-324, `sub_78D580` opens auxiliary target connections before selection, and `sub_78DA40` promotes
the chosen connection before calling the in-process login continuation `sub_584890`. Neither the
auxiliary probes nor release of Gateway's bootstrap socket identifies the player's choice to the
Launcher. A future selection notification therefore needs a verified client-side callback. Such an
event should contain only a catalog-matched target identity and endpoint, associated with the owning
launch. The Launcher and authentication component must validate that association and own any ticket
exchange. A generic communication transport alone does not supply this callback or an authentication
contract, and Gateway does not implement either capability.

### Explicit native selector identity capability

Schema 1 also supports the opt-in `bf-native-selector-identity-v1` bridge contract for all 14 builds.
This is an integration contract for a Launcher-owned native bridge; Gateway alone does not change
native labels. Each build requires its own verified native consumer; accepting the control contract
does not establish that a bridge exists for that client. The three modern directory projections retain
their evidence scope in identity mode as well. A launch requires at least two choices in this mode.
The Launcher retains one cluster entry and presents all choices inside the native chooser. It may
resolve the automatic choice in the background, preserving the manual choices in the directory.
Gateway does not measure RTT or choose a backend.

Add this object to the selector row's `config`:

```json
{"selector_identity":{"capability":"bf-native-selector-identity-v1","launch_id":"0123456789abcdef0123456789abcdef"}}
```

Every center then requires `backend_id` and `native_center_id` alongside `id`, `name`, `host`, `port`.
`id` identifies the selector choice. `backend_id` identifies the physical backend using 1..64 lowercase
ASCII letters, digits or hyphens. `native_center_id` is a unique positive signed 32-bit chooser identity,
1..2147483647; this contract bound does not assert a smaller native wire field. Names are nonblank
printable strings of at most 128 UTF-16 code units, a control-contract limit rather than an inferred
native buffer size. Selector ids, native ids and names must each be unique. Backend identities and
canonical IPv4 endpoints have a one-to-one mapping within the launch. Multiple distinct selector
choices may share that backend and endpoint, such as automatic and manual choices. These identities
grant no authentication, data sharing, room visibility or joining rights.

The READY row adds `selector_identity` with `capability`, `launch_id`, `build`, and `entries`. Each entry
contains `id`, `name`, `backend_id`, `native_center_id`, `group`, `sub_key`, `host`, `port`, in wire
directory order. Keys are generated once and used for both READY and native wire encoding. Native
ids and names are metadata for the native bridge, not extra fields appended to the native packet.
Legacy READY omits this object and legacy mode rejects bridge metadata and duplicate endpoints.

Before starting the game the Launcher must verify the capability, owning launch, executable build
and every ordered mapping field against its expected configuration. It must install the mapping
through its verified native bridge; missing support, stale mappings or ambiguous connections fail
the launch. The capability request asserts parent support and does not prove a bridge was installed.
An unsupported integration must not silently fall back to duplicate native names. A directory-only
source or a shared endpoint never establishes shared authentication.

`cn-324 sub_78CE60` indexes groups and sub-keys, and `cn-324 sub_78D580` creates one auxiliary connection
per matching row even when endpoints match. The connection retains group/sub-key at +2328/+2332.
`cn-324 sub_703640` consumes backend status identity/name at +2340/+2344; `cn-324 sub_407B20` supplies the label read
by `cn-324 sub_408440`. `cn-324 sub_78DA40` promotes the selected connection pointer. A bridge must associate by
directory keys and verify the endpoint, preserve native string ownership and retain backend status
identity separately. It must not replace backend authentication identity with a chooser identity.
The declaration and tests here do not establish native bridge installation or alias UI acceptance.
For every build, the encrypted six-choice test retains distinct automatic/manual sub-keys on the same
physical backend and verifies the ordered READY metadata. No selector is collapsed into a Launcher
entry or removed because another selector targets the same backend.

The v1 capability represents choices with real destinations only. A parent supporting disabled native
rows may instead explicitly request `bf-native-selector-presentation-v2`. It retains the v1 active
`centers` and READY `entries`, and adds `presentation` to both `selector_identity` objects:

```json
{
  "capability": "bf-native-selector-presentation-v2",
  "launch_id": "0123456789abcdef0123456789abcdef",
  "presentation": [
    { "id": "automatic", "name": "Automatic", "native_center_id": 1, "binding_id": "automatic" },
    { "id": "manual-a", "name": "Region A", "native_center_id": 2, "binding_id": "manual-a" },
    { "id": "manual-b", "name": "Region B", "native_center_id": 3, "unavailable_reason": "offline" }
  ]
}
```

The array determines visible order and contains at least two rows. Each active binding occurs exactly once;
its `id`, `name` and `native_center_id` must match the referenced active entry. An unavailable row has
no `binding_id`, requires a 1..64 character lowercase ASCII reason identifier and carries no network
fields. Choice ids, labels and positive presentation ids are unique across the entire visible list.
The native packet contains only active entries, so unavailable rows create no auxiliary sockets.
At least one real destination is required. With one active destination and multiple presentation rows,
Gateway still returns `bootstrap`; the parent must preserve the chooser instead of bypassing it.
An entirely unavailable directory fails before READY and the Launcher must not start the game.

The parent must verify all active and presentation metadata before installing its native bridge. It
must build disabled rows using native UI objects, reject their clicks and map available rows to the
actual connection by binding keys and endpoint. It must preserve the original backend status identity.
It must not add disconnected placeholder connections to the client's network vector: cn-324
`sub_78D580` waits for auxiliary connections before selection, and cn-324 `sub_408440`/`sub_408800`
otherwise assume button ordinals match that vector. Presentation-only metadata does not implement
these UI and callback changes; a parent without that implementation must not request v2. A target
that becomes unavailable after the parent's preflight still needs native connection-failure handling.
This contract does not assert support for selecting a target with an incompatible ticket issuer.

Wait for READY before starting the game:

```json
{"schema_version":1,"type":"ready","rows":[{"id":"selector","mode":"bootstrap","host":"127.0.0.1","port":49152,"build":"kr-812"}]}
```

The port is allocated by the OS on IPv4 loopback. Substitute it into the game's existing launch
arguments; supply credentials through its existing authentication integration. If READY returns
`direct`, use the real endpoint. Do not infer readiness from stderr or elapsed time.

Subsequent commands use the same framing: `{"type":"stop"}` stops the process; the optional automatic
measurement refresh is described below. EOF also stops the process.
On failed/cancelled game launch, close the pipe, await process exit, and use a bounded parent-side
termination fallback. Keep the owning pipe open throughout selection. Exit code 0 means normal
completion or parent stop; 1 means invalid input, unsupported build/plugin, malformed game data,
transport error or deadline expiration. English stderr is capped at 2 KiB per process, without packet
bodies, credentials or persistent files. The Launcher owns any retained log.
Diagnostics identify bootstrap acceptance, greeting and directory delivery, accepted markers,
incomplete EOF, rejected opcode/body length, and the phase whose deadline expired. A delivered
directory or a released bootstrap connection does not confirm that the native chooser opened.

Optional configuration limits:

| Field | Default | Accepted range |
|---|---:|---:|
| `control_frame_timeout_ms` (top level, after first byte) | 5000 | 100..30000 |
| `connect_timeout_ms` | 30000 | 0 (parent launch budget) or 100..600000 |
| `selection_timeout_ms` (absolute after accept; zero delegates lifetime to the parent) | 0 | 0 or 100..600000 |
| `handshake_timeout_ms` | 10000 | 100..60000, at most a nonzero selection timeout |
| `receive_buffer_bytes` | 4096 | 256..16384 |
| `max_payload_bytes` | 16384 | 96..65525 |
| `max_string_characters` | 1024 | 1..4096 |
| `listen_backlog` | 1 | 1..8 |

Each selector accepts exactly one bootstrap connection and closes its listening socket. Frame buffers,
control frames, rows, effects, diagnostics and waits are bounded. There is no timer polling loop or
retained completed-connection history.

## Experimental native automatic Center

The optional `automatic_entry` configuration adds a real, distinct loopback Center endpoint to the
directory. The audited protocol adapters are **kr-812** and **cn-324**. They cannot be combined with `selector_identity` or
presentation metadata. Other builds reject this capability. Omit the option to retain existing behavior.
Even one physical worker uses a bootstrap when this automatic entry is requested. Compiled protocol
availability is distinct from parent enablement and original-client acceptance.

The capability string is `bf-native-automatic-center-v1`. Its required fields are `id`, `name`,
`native_center_id`, `selection_policy: "lowest-rtt-then-load"`, `launch_id`, `grant_id`,
`grant_expires_at_utc`, `admitted_center_ids` and `candidates`. Each ordinary center
must also supply its actual `backend_id` and `native_center_id`. Native identities, backend mappings
and physical endpoints must be distinct; aliases do not create extra workers. The automatic entry
occupies one additional directory entry. READY retains the ordinary bootstrap endpoint format.

`launch_id` contains 32 hexadecimal characters. `grant_id` is 1..128 printable ASCII characters
without spaces. The original grant expiry is immutable. `admitted_center_ids` is a distinct, nonempty
subset of the configured physical center identities admitted for this login grant; it cannot contain
unrelated manual-only destinations. This pool stays fixed even when the active snapshot is empty.

Each candidate references this pool through `center_id` and requires `healthy`, `rtt_us`,
`users`, `capacity`, `observed_at_utc`, `status_revision` and `expires_at_utc`. RTT is an integer in 0..60000000 microseconds; population is
nonnegative and cannot exceed its positive capacity. The parent supplies an authority-admitted,
launch-time snapshot, with actual health, measurements and expiry. It must verify that the same login
credential is valid for every candidate and that their data scopes satisfy the automatic policy.
Gateway does not manufacture a lease, resolve DNS, probe workers, issue tickets or redeem credentials.
Source timestamps use RFC3339. Supply the actual source observation, never the directory fetch time;
measurement timestamps are diagnostic metadata and are never compared with the user computer clock
to gate status or login. A reported healthy heartbeat remains eligible until the parent withdraws it
or reports an explicit health failure; the original login grant still bounds admission.
`status_revision` is an opaque 1..128 character ASCII token (`!` through `~`); Gateway does not order it
or assume that source clocks increase. Omit never-observed nodes from `candidates` rather than
manufacturing missing timestamps or revisions. They can appear later only within the original pool.

Unhealthy and full workers are excluded at native status and selection. A past measurement expiry or
clock difference, including 19 seconds, does not make a healthy worker unavailable. An empty initial
snapshot is valid and can be refreshed before the client queries the automatic endpoint. If there is
no eligible worker when queried or selected, Gateway waits for a parent refresh for at most
`handshake_timeout_ms`, capped by grant expiry and any accepted selection deadline. A single snapshot
signal wakes the pending request without polling or accumulating messages. Login payload bytes are
erased before this wait. If the window expires, Gateway explicitly logs unavailability and closes only
the automatic auxiliary connection; the manual bootstrap remains available. That auxiliary retirement
is terminal for this launch: later refreshes return `finished`, and restoring Automatic requires a new
launch. Native UI behavior on auxiliary closure remains an original-client acceptance requirement. Among eligible workers the
policy chooses lowest RTT, then lowest population/capacity ratio, then ordinal center identity.

The parent can supply `automatic_entry.unavailable_capacity` (1..1073741823), derived from the
maximum real capacity in the admitted Automatic targets, independently of fresh health measurements.
With this field, a status request with no eligible candidate immediately receives the configured
`name`, native identity, this capacity and an explicit unavailable occupancy sentinel of twice this
capacity. This sentinel is not a measured player count: cn-324 `sub_408AE0`/`sub_408800` require a
ratio above one to prevent selection. A healthy candidate instead supplies its actual population
and capacity. An auxiliary that has advertised unavailability remains unavailable for its lifetime;
uniqueness retires it normally and login closes it without any redirect, even after a health refresh.
After the main directory flow ends, unaccepted unavailable listeners stop accepting connections.
Already accepted unavailable auxiliaries retain their cipher state and complete their own uniqueness
reply and client EOF before Gateway exits. Their aggregate retirement grace is bounded by
`handshake_timeout_ms`; expiry is an explicit retirement error. Parent STOP and control-pipe EOF
still cancel every socket immediately, so parents must allow native retirement before normal STOP.
Manual selection remains available. Omit this field only for compatibility with the earlier bounded
freshness-wait behavior. Neither status reporting nor this capacity admits a worker or renews a lease.

After READY the parent can send a full candidate replacement using the existing framed control pipe:

```json
{
  "type": "refresh-automatic",
  "row_id": "selector",
  "launch_id": "0123456789abcdef0123456789abcdef",
  "grant_id": "issued-grant-identity",
  "revision": 1,
  "candidates": []
}
```

Each update receives one framed response, for example:

```json
{"type":"automatic-refresh-result","row_id":"selector","revision":1,"status":"accepted","reason":"updated"}
```

`revision` is a positive Int64 parent-local snapshot counter. For a matching binding, every increasing
revision is consumed even when its candidate snapshot is rejected; rejection leaves the previous
snapshot in place under the original grant. Empty snapshots withdraw all automatic candidates.
Duplicate candidates, unknown members and invalid measurements are rejected
atomically. Rejected reasons are `binding`, `revision`, `grant-expired`, `candidates` or
`unsupported-row`. A successful selection or auxiliary retirement freezes the decision; later updates
with a matching binding and positive revision return `status: "finished", reason: "retired"` without
altering the destination or aborting handoff. STOP remains `{"type":"stop"}` without a response.

The parent owns one bounded refresh loop, one outstanding request and one ACK reader. Serialize all
pipe writes, including STOP. Refresh actual observations during slow resource preparation and game
startup, intersect them with the immutable issuer grant, and preserve backend, native identity and
endpoint bindings. A failed fetch cannot renew previous source data. Cancel and await the loop before
STOP or lease disposal, and stop refreshing on `finished` or process exit. `connect_timeout_ms: 0`
delegates both listener accept waits to this parent; it must enforce a finite startup budget and the
original grant expiry, then send STOP on failure, deadline, game exit or grant expiry. Default callers
retain a 30000 ms accept deadline. No game-start message or additional control listener is required.

The separate automatic listener accepts one auxiliary socket, sends its native greeting and answers
the empty Center status query. Its population/capacity describe the currently chosen candidate.
A native uniqueness query returns true to retire an unselected auxiliary; it is not a health reply.
Only a bounded, structurally valid, nonempty-credential login request **after status** triggers
the ordered move-offer and empty pending-promotion frames. It sends no successful login reply and
opens no connection to a worker. The client retains its own credential string and performs worker
login. Diagnostics contain no packet bodies or credential text.

`automatic_entry.redirect_timeout_ms` defaults to 10000 and accepts 100..60000. It bounds source
release after redirect or uniqueness cleanup. `automatic_entry.max_login_adapters` defaults to 32
and accepts 1..256. Existing payload/string/backlog bounds apply to both sockets. Automatic accept
uses `connect_timeout_ms`, or the parent launch budget when zero; its first status is bounded by the
handshake timeout. Parent STOP or pipe EOF cancels both listeners and their accepted sockets.

After a validated selected login, a successfully written redirect burst and clean automatic-source EOF
retire this row's remaining bootstrap connection. Expected internal bootstrap cancellation completes
normally, allowing Gateway to exit when all rows are finished. Unselected uniqueness cleanup, status-only
source closure and automatic unavailability preserve manual bootstrap. A failed redirect or incomplete
source release remains an error. Retiring this bootstrap is a transport lifecycle result, not proof of
worker authentication, ticket redemption or entry into the main menu; it does not close worker sockets.

Original kr-812 consumers establish this conditional path: `kr-812 sub_81B680` creates pending,
`kr-812 sub_81C550` promotes it, `kr-812 sub_8178B0` schedules LogoStage through `kr-812 sub_8E1240`/`kr-812 sub_8DE280`,
`kr-812 sub_682850` returns to LoginStage through `kr-812 sub_8DDF00`, and `kr-812 sub_680CA0` can invoke `kr-812 sub_681500`
on the new current connection. The previously selected flag suppresses the chooser condition in
`kr-812 sub_681FE0`. This requires a fresh pre-login identity, retained credentials, native region/feature
conditions and promotion before target patch processing. Independent TCP streams have no shared
arrival ordering. Original-client scheduling, target failure, normal worker login, one-use ticket
redemption and chooser behavior require their own acceptance. A recorded kr-812 client run completed
worker authentication, character creation, plaza entry and room creation. It used the preceding
lifecycle implementation; native acceptance of automatic bootstrap retirement remains separate.

CN2012 uses its own native S2C 8 move offer and S2C 9 `gspDisconnectMovedUser` promotion, established
by `cn-324 sub_714540`, `cn-324 sub_705240`, `cn-324 sub_705710` and `cn-324 sub_705980`. Its auxiliary
first sends one empty `gcpStartGPK` before status (`cn-324 sub_7001F0`, `cn-324 sub_718480`); duplicate,
nonempty or post-status markers are errors. The C2S 3 login body contains three strings and a counted
IPv4-word vector without the KR trailing words (`cn-324 sub_583D70`, `cn-324 sub_583DD0`).

The CN2012 pending constructor does not copy the current credential. Its target patch schedules
LogoStage and a new LoginStage (`cn-324 sub_7034B0`, `cn-324 sub_788740`, `cn-324 sub_585C90`). The
selected flag suppresses a second chooser, but normal authentication must deliver credentials again
through the new stage's provider callback (`cn-324 sub_584580`, `cn-324 sub_5852D0`). A parent must
supply the necessary login integration and complete original-client validation before enabling this
route. This adapter does not install selector, presentation or automatic-routing hooks, and its wire
coverage does not establish a successful native CN2012 handoff.

## Adapter and deployment scope

All fourteen manifest builds implement native directory delivery, auxiliary status and uniqueness,
selected-login validation, move offers and pending promotion. CN735, CN774, TW454 and KR1256 also
require the native empty source acknowledgement before the final source-close reply. The remaining
legacy builds release the old source without that acknowledgement. Build-specific opcodes and packet
shapes are compiled from the canonical profiles and packet tables.

The optional native dynamic-configuration exchange has a 32-bit mask on KR1256, a 16-bit mask with
ten checksum categories on CN735, and a 16-bit mask with sixteen categories on CN774 and TW454.
CN774 and TW454 send this request before first-connect. Gateway validates checksum presence and
returns a zero mask, preserving the client's configuration, with the same continuous outgoing cipher.
Malformed bodies fail during both handshake and selection. No invented keep-alive traffic is sent.

`FantasyTale.Gateway.capabilities.json` is generated beside the executable from the same manifest:
`schema_version: 1`, product `Fantasy Tale Gateway`, and separate `bootstrap` and `automatic` build
arrays. It records protocol support only. It grants no deployment permission or additional workers.
The upstream Center contract is `center: {enabled: true, mode: "gateway"}`; absent or disabled Center
does not start Gateway. An enabled Center with omitted mode defaults to Gateway; explicit `native`
is owned by the server. The parent enforces that policy and starts this executable only when authorized.

The parent remains responsible for obtaining real eligible endpoints and correctly scoped credentials,
starting Gateway before launching the game, and keeping the control lease until selection finishes.
Target-server support, online worker availability and successful authentication are separate from
adapter coverage. CN2012's native login integration is owned by the Launcher. No selector, click or
routing hook is installed by Gateway. Future messenger work remains inside this executable, unimplemented.

## Data and AOT build

The runtime shares the existing wire cipher/framing library. `tools/GatewayData` is a build tool, not a
second product or deployed process. It uses the existing profile composer, opcode loader and packet
table resolver to compile only selected data into the executable. No YAML parser, server kernel or
build tool ships with Gateway. Build without `local.patch.yaml`; environment overrides are excluded
from generation. Build identities and native automatic promotion packet names are selected in
`profiles/bf-center-selector.json`; express layout
differences in the repository's `packets/` data and identify experimental projections explicitly.

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
dotnet test tests/FantasyTale.Gateway.Tests -c Release
dotnet publish src/FantasyTale.Gateway -c Release -r win-x64 --self-contained true `
  -p:PublishAot=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/gateway/win-x64
```

Use a fresh publish directory. The only runtime executable is `FantasyTale.Gateway.exe`.
Distribution packages also include the repository's MIT license and the matching Native AOT runtime
license and third-party notices. Retain the notices for any included package dependencies; identical
notice files may be shared within the enclosing Launcher package. License files are not runtime inputs.
Use `win-arm64` for ARM64 Windows. A 32-bit game can connect to either architecture through TCP;
Gateway is not an in-process game DLL. Building requires the .NET 10 SDK and native C++ linker and
Windows SDK for the target architecture; end users need neither SDK nor a .NET installation.

```powershell
tools/GatewayData/Smoke-Gateway.ps1 -Executable artifacts/gateway/win-x64/FantasyTale.Gateway.exe
dotnet run --project tools/GatewayData -c Release -- --smoke . artifacts/gateway/win-x64/FantasyTale.Gateway.exe
dotnet run --project tools/GatewayData -c Release -- --smoke-automatic . artifacts/gateway/win-x64/FantasyTale.Gateway.exe
```

The smoke script runs the published executable, exercises STOP and pipe EOF, checks for a window, and
reports launch-to-READY time, working set, private bytes and CPU time. These are machine-specific
observations affected by warm launches, antivirus, CPU architecture and x64 emulation. Encrypted
loopback tests cover all 14 adapters and their six-choice mappings, fragmented packets, repeated keep-alives,
actual endpoint bytes, version mismatch,
malformed packets, failure propagation, cancellation, deadlines and parent EOF. Synthetic tests do not
establish acceptance in the original game's UI or successful target login.

This change supplies the executable and integration contract inside this repository. Wiring the
Launcher catalog and launch transaction to this contract, and original-client acceptance, remain
integration steps. No Launcher source is changed here.

## Failed manual destinations

The parent preserves every directory member. After a bounded connection or native status probe proves
failure, it supplies that member's actual capacity as `centers[].unavailable_capacity` (1..1073741823)
and its positive `native_center_id`. Gateway substitutes one loopback status-only endpoint for that
slot, retaining its order, catalog identity and exact name. Other slots keep their physical endpoints.
This contract works without Automatic or a data scope; it cannot be combined with the selector identity
bridge. A single failed member still uses bootstrap mode, rather than returning a direct endpoint.

Only marked failed members may retain a bounded ASCII DNS hostname in `host` when resolution itself
failed. Gateway neither resolves nor connects to it. Unmarked members still require canonical IPv4.
The parent must distinguish proven failure from an unmeasured or unknown state. A connection that
fails after the immutable native directory has been sent cannot be replaced within that directory;
rebuilding the launch repeats the bounded probe and projects the resulting state.

Each local status response uses the original name and native identity, the supplied capacity and
`users = 2 * capacity`. This is an unavailable sentinel, not a population measurement. CN2012's
`cn-324 sub_408AE0` marks ratios strictly above one as state 3, and `cn-324 sub_408800` rejects that
state's click. The canonical per-build CenterStatus layout supplies the wire fields; CN2011 omits
the name and continues to use its client resources. The parent does not receive fabricated online
observations. Uniqueness retires an unselected socket; a validated forced login closes without an
authentication reply or redirect, and received payloads are erased.

A failed member remains in immutable `admitted_center_ids` when originally authorized. It cannot
appear in initial or refreshed active candidates during this launch. Recovery requires a new launch
because these status-only endpoints remain disabled for their entire lifetime. A manual entry without
automatic authority remains manual and is never added to that admitted pool. There is no fixed 32-entry limit; Automatic adds one slot when configured. Parent shutdown, bootstrap completion or selected
Automatic handoff cancels all remaining status listeners and sockets; an unselected status closure
alone preserves the bootstrap.

The parent may pass `--control-max-bytes <bytes>` after `--pipe <name>`, sizing the byte budget for
its complete launch configuration, READY mappings and permitted refresh snapshots. Without this
argument, the earlier 64 KiB control budget remains for compatibility. Reads allocate only the declared
body after validating that per-launch budget. Neither directory members nor Automatic membership are
truncated by count. Active snapshots are bounded by the actual immutable admitted pool. Native frames retain their per-build length encoding: unsigned 16-bit lengths for the five- and ten-byte
headers, and an unsigned 32-bit length for the eight-byte header. An unencodable directory fails
explicitly rather than silently dropping entries. Tests above 32 establish transport behavior, not native UI acceptance.
