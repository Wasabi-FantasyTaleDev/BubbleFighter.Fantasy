# Rules for AI agents and contributors

## Build and test

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release   # must produce 0 warnings
dotnet test BubbleFighter.Fantasy.sln -c Release
dotnet run --project src/BubbleFighter.Fantasy.Host -- run --profile kr-1256 --dump-config
```

`TreatWarningsAsErrors` is on; do not suppress warnings to make the build pass.
`GenerateDocumentationFile` is on: **every public type and member needs a real XML
`<summary>`** — no boilerplate, no auto-generated filler.

## Architecture rules

- **Everything is a plugin.** There is no privileged core. New capability ships as a
  plugin row in a profile, not as a special case in the kernel or host.
- **Context is the service registry.** Consume services by interface type only. Never
  reach into another plugin's concrete classes.
- **Registrations are effects.** Every `Register*`/`On*` returns `IDisposable`; hand it to
  `context.Effect(...)` so the row unloads cleanly in reverse order.
- **No hard-coded tunables.** Anything that varies per deployment is a validated config
  field on the plugin row (snake_case YAML key to PascalCase property). Protocol constants
  belong in code, documented with their evidence. The exception is a client build's packet
  layouts, which are data under `packets/` (see "Packet structures are data" below).
- **Fail loud.** Misconfiguration, missing services, malformed packets, and connection
  failures are errors at load/close time, never silent defaults or dropped data.
- **Bounded resources.** Never create unbounded queues, buffers, or file sinks.
- **Exclusive writes.** A field's meaning is derived from the client itself: the decompiled
  reader or writer that consumes it, or the unpacked game data that feeds it. What the server
  sends and in which order is decided by the client's consuming path — its handler, its state
  check, its UI callback. Behaviour that only exists because some other reimplementation does
  it is never a source: field order, widths, defaults and codecs are never copied, and a
  name is never borrowed from a project that did not build this game.
- **One logic plugin, many client builds.** Game logic handles typed messages; opcode
  numbers, codecs, and framing live in the per-build dialect layer. Behavior that differs
  between builds is selected by build range or capability, never by scattered version
  checks inside handlers.
- **A legacy build is its differences from the baseline.** kr-812 is the baseline of every
  build whose packets keep their class names. Its setup is `profiles/bundles/legacy.yaml`, written
  once; another legacy build is a profile folder that lists that bundle and states only what
  differs: its port and values in `profile.yaml` and `patch.yaml`, and its full `opcodes.yaml`. What a
  build does not state is its base's, so a difference that is not written down is a bug in the data,
  not a reason to copy code for that build.
- **Packet structures are data, in `packets/`.** A structure is written once in a `packets/*.yaml`
  file as kr-812 has it, one named field per line with its client offset and what is known about it;
  a build that differs says so beside the field (`except`, `only`, `also`, or a replacement under
  the build's name), or restates the structure as `Name@build` when it differs in more than about
  eight places. `packets/builds.yaml` says which build is based on which. The format and its rules are
  `packets/README.md`; read it before touching a layout. In particular:
  - Never add a per-build field table, shape record or `if (build ...)` to code for something the
    tables can say. Code supplies values by field name; the tables say where they go.
  - A field without a proven purpose is `unknown_<offset>`. Do not invent a meaning to get a nicer name.
  - Every `sub_` address in a comment is prefixed with its client build (`id-400 sub_5F1FF0`): the
    same address is a different function in every build.
  - Comments in `packets/` are English and as detailed as the evidence allows.
  - A packet layout that is still hand-written in C# is a candidate to move into `packets/`, not a
    pattern to copy. New legacy layouts go into the tables.
  - Check a change with `bff packets show <build> <structure>` and `bff packets diff <a> <b>`.
- **Read the client before writing the wire.** Every layout and every ordering the server
  depends on is established by reading the client's own code, and each claim carries the
  function it came from. A wire change that alters what the server sends is checked against
  the client's consuming path before it is committed: the reader, the dispatcher case, or the
  state check that expects the frame. Where two readings disagree, the client wins and the
  disagreement is written down rather than smoothed over.

## Opcode policy (summary — full rules in profiles/opcode-policy.md)

- Every packet codec documents its full field list and the client function address it came
  from. Unknown fields are named `unknownN`, never invented.
- Evidence grades: `verified` / `inferred` / `unverified` / `captured` in `opcodes.yaml`;
  `name_source` records where a name comes from (`label@<profile>` / `mirrored` / `coined` / `none`).
- Names, in order of preference: the build's own label; a label from another build whose
  builder or handler is matched to this one (Korean builds kr-1256 and kr-812 first); a
  coined name for a packet whose purpose the client's own code proves, ending in `_`
  (`gcpReqXxx_`); `UnknownNNN` when the purpose is unknown.
- Every codec has a byte-length-pinning test.
- Unknown inbound opcodes are logged, never parsed optimistically.
- C2S and S2C are separate numeric spaces, and so are the game and relay connections
  (`connection: game|relay|both` in `opcodes.yaml`; resolve names through `ForProtocol`).
- Every client function address a comment or a table cites is prefixed with its build
  (`kr-812 sub_810C20`): the same address is a different function in every build.
- A row whose evidence came from a test session rather than from the client's code stays at
  the `captured` grade.

## Contribution rules

- Languages. No emojis anywhere.
  - English only: code, comments, XML docs, commit messages, `opcodes.yaml` and
    `profiles/opcode-policy.md`.
  - The published guides in `docs/` are listed in `docs-site/pages.json`. `X.md` is the
    canonical English text; `X.zh.md` (Simplified Chinese), `X.zh-tw.md` (Traditional
    Chinese) and `X.ko.md` (Korean) are its translations, updated in the same commit.
  - The README and contribution guide are English plus Simplified Chinese.
  - Release notes are bilingual: `CHANGELOG.md` and `CHANGELOG.zh.md`.
- Never cite or name third-party projects, other servers, or reference trees in code,
  comments, docs, tests, or commit text — describe designs in our own terms.
- Comments say why the code is the way it is and point at the evidence (a build-qualified
  client function address), in one or two lines. A test session that grades an opcode row
  `captured` is named in that row, not in code comments. Investigation history and A/B runs
  belong in commit messages, not in code.
- Work lands on `main` through reviewed pull requests from a separate branch.
- Never commit credentials or secrets. Use `${VAR}`/`${VAR:-default}` placeholders in
  profiles and document variables in `.env.example`.
- `profiles/*/local.patch.yaml` is the git-ignored developer override slot; do not commit it.
