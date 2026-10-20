# Packet tables

This folder describes what the game clients' packets look like. It is read when the server starts and
is not compiled into it: change a file, restart, and the server writes the new layout.

Chinese: [README.zh.md](README.zh.md).

| File | What it holds |
|---|---|
| `builds.yaml` | The client builds, what each is based on, and each build's own values. |
| `room.yaml` | The room structures: `SessionSetting`, `GameSetting`, `UserSlot`, `EquippedItemList`, `gspGotoRoom`. |
| `room-details.yaml` | Audited master-change requests/replies, round-setting requests, room inventory notifications and the shared amplifier request; round-map bindings also require the named game opcode and audited target mapping in `builds.yaml`. |
| `score.yaml` | The scoreboard: `ScoreRow`, one seat of `gspGameBroadcastPlayerScoreData`. |
| `score-reports.yaml` | Native inbound score envelopes; table coverage does not enable unreviewed event kinds or opcode routes. |
| `inventory.yaml` | `InventoryUpdate` and `InventorySlot`: the bag on S2C 801/802. |
| `equipment-attributes.yaml` | Audited `ItemAttr` and `ItemExpiredTime` element layouts for complete equipment vectors. |
| `cash-inventory.yaml` | Counted cash-inventory slots and pickup bodies, with the modern presentation tail. Tables do not enable item claims. |
| `login.yaml` | `LoginReply`: the legacy S2C login response, including early-client differences. |
| `weapon-presets.yaml` | Legacy preset requests, status and parallel-vector bars; modern update envelopes with distinct KR, CN and TW entry widths. Table coverage does not enable runtime preset behavior. |
| `customized-keys.yaml` | Audited customized-key updates and replies for kr-812 and three modern builds; the explicit `player-settings` bundle binds them to scoped storage and login restoration. |
| `user-stat.yaml` | `UserStat` (S2C `gspUserStat`, the wallet and FighterInfo) and its `FighterInfo` and `EloRating`. |
| `fighter-info.yaml` | `FighterInfoRequest` and `FighterInfoReply`: the player menu's "view info" (C2S `gcpReqFighterInfo`, S2C `gspRepFighterInfo`) of the legacy builds and of the kr-1256 family (`FighterInfoReply@kr-1256`). |
| `clan.yaml` | The clan game-connection bodies (`ClanPassport`, `ClanMarkInfo`, `ClanGuildInfo`, `ClanAttendanceState`) and the empty clan request frames, with the kr-1256 shapes stated beside the kr-812 baseline. |
| `relay-connection.yaml` | Audited native relay endpoints, keys, offers, joins and connection reports. Table presence does not enable a relay listener or handshake. |
| `relay-validation.yaml` | Native post-join validation reports and signature data; CN2011 uses a direct-success sequence instead. |
| `relay-success.yaml` | Empty native join/scene success signals; does not initialize scene seed tables or enable listeners. |
| `relay-seeds.yaml` | Audited native relay seed-table structures; table presence does not enable seed handling. |
| `damagers.yaml` | In-round damager bodies the server relays unchanged, such as `SplashDamagerByDamager` (32900) and the seven spawns of `packets/damagers.yaml`. |
| any other `*.yaml` | More structures. Every file in this folder is read; a plugin can ship its own. |

Every file starts with `version: 1`. That is the version of this file format, not of the game: the
server refuses a file whose version it does not know, so the format can change later without old files
being misread.

## Build identity

Build identifiers use `<region>-<wire version>` and match profile directories. `builds.yaml` records layout inheritance and capabilities. An inherited layout or enabled capability describes data coverage; it does not establish real-client acceptance. Deployment ports belong in profile configuration.

## One table for every build

A structure is written once, as the baseline build (`kr-812`) has it, one field per row in wire order:

```yaml
structs:
  SessionSetting:
    - session_state: u32
      offset: 4
    - room_id: u16                  # room number
      offset: 8
    - open_seats: u16
      offset: 44
      except: [id-400]              # id-400 has no such field
```

Every build has every field unless a row says otherwise. A row can say four things:

| Key | Meaning |
|---|---|
| `except: [a, b]` | The listed builds do not have this field. |
| `only: [a]` | Only the listed builds have this field. |
| `also: [a]` | The build has the field again although the build it is based on does not. |
| `a:` followed by a list of fields | The build has those fields here instead of the row's own. |

```yaml
    - clan_name: wstr
      offset: 180
      id-400:
        - unknown_180: u32          # a u32 where kr-812 has the string
          offset: 180
          default: 1000
```

What a row says about a build also holds for every build based on that build (`builds.yaml` says which
is based on which), until a row names the nearer build. So a build that is not mentioned anywhere is
exactly its base, and adding a build that differs in three fields means writing three annotations.

### When a build differs a lot

A build that differs in many fields of a structure restates the whole structure next to the shared one:

```yaml
  UserSlot@cn-324:
    - occupied: bool
    - closed: bool
    ...
```

The builds based on `cn-324` then follow that table, and annotate it the same way. As a rule of thumb,
restate a structure when a build needs more than about eight annotations in it. A restated table is a
copy: a field renamed in the shared table has to be renamed there by hand.

## Using a structure from code

A plugin writes a structure with `PacketCodec.Write(tables, build, "UserStat", values)`, where `values`
maps field names to values. A field without a value carries its `default`. A nested structure takes its
values from a map under its own field name, or else from the same map as its parent, so a caller can fill
`nickname` without knowing that it sits inside `FighterInfo`. A build the tables do not name uses its
nearest ancestor's tables (`PacketTables.NearestBuild`).

For inbound flat scalar layouts, `PacketCodec.ReadScalars` returns fields by name using the same
build resolution. It requires the entire body, rejects trailing bytes and limits strings before
decoding them (1,024 characters by default). Words preserve their bits as signed 32-bit integers.
Callers can enable `requireCanonicalBooleans` to reject bool32 words other than zero and one;
the default retains native nonzero truth semantics.
Nested structures, lists and fixed runs are explicitly unsupported by this reader; those need
`PacketCodec.Read` with audited tables rather than optimistic parsing.

`PacketCodec.Read` reads nested structures, counted scalar/structure lists and fixed runs using
the same build resolution. It returns nested field maps and element arrays accepted by the table
writer. It requires the whole body and rejects nonzero `empty_list` counts: an outbound placeholder
does not establish an inbound element layout. `PacketReadLimits` defaults to 1,024 characters per
string, 4,096 elements per collection, 16,384 total fields/elements and 16 nesting levels. These
bounds are checked before allocating the corresponding container; scalar collection widths must
also fit the remaining body. The native 65,535-byte body ceiling always applies. Callers can use
stricter limits and canonical booleans for their audited message. Reading a structure does not
register an opcode, grant a capability or authenticate a connection.

## A field

```yaml
- name: type          # what it is
  offset: 44
  default: 8
  vacant: 1
  menu: 8
```

Only the first line is required.

- **name** says what the server puts in the field. A name the server knows (for the room structures:
  `nickname`, `user_number`, `room_id`, `title`, `mode`, ...; the full list is the `RoomFieldValue` enum,
  in snake case, plus `seated`) is filled from the room. Any other name is written with the default. A
  field whose purpose is not known is called `unknown_<offset>`; rename it when its purpose is proven.
  Names are lower-case letters, digits and underscores, never spaces, and unique within a structure.
- **type** is `u8`, `u16`, `u32`, `u64`, `bool` (4 bytes), `f32`, `f64`, `wstr` (u32 character count,
  then UTF-16LE), `list<T>` (u32 count, then that many `T`), `empty_list` (a list the server always
  sends empty), a fixed run such as `u32[49]` (that many scalars without a count prefix),
  `ScoreRow[16]` (exactly sixteen structures without a count prefix), `optional<0x3A,Tail>` (one tag
  byte, then the structure `Tail` only when the byte is `0x3A`; a writer given no map writes the field's
  `default` as the absent tag, and a reader keeps the absent byte so the record writes back unchanged),
  or another structure's name.
  Supplied runs must have the exact length and may contain at most 65,535 elements. Scalar runs describe
  fields the client reads consecutively whose purpose is unknown; split a field out when its purpose
  is proven. Quote a type that contains `<` or `[`.
- **offset** is where the client's reader stores the field in its object. The server does not use it;
  it is there so the field can be found in the client code and is shown by `bff packets show`. In a
  restated table or under a build's name it is that build's own offset.
- **default** is written when the server has no value of its own (0 or an empty string when absent).
- **vacant** is what an empty seat carries instead.
- **menu** is what the field carries in the main menu's one-seat room, where that is not what a play
  room carries: a number, another field's name, or `empty` for a list.
- The **comment** says what is known about the field and where that is proven.

Every function address in a comment names its client build (`kr-812 sub_4196F0`), because the same
address is a different function in every build.

## Conditional fields

Use `when<mask&0x01,f32>` when a preceding unsigned scalar in the same structure determines whether
one field is present. The hexadecimal mask selects **any** matching bit: the field is present when
`(mask & 0x01) != 0`. This adds no tag or presence byte of its own. For example:

```yaml
  Delta:
    - mask: u8
    - x: 'when<mask&0x01,f32>'
    - name: 'when<mask&0x02,wstr>'
    - detail: 'when<mask&0x04,Detail>'
```

The inner type can be a scalar (`u8`, `u16`, `u32`, `u64`, `bool`, `f32`, `f64`, `wstr`) or an
existing structure name. A nested structure evaluates its gates against its own fields. Lists, runs,
tagged optional blocks and another `when` cannot directly be the inner type; wrap those fields in a
named structure when the client's layout calls for them. Table loading checks every resolved build:
the gate must precede the field, be an unconditional `u8`/`u16`/`u32`/`u64`, and the mask must be
nonzero and fit that width. A build override that removes the gate is therefore invalid too.

Both readers omit a skipped field's key. A present zero is kept as a value, so it cannot be mistaken
for an absent coordinate. `ReadScalars` supports conditional scalars; a conditional structure requires
`Read`. The writer requires an explicitly supplied gate and a non-null value for every selected field.
For a clear gate, omit the value or supply null; any non-null value, including zero, is an error.
The writer never derives the mask or silently writes a default for a selected field. Unsigned gates
retain their native bit patterns, including the signed `int`/`long` values returned by the readers.

`ReplaceStrings` skips absent conditional fields while preserving all other original bytes. It may
replace a present conditional string, but refuses to add a string whose gate is clear. `packets show`,
`diff` and `json` retain the complete `when<...>` spelling. This additive field type remains in format
version 1; older servers reject the unfamiliar type at load time instead of misreading it.

The six-bit regression fixture follows the conditional byte order in kr-1256 sub_10F4A10 (reader)
and kr-1256 sub_10F4930 (writer): three four-byte fields followed by three one-byte fields. Adding
this format support does not enable a movement binding or change any shipped packet layout.

## Looking at one build

Nobody has to lay a build's differences over its base in their head:

```sh
bff packets show id-400 UserSlot        # the structure as id-400 has it, with where each field comes from
bff packets diff kr-812 id-1809         # the fields in which two builds differ, every structure
bff packets check                       # every build, its base, and how much is stated for it
bff packets json id-400                 # everything about one build as JSON, for scripts
```

## What needs code and what does not

| Change | Recompile? |
|---|---|
| A build has a field more or fewer, a different order or width | no |
| A default changes; an unknown field gets a name the server does not fill | no |
| A new build that is close to an existing one | no |
| A field that was a constant must now carry real data | yes: code has to supply the value |
| A new packet or new behaviour | yes |

Structure is data; meaning is code.

Native feature support belongs under `capabilities` in `builds.yaml`. The baseline states each
audited capability; descendants inherit it and override only differences. Dialects use
`PacketTables.CapabilityOf(build, name)` before enabling a feature. Unknown or malformed values
are configuration errors. An enabled capability records native support, not client test acceptance.

## Extending the tables from a plugin

The reader is the library `BubbleFighter.Fantasy.Packets`. A plugin that has structures of its own puts
a file in this folder (any name ending in `.yaml`, starting with `version: 1`, with its structures under
`structs:`) and reads it with:

```csharp
var tables = PacketTables.Load(options.PacketsDirectory);
IReadOnlyList<PacketField> fields = tables.Resolve("id-400", "MyStructure");
```

`Resolve` returns the fields one build has, in wire order, with each field's name, type, default, offset
and the build it comes from. The reader accepts any structure name and any `list<...>` type; what a type
means on the wire is up to the plugin that writes it. A structure name may appear in one file only.

## Rules for editing

- Field order, widths and defaults come from the client reader or writer.
  Cite the reader's address, with its build, in the structure's heading comment.
- Do not invent meanings: a field without a proven purpose stays `unknown_<offset>`.
- A change that alters bytes for a build must be checked against that build's reader function, and the
  byte-length tests of the structure must still pass.

`PacketMenuProjection.Project` applies the selected structure's `menu` annotations to named source values. Numeric annotations override the matching source value; a field-name annotation reads the original source value before overrides. Nested maps and lists are supplied as separately projected scopes. Missing references are configuration errors, and unannotated fields without a source keep their table defaults.

`room_setting_change_fields` in `builds.yaml` maps the third string and first two flags of the native map-change request to their audited offset names. Builds inherit the baseline mapping and override only differing names. The room dialect validates mapping keys, target types and distinct flag targets before binding typed room requests.

`room_unchosen_map` in `builds.yaml` lists the `GameSetting` fields, and their values, that a room shows until its master picks a map: the create request carries the client's startup map, not a choice. Each name must be a `wstr` or `u32` field of the build's `GameSetting`; a build that restates `GameSetting` differently cancels the inherited value with `~`.
`room_unchosen_difficulty` (`mode`, `key`) shows that mode's own marker wherever the all-random entry would show `Random`. `room_setting_change_map` names the map change request's `name`, `key`, `scene` and `mode` fields and the four `GameSetting` strings the room shows them in; a scene with `mission_prefix` stays in every map string. `room_ai_seat` (`field`, `modes`) sets the lowest bit of a `SessionSetting` word for rooms of the listed modes.

`room_projection_fields` describes the modern typed waiting room's mode field, ordered map fallbacks and ordinary-room gate overlay. `clear_mask` removes only the audited blocking bits from `flags`; unrelated bits, including the round-map selector bit, survive. `clear_words` and `clear_booleans` name the remaining audited gates. The binding validates all aliases and their types before mounting. An inherited map whose aliases do not exist in a later build is an error, not permission to use the baseline offsets.

`inventory` in `builds.yaml` says how a build publishes its bag through the shared inventory binding. `envelope` is `plain` or `zlib` (the modern family wraps the 802/803/808 bodies in a `u32` compressed length and a zlib stream; the cash inventory is never wrapped). `rows` is `ranked` (the legacy baseline: ascending item id, empty rows dropped on a restatement, connection-relative day counts) or `mirror` (the stored slots in stored order with their own identity fields). `replaced`, `purchase` and `changed` are the ordered publication lists, built from these entry names: `bag` (the whole-bag packet over the character bag), `slots` (the slots packet over the character bag), `equipped` (the slots packet over the equipped bag), `changed` (the slots packet over the changed slots), `articles` (the empty article table) and `cash` (the empty cash inventory). The binding resolves every packet by name and refuses a list entry whose packet the build's table lacks.
