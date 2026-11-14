"""Native static character motion selection and the distinct character-card orbit."""

from types import SimpleNamespace
from xml.sax.saxutils import escape

import numpy as np
import pytest

from bf_item_viewer.catalog.model import Kind
from bf_item_viewer.rendering import compose
from bf_item_viewer.rendering.camera import (CameraRecord, ViewSettings, build_camera, load_profile,
                                            look_at_lh)
from bf_item_viewer.rendering.pose import evaluate, yaw_pitch_roll
from bf_item_viewer.rendering.scene import Scene
from bf_item_viewer.resources.model import Bone, Motion
from bf_item_viewer.service import ClientSession, PreviewRequest


def workbook(rows):
    fields = ("[Weapon]", "[Category]", "[Motion]", "[FileUpper]", "[FileLower]", "[EyePitchFollowBone]")
    header = "<Row>" + "".join(f"<Cell><Data>{field}</Data></Cell>" for field in fields) + "</Row>"
    data = "".join("<Row>" + "".join(f"<Cell><Data>{escape(value)}</Data></Cell>" for value in row)
                   + "</Row>" for row in rows)
    return ('<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" '
            'xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">'
            '<Worksheet ss:Name="CommonAni"><Table><Row><Cell><Data>notes</Data></Cell></Row>'
            + header + data + '</Table></Worksheet></Workbook>').encode()


class Index:
    """Only the resources needed to exercise motion selection, without private assets."""

    def __init__(self, rows, motion=True, table=True):
        self.files = {"Models/Characters/Dao/common/pis-basic-stand.mtn": b"idle"}
        if table:
            self.files["Models/Characters/AniList.xml"] = workbook(rows)
        if motion:
            self.files["Models/Characters/Dao/common/pis-basic-stop.mtn"] = b"stop"
        self.reads = 0

    def exists(self, path):
        return path in self.files

    def read(self, path):
        self.reads += 1
        return self.files[path]

    def list_prefix(self, prefix, extension):
        return [path for path in self.files if path.startswith(prefix + "/") and path.endswith(extension)]


STOP = ("Common", "basic", "stop", "pis-basic-stop", "pis-basic-stop", "NONE")


@pytest.mark.parametrize("kind", [Kind.CHARACTER, Kind.COSTUME_PART])
def test_static_preview_selects_native_row_and_preserves_authored_head(monkeypatch, kind):
    idle = ("Common", "basic", "stand", "pis-basic-stand", "pis-basic-stand", "Bip01_Neck")
    index = Index([idle, STOP])
    composer = compose.Composer(index, preview_motion=load_profile("kr-812").preview["character_motion"])
    motion = Motion(3, "fixture", 3600, 1, 31,
                    [Bone("Bip01_Spine2", np.eye(4)),
                     Bone("Bip01_Head", yaw_pitch_roll(0.4, 0.2, 0), 0)])
    monkeypatch.setattr(compose, "load_motion", lambda *args: motion)
    item = SimpleNamespace(item_id=1, kind=kind, model=SimpleNamespace(
        meshes=["Models/Characters/Dao/Default/Head.msh"], wearers=["Dao"], borrowed_from=None))
    scene, metadata = composer.compose(item)
    assert scene.motion_path.endswith("pis-basic-stop.mtn") and scene.frame == 0
    assert scene.face_forward is None
    assert np.allclose(evaluate(scene.motion).worlds["bip01_head"], motion.bones[1].matrix)
    assert metadata.motion_rule == "CommonAni basic/stop at frame 0"
    composer._preview_pose("Dao")
    assert index.reads == 1


@pytest.mark.parametrize("table,motion", [(False, True), (True, False)])
def test_missing_preview_resources_keep_explicit_idle_fallback(table, motion):
    composer = compose.Composer(Index([STOP], motion, table),
                                preview_motion=load_profile("kr-812").preview["character_motion"])
    path, rule, fallback = composer._preview_pose("Dao")
    assert path.endswith("pis-basic-stand.mtn") and fallback
    assert "preview motion unavailable" in rule


def test_missing_all_motions_reports_the_bind_pose():
    index = Index([], motion=False, table=False)
    index.files.clear()
    composer = compose.Composer(index, preview_motion=load_profile("kr-812").preview["character_motion"])
    path, rule, fallback = composer._preview_pose("Dao")
    assert path is None and fallback and "bind pose" in rule


@pytest.mark.parametrize("rows,message", [
    ([], "expected one"), ([STOP, STOP], "expected one"),
    ([(*STOP[:4], "different", "NONE")], "same file"),
    ([(*STOP[:5], "Bip01_Neck")], "pitch-follow bone"),
])
def test_unsupported_animation_rows_fail_explicitly(rows, message):
    composer = compose.Composer(Index(rows), preview_motion=load_profile("kr-812").preview["character_motion"])
    with pytest.raises(ValueError, match=message):
        composer._preview_pose("Dao")


@pytest.mark.parametrize("build", ["kr-812", "kr-1256"])
def test_character_orbit_matches_native_orthographic_look_at(build):
    profile = load_profile(build)
    record = CameraRecord(300002, (0.7, -7.5, -13.2), (0.06, 0.1717, 0, 0.983), 32.2)
    points = np.array([[1, 5, -2], [-2, 10, 1], [0, 0, 0]], dtype=float)
    orbit = build_camera(profile, record, 164, 120, character=True)
    x, y, z, w = record.quaternion
    yaw = np.arctan2(2 * (x * z + w * y), w * w + z * z - x * x - y * y)
    centre = profile.preview["centre"]
    target = np.array([centre["x"] - record.position[0] * record.zoom,
                       -centre["y_per_aspect"] * 120 / 164 - record.position[1] * record.zoom, 0])
    rotation = yaw_pitch_roll(-yaw, 0, 0)
    target = target @ rotation[:3, :3]
    eye = target + np.array([0, 0, 70]) @ rotation[:3, :3]
    native = np.concatenate([points * record.zoom, np.ones((len(points), 1))], axis=1)
    native = native @ look_at_lh(eye, target, [0, 1, 0]) @ orbit.projection
    pixels = (native[:, :2] + 1) * [82, -60] + [0, 120]
    assert np.allclose(orbit.to_screen(points)[:, :2], pixels, atol=1e-5)
    generic = build_camera(profile, record, 164, 120)
    assert not np.allclose(generic.to_screen(points)[:, :2], pixels)
    large = build_camera(profile, record, 656, 480, character=True)
    assert np.allclose(orbit.to_screen(points)[:, :2] * 4, large.to_screen(points)[:, :2])


def test_character_user_pitch_does_not_tilt_the_static_card():
    profile = load_profile("kr-812")
    record = CameraRecord(1)
    points = np.array([[0, 5, -1], [2, 0, 1]], dtype=float)
    pitched = ViewSettings((np.sin(0.2), 0, 0, np.cos(0.2)))
    default = build_camera(profile, record, 164, 120, character=True)
    assert np.allclose(default.world, build_camera(profile, record, 164, 120, pitched, character=True).world)
    yawed = ViewSettings((0, np.sin(0.2), 0, np.cos(0.2)))
    assert not np.allclose(default.to_screen(points),
                           build_camera(profile, record, 164, 120, yawed, character=True).to_screen(points))


@pytest.mark.parametrize("kind", [Kind.CHARACTER, Kind.COSTUME_PART, Kind.WEAPON])
def test_render_dependencies_include_character_animation_workbook(kind):
    session = ClientSession.__new__(ClientSession)
    session.profile = load_profile("kr-812")
    session.index = Index([STOP])
    scene = Scene([], "Models/Characters/Dao/common/pis-basic-stop.mtn", None)
    session._prepare = lambda request: (SimpleNamespace(kind=kind), None, None, None, None,
                                        None, None, scene, None)
    files = session.input_files(PreviewRequest(1, 164, 120))
    assert ("Models/Characters/AniList.xml" in files) == (kind != Kind.WEAPON)
