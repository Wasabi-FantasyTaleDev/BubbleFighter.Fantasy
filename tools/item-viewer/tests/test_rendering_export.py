"""Catalog parsing, camera presets, paint, transparent output, repeatability and batch cancellation."""

import json
import threading
from types import SimpleNamespace

import numpy as np
import pytest

from bf_item_viewer.catalog.spreadsheet import read_raw, read_sheets
from bf_item_viewer.rendering.camera import (CameraRecord, ViewSettings, build_camera, load_profile,
                                             parse_camera_table)
from bf_item_viewer.rendering.raster import DrawItem, Frame, Texture, draw, resolve

WORKBOOK = b"""<?xml version="1.0"?>
<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
 <Worksheet ss:Name="ShopItem"><Table>
  <Row><Cell><Data>itemId</Data></Cell><Cell><Data>itemName</Data></Cell><Cell><Data>parameter</Data></Cell></Row>
  <Row><Cell><Data>100064</Data></Cell><Cell ss:Index="3"><Data>4</Data></Cell></Row>
 </Table></Worksheet>
 <Worksheet ss:Name="FighterConfig"><Table>
  <Row><Cell><Data>FighterConfig:HorzTable</Data></Cell></Row>
  <Row><Cell><Data>[Field]</Data></Cell><Cell><Data>[Desc]</Data></Cell><Cell><Data>1</Data></Cell><Cell><Data>3</Data></Cell></Row>
  <Row><Cell><Data>name</Data></Cell><Cell><Data>d</Data></Cell><Cell><Data>Dao</Data></Cell><Cell><Data>Bazzi</Data></Cell></Row>
 </Table></Worksheet>
</Workbook>"""


def test_spreadsheet_sparse_cells_and_transposed_tables():
    rows = read_sheets(WORKBOOK, {"ShopItem"})["ShopItem"]
    assert rows == [{"itemId": "100064", "itemName": "", "parameter": "4"}]   # ss:Index skips a column
    raw = read_raw(WORKBOOK, "FighterConfig")
    fields = {r[0]: r[2:] for r in raw if r and r[0]}
    assert fields["name"] == ["Dao", "Bazzi"] and fields["[Field]"] == ["1", "3"]


def test_camera_table_parsing_reports_bad_rows():
    data = ("shopItemId,position,quaternion,zoom\n"
            '201001,"3.5,-1.3,0","0.1,0.6,0.7,-0.1",110\n'
            '201002,"1,2","0,0,0,1",40\n').encode()
    records, problems = parse_camera_table(data)
    assert records[201001].zoom == 110 and records[201001].position == (3.5, -1.3, 0.0)
    assert 201002 not in records and "201002" in problems[0]


@pytest.mark.parametrize("build", ["kr-1256", "kr-812"])
def test_camera_composition_is_resolution_independent(build):
    profile = load_profile(build)
    record = CameraRecord(1, (0.5, -2.0, 0.0), (0.0, 0.3826834, 0.0, 0.9238795), 60.0)
    pts = np.array([[0.0, 0.0, 0.0], [1.0, 5.0, -2.0], [-3.0, 10.0, 1.0]])
    small = build_camera(profile, record, 256, 256).to_screen(pts)
    large = build_camera(profile, record, 1024, 1024).to_screen(pts)
    assert np.allclose(small[:, :2] * 4, large[:, :2], atol=1e-6)
    origin = build_camera(profile, CameraRecord(1), 400, 300).to_screen(np.zeros((1, 3)))
    assert np.allclose(origin[0, :2], [200, 150])          # the model origin sits at the control centre


def test_camera_expand_keeps_scale_and_centre():
    profile = load_profile("kr-1256")
    record = CameraRecord(1, zoom=40.0)
    pts = np.array([[0.0, 0.0, 0.0], [2.0, 0.0, 0.0]])
    base = build_camera(profile, record, 300, 300).to_screen(pts)
    wide = build_camera(profile, record, 600, 300, canvas_scale=2.0).to_screen(pts)
    assert np.isclose(base[1, 0] - base[0, 0], wide[1, 0] - wide[0, 0])      # same pixels per unit
    assert np.allclose(wide[0, :2], [300, 150])


def test_official_flag_and_user_rotation():
    assert ViewSettings().official
    assert not ViewSettings((0.0, 0.1, 0.0, 0.995)).official


def _quad(texture, tint, blended=False):
    screen = np.array([[2, 2, 0.5], [30, 2, 0.5], [30, 30, 0.5], [2, 30, 0.5]], dtype=float)
    uvs = np.array([[0, 0], [1, 0], [1, 1], [0, 1]], dtype=float)
    tris = np.array([[0, 1, 2], [0, 2, 3]])
    return DrawItem(screen, uvs, None, tris, texture, (1, 1, 1, 1), tint, blended)


def test_dye_blend_uses_alpha_as_mask():
    tex = np.zeros((4, 4, 4), dtype=np.float32)
    tex[..., :3] = (0.0, 0.0, 1.0)                     # blue art
    tex[:, :2, 3] = 1.0                                # left half: artist colour
    tex[:, 2:, 3] = 0.0                                # right half: dye
    frame = Frame(32, 32)
    draw(frame, _quad(Texture(tex), (1.0, 0.0, 0.0)))
    out = resolve(frame, 1)
    assert tuple(out[16, 6, :3]) == (0, 0, 255)        # alpha 1 -> texture colour
    assert tuple(out[16, 26, :3]) == (255, 0, 0)       # alpha 0 -> spray tint
    assert out[16, 26, 3] == 255                       # dyed texels stay opaque


def test_transparent_background_and_alpha_blended_parts():
    tex = np.ones((4, 4, 4), dtype=np.float32)
    tex[..., 3] = 0.5
    frame = Frame(32, 32)
    draw(frame, _quad(Texture(tex), (1, 1, 1), blended=True))
    out = resolve(frame, 1)
    assert out[0, 0, 3] == 0                           # outside the model: fully transparent
    assert 120 <= out[16, 16, 3] <= 136                # alpha-blended part keeps its texture alpha


def test_render_is_deterministic():
    tex = np.random.default_rng(1).random((8, 8, 4)).astype(np.float32)
    images = []
    for _ in range(2):
        frame = Frame(64, 64)
        item = _quad(Texture(tex), (0.2, 0.4, 0.6))
        item.screen = item.screen * 2
        draw(frame, item)
        images.append(resolve(frame, 2).tobytes())
    assert images[0] == images[1]


class FakeSession:
    """Just enough of ClientSession for export_items."""

    build = "kr-1256"

    def __init__(self, root):
        self.client_root = root
        self.index = None
        self.renders = 0

    def item(self, item_id):
        return SimpleNamespace(item_id=item_id, name=f"item{item_id}", is_3d=True, icon=None,
                               status=SimpleNamespace(value="Renderable"), warnings=[])

    def render_preview(self, request):
        self.renders += 1
        rgba = np.zeros((request.height, request.width, 4), dtype=np.uint8)
        rgba[10:20, 10:20] = 255
        return SimpleNamespace(rgba=rgba, metadata={"camera": {}, "composition": {}, "sources": [], "warnings": []})

    def _prov(self, path):
        return {"path": path}


def test_batch_export_cancellation_and_manifest(tmp_path):
    from bf_item_viewer.export.batch import ExportOptions, export_items
    session = FakeSession(tmp_path / "client")
    cancel = threading.Event()

    def progress(done, total, label):
        if done == 3:
            cancel.set()

    manifest = export_items(session, list(range(1, 50)), tmp_path / "out", ExportOptions(width=64, height=64),
                            progress, cancel)
    data = json.loads(manifest.read_text(encoding="utf-8"))
    assert data["cancelled"] is True and data["requested_jobs"] == 49
    assert data["completed_jobs"] == 3 and session.renders == 3
    assert all(e["status"] == "written" for e in data["entries"])
    written = sorted(p.name for p in (tmp_path / "out").glob("*.png"))
    assert len(written) == 3 and written[0].startswith("kr-1256_1_item1_transparent_64x64")


def test_batch_export_never_overwrites_silently(tmp_path):
    from bf_item_viewer.export.batch import ExportOptions, export_items
    session = FakeSession(tmp_path / "client")
    export_items(session, [7], tmp_path / "out", ExportOptions(width=32, height=32))
    m = export_items(session, [7], tmp_path / "out", ExportOptions(width=32, height=32))
    entry = json.loads(m.read_text(encoding="utf-8"))["entries"][0]
    assert entry["status"] == "skipped" and "exists" in entry["error"]
    m = export_items(session, [7], tmp_path / "out", ExportOptions(width=32, height=32, overwrite="rename"))
    assert json.loads(m.read_text(encoding="utf-8"))["entries"][0]["output"].endswith("~2.png")


def test_export_refuses_client_folder(tmp_path):
    from bf_item_viewer.export.batch import ExportOptions, export_items
    session = FakeSession(tmp_path)
    with pytest.raises(ValueError):
        export_items(session, [1], tmp_path / "exports", ExportOptions())


def test_outline_rule_keeps_weapons_and_skips_eyes():
    from bf_item_viewer.rendering.raster import has_outline, is_alpha_blended
    assert is_alpha_blended("Gun") and has_outline("Gun")        # blended texture, still outlined
    assert not has_outline("kEyeL") and not has_outline("Eyebrow")
    assert has_outline("Head")
