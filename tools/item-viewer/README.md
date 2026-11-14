# Item viewer

[Chinese](README.zh.md)

A standalone Python tool that previews shop and inventory items from an installed client and exports transparent PNG
images by default, with WebP available for smaller exports. It reads the client's .pkn/.bfz archives directly, renders 3D items with the client's own shop camera
(`Config/ShopItemCamera.csv`) and shows 2D icons on the same card layout. Supported builds: kr-812 and kr-1256.
It imports nothing from the server.

```sh
python -m venv .venv
.venv\Scripts\pip install PySide6 numpy Pillow pytest
.venv\Scripts\python -m pytest -q
run_gui.bat <client folder>
```

Extracted game files, caches and exports (`.cache/`, `output/`) are proprietary and never committed.
Accessory previews replace the item's own motion root with the authored socket attachment and load only
the registered socket meshes. Missing attachment nodes or registered meshes are reported as errors.
The pack uses a native item icon before a model when the client supplies both.

## Command-line exports

```sh
python -m bf_item_viewer export <client-folder> --items 201001 --output output/single
python -m bf_item_viewer export <client-folder> --items 201001 --output output/single --format webp --lossless
python -m bf_item_viewer atlas <client-folder> --filter kind=weapon,status=Renderable --output output/atlas --format webp --quality 85
python -m bf_item_viewer pack --games-dir <clients-directory> --builds kr-812 --output output/pilot --jobs 1
python -m bf_item_viewer pack --games-dir <clients-directory> --output output/preview-pack --jobs 2
```

`export` and `export-all` default to PNG. `atlas` and `pack` default to WebP. Both WebP modes preserve
alpha. `--quality` controls lossy WebP, and `--lossless` preserves all RGBA bytes. `atlas` writes
`atlas-index.json` alongside its images, with the page and pixel crop for each preview.

`pack` uses the relative client folders in `bf_item_viewer/data/builds.json`. Supply their common directory
with `--games-dir` or `BFIV_GAMES_DIR`. Omit `--builds` to select every mapped build. It writes the shared
resource index `pack.json` and one metadata index per build. Preview cells are 328×240, and exact RGBA
matches share an atlas position across builds. An indexed item without a render has null atlas and cell
values. For launcher consumption, use the default WebP format.

Use a fresh output directory for each deliverable. The separate `--work-dir` (by default `<output>-work`)
holds resumable render inputs and statistics, including skipped builds, render errors, and the three
image-format sizes. The cache is invalidated when renderer source, relevant input resources or export
settings change. Reuse the cache directory across runs with the same settings.

After rendering, report preview availability separately from the public pack format:

```sh
python tools/report_availability.py --work-dir output/preview-pack-work --games-dir <clients-directory> --output output/availability.json
```

The report keeps item ids in three exclusive groups: `not_rendered` for design exclusions,
`unavailable_native` for missing resources in the selected client, and `render_failed` for decode or
render failures. It reads the captured catalog and index without rescanning or changing render records.
For a separately captured client view, pass matching `--client-root <build>=<folder>` and
`--client-cache <build>=<index-cache-parent>` overrides. `--renderer-root` and `--renderer-sha256` pin an
archived renderer when the report runs from another checkout. A missing requested resource is checked
against the selected root; a staged archive failure is not assumed to be a missing native asset.

## Reviewing and repairing an existing pack

Scan suspected previews without decoding the whole pack into memory:

```sh
python tools/audit_pack.py output/preview-pack --builds kr-812,kr-1256 --output output/review
```

The report and contact sheets identify blank pictures, small coverage, off-centre bounds, edge contact
and distant components. These are observations requiring review: native costume close-ups often touch
the edge, and an incorrectly rotated accessory can still have centred bounds. Keep client pictures,
review sheets and resource evidence outside Git. `--font` optionally supplies a local label font.

For a reviewed subset, prepare replacement pictures and an explicit JSON plan outside the package:

```json
{
  "baseline_pack_sha256": "<SHA-256 of the original pack.json>",
  "renderer_sha256": "<replacement renderer source fingerprint>",
  "replacements": [
    {"build": "kr-812", "id": 100032, "kind": "cell", "image": "reviewed-preview.png", "reason": "authored attachment root replacement"}
  ]
}
```

Use `kind: "icon"` with the original native PNG when the client selects an icon. Image paths are relative
to the plan directory. Each rendered cell must match the baseline's cell size and have nonempty alpha.

```sh
python tools/repair_pack.py output/preview-pack output/replacements/plan.json --output output/candidate --receipt output/repair-receipt.json
```

The output must be fresh. Repair appends deduplicated lossless atlases or original PNG icons and changes
only the selected picture references. It verifies every retained file byte for byte, every unselected
item reference, and all text, gauges, allowed characters, sets and character metadata. Shared baseline
cells remain intact. The original generator describes retained pictures; `generator.repair` separately
pins the baseline, reviewed plan and replacement renderer. The receipt lists changes and per-build
replacement/preservation counts. This is a mixed-provenance repair, not a full new render or a cache hit.
