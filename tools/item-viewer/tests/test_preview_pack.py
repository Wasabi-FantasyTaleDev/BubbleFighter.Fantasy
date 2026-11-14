"""Pin preview encoding, atlas placement and the launcher resource manifest."""

import hashlib
import io
import json
from types import SimpleNamespace

from PIL import Image

from bf_item_viewer.catalog.model import CatalogItem, Kind, ModelRef, Status
from bf_item_viewer.export.atlas import CELL, build_atlas, cell_box, cell_hash, encode
from bf_item_viewer.export.batch import ExportOptions
from bf_item_viewer.export.pack import WorkDirs, assemble, input_key, render_build, renderer_source_hash
from bf_item_viewer.service import PreviewRequest


def test_webp_preserves_alpha_and_lossless_preserves_every_pixel():
    image = Image.new("RGBA", (4, 4), (51, 102, 127, 125))
    image.putpixel((0, 0), (200, 110, 70, 0))
    with Image.open(io.BytesIO(encode(image, "webp", lossless=True))) as decoded:
        assert decoded.format == "WEBP"
        assert decoded.convert("RGBA").tobytes() == image.tobytes()
    with Image.open(io.BytesIO(encode(image, "webp"))) as decoded:
        assert decoded.getchannel("A").tobytes() == image.getchannel("A").tobytes()
    other = image.copy()
    other.putpixel((0, 0), (0, 0, 0, 0))
    assert cell_hash(image) != cell_hash(other)


def test_atlas_crop_uses_row_major_coordinates():
    cells = [Image.new("RGBA", (3, 2), (i, 10, 20, 100 + i)) for i in range(5)]
    atlas = build_atlas(cells, columns=3, cell=(3, 2))
    assert atlas.image.size == (9, 4)
    assert atlas.cells == 5
    for i, expected in enumerate(cells):
        assert atlas.image.crop(cell_box(i, 3, (3, 2))).tobytes() == expected.tobytes()
    assert atlas.image.crop(cell_box(5, 3, (3, 2))).getbbox() is None


def test_pack_index_shares_exact_cells_and_keeps_native_categories(tmp_path):
    work = WorkDirs(tmp_path / "cache")
    work.make()
    shared = Image.new("RGBA", CELL, (10, 20, 30, 200))
    distinct = Image.new("RGBA", CELL, (11, 20, 30, 200))
    for image in (shared, distinct):
        image.save(work.cells / f"{cell_hash(image)}.png")

    def entry(item_id, image):
        return dict(id=item_id, cell=cell_hash(image) if image else None, name="道具",
                    description="Description", slogan="Slogan", detail1="One", detail2="Two",
                    main="Accessories", sub="HeadAccesory")

    def load(path):
        return json.loads(path.read_text(encoding="utf-8"))

    out = tmp_path / "pack"
    results = [
        {"build": "kr-812", "items": [entry(11, shared), entry(12, distinct), entry(13, None)]},
        {"build": "cn-717", "items": [entry(21, shared)]},
    ]
    stats = assemble(SimpleNamespace(builds=[]), results, out, work, lossless=True)
    pack = load(out / "pack.json")
    assert set(pack) == {"format", "generator", "cell", "atlases", "builds"}
    assert pack["format"] == 1 and pack["cell"] == list(CELL)
    assert pack["builds"] == {"kr-812": "builds/kr-812.json", "cn-717": "builds/cn-717.json"}
    kr = load(out / pack["builds"]["kr-812"])
    cn = load(out / pack["builds"]["cn-717"])
    assert kr["format"] == 1 and kr["build"] == "kr-812"
    assert kr["items"]["11"] == cn["items"]["21"]
    assert kr["items"]["11"]["sub"] == "HeadAccesory"
    assert kr["items"]["13"]["atlas"] is None and kr["items"]["13"]["cell"] is None
    assert (stats["unique_cells"], stats["shared_cells"], stats["cell_references"]) == (2, 1, 3)
    atlas, = pack["atlases"]
    data = (out / atlas["file"]).read_bytes()
    assert atlas["file"] == f"atlas/{hashlib.sha256(data).hexdigest()[:16]}.webp"
    with Image.open(io.BytesIO(data)) as decoded:
        assert decoded.size == (atlas["width"], atlas["height"])
        assert decoded.crop(cell_box(1, atlas["columns"], CELL)).tobytes() == distinct.tobytes()
    assert stats["pack_bytes"] == sum(path.stat().st_size for path in out.rglob("*") if path.is_file())
    assert all(value > 0 for value in stats["format_comparison_bytes"].values())


def test_input_cache_changes_with_renderer_options_and_resource_bytes(tmp_path):
    renderer = tmp_path / "rendering"
    renderer.mkdir()
    (renderer / "attach.py").write_bytes(b"first")
    before = renderer_source_hash(tmp_path)
    (renderer / "attach.py").write_bytes(b"second")
    after = renderer_source_hash(tmp_path)
    assert before != after

    files = {"gun.msh": b"mesh", "toon.dds": b"ramp"}
    session = SimpleNamespace(
        index=SimpleNamespace(exists=files.__contains__, read=files.__getitem__),
        camera_record=lambda _: SimpleNamespace(position=(0, 0, 0), quaternion=(0, 0, 0, 1), zoom=10),
        input_files=lambda _: ["gun.msh"],
        profile=SimpleNamespace(raw={"shading": {"toon": {"enabled": True, "ramp": "toon.dds"}}}),
    )
    item = SimpleNamespace(item_id=1, kind=Kind.WEAPON, raw={})
    key = input_key(session, item, PreviewRequest(1), before, {})
    assert key != input_key(session, item, PreviewRequest(1), after, {})
    assert key != input_key(session, item, PreviewRequest(1, supersample=2), before, {})
    files["toon.dds"] = b"different ramp"
    assert key != input_key(session, item, PreviewRequest(1), before, {})


def test_pack_uses_native_icons_before_models_and_reports_invalid_icons(monkeypatch, tmp_path):
    native = Image.new("RGBA", (7, 5), (50, 150, 200, 120))
    encoded = io.BytesIO()
    native.save(encoded, format="PNG")
    items = [CatalogItem("kr-812", item_id, "Fixture", "Accessories", "HeadAccesory",
                         Kind.SLOT_ITEM, Status.RENDERABLE, model=ModelRef("fixture", ["mesh.msh"]),
                         icon=icon) for item_id, icon in [(1, "native.png"), (2, None), (3, "broken.png")]]
    spec = SimpleNamespace(id="kr-812", base="kr-812", release="Fixture", text_column=None,
                           profile=lambda: SimpleNamespace(raw={}))
    build_map = SimpleNamespace(get=lambda _: spec, root=lambda _: tmp_path, builds=[])
    session = SimpleNamespace(load=lambda: None, profile=spec.profile(),
                              catalog=SimpleNamespace(items=items, main_categories=[], sub_categories=[],
                                                      fighters=[], problems=[]),
                              index=SimpleNamespace(read=lambda path: encoded.getvalue() if path=="native.png" else b"invalid"))
    import bf_item_viewer.export.pack as pack
    monkeypatch.setattr(pack,"load_build_map",lambda _: build_map)
    monkeypatch.setattr(pack,"ClientSession",lambda *args: session)
    monkeypatch.setattr(pack,"translations",lambda *args: {})
    monkeypatch.setattr(pack,"weapon_graph",lambda *args: {})
    monkeypatch.setattr(pack,"stock_sets",lambda *args: [])
    monkeypatch.setattr(pack,"armor_tables",lambda *args: {})
    monkeypatch.setattr(pack,"input_key",lambda s, item, *args: str(item.item_id))
    rendered = []

    def draw(s, item_id, *args):
        rendered.append(item_id)
        return Image.new("RGBA", CELL, (1,2,3,255)), {}

    monkeypatch.setattr(pack,"render_transparent",draw)
    result = render_build("kr-812", str(tmp_path), str(tmp_path/"work"), ExportOptions(), "fixture-source")
    assert rendered == [2]
    first, second, broken = result["items"]
    assert first["icon"] == cell_hash(native) and first["cell"] is None
    assert second["cell"] is not None and "icon" not in second
    assert broken["cell"] is None and broken["error"].startswith("icon:")
    assert result["failed"] == 1
    with Image.open(tmp_path/"work/icons"/f'{first["icon"]}.png') as saved:
        assert saved.convert("RGBA").tobytes() == native.tobytes()
