"""Pin decoration mount, visibility and framing without installed client files."""

from types import SimpleNamespace

import numpy as np
import pytest

from bf_item_viewer.catalog.model import CatalogItem, Fighter, Kind, ModelRef, Status
from bf_item_viewer.rendering.attach import SlotTable
from bf_item_viewer.rendering.camera import CameraRecord, build_camera, fit_accessory_in_view, load_profile
from bf_item_viewer.rendering.compose import Composer, Composition
from bf_item_viewer.rendering.scene import Scene
from bf_item_viewer.rendering.pose import evaluate, translation, yaw_pitch_roll
from bf_item_viewer.resources.model import Bone, Motion
from bf_item_viewer.service import ClientSession


HEADER = b"chrTypeId,slotName,itemId,parentLinkBone,position,rotation,scale,scaleVector\n"
SOCKET = b'1,kEye,0,Bip01_Head,"10,0,0","0,0,0.70710678,0.70710678",1,"1,1,1"\n'
ITEM = b'1,,100221,0,"1,0,0","0,0,0,1",1,"2,1,1"\n'


def test_authored_mount_does_not_receive_the_measured_fallback_roll():
    table = SlotTable(HEADER + SOCKET, HEADER + ITEM)
    bone, transform = table.preview_mount(1, "KEYE", 100221)
    assert bone == "Bip01_Head"
    assert np.allclose(np.array([1, 0, 0, 1]) @ transform, [10, 3, 0, 1])
    assert table.preview_mount(1, "kPelvic", 100221) is None
    assert np.allclose(table.preview_mount(1, "kEye", 100222)[1][3], [10, 0, 0, 1])


def test_socket_selection_keeps_registered_parts_and_excludes_alternate_meshes(monkeypatch):
    paths = [f"fixture/{slot}.msh" for slot in ("kTail", "kPelvic", "kBodyB")]
    item = CatalogItem("kr-812", 100000, "Fixture", "Accessories", "BackAccesory",
                       Kind.SLOT_ITEM, Status.RENDERABLE, model=ModelRef("fixture", paths),
                       raw={"slotSocket": "kTail kBodyB"})
    monkeypatch.setattr("bf_item_viewer.rendering.compose.load_part", lambda index, path, origin: path)
    scene, composition = Composer(None).compose(item)
    assert scene.parts == [paths[0], paths[2]]
    assert composition.item_parts == scene.parts
    item.raw["slotSocket"] = "kMissing"
    with pytest.raises(ValueError, match="missing socket meshes"):
        Composer(None).compose(item)


@pytest.mark.parametrize("option, draws_body", [("PowerPack", False), ("DrawCharacter", True),
                                               ("Event CharacterOnly", True), ("OtherDrawCharacter", False)])
def test_accessory_can_use_bones_without_showing_the_wearer(monkeypatch, option, draws_body):
    item = CatalogItem("kr-812", 100222, "Fixture", "Accessories", "FaceAccesory",
                       Kind.SLOT_ITEM, Status.RENDERABLE,
                       model=ModelRef("fixture", ["fixture/kEye.msh"], borrowed_from=100221),
                       raw={"displayOption": option})
    part = SimpleNamespace(slot="kEye", mesh_path="fixture/kEye.msh", motion=None, rigid=False)
    scene = Scene([part], None, None)
    composition = Composition(item.kind.value)
    session = ClientSession.__new__(ClientSession)
    session.profile = load_profile("kr-812")
    session.catalog = SimpleNamespace(fighters=[Fighter(1, "Fixture", "Fixture", 1, True)])
    session.index = SimpleNamespace(exists=lambda path: path.endswith("pis-basic-stop.mtn"))
    session._composer = SimpleNamespace(_default_parts=lambda wearer: ["fixture/Head.msh"])
    session._slot_table = SlotTable(HEADER + SOCKET, HEADER + ITEM)
    monkeypatch.setattr("bf_item_viewer.service.load_motion", lambda index, path: path)
    monkeypatch.setattr("bf_item_viewer.service.load_part", lambda index, path, origin: path)

    session._wear_slot_item(item, scene, composition, None)

    assert scene.motion.endswith("pis-basic-stop.mtn")
    assert part.attach_bone == "Bip01_Head"
    assert np.allclose(part.attach_matrix[3], [10, 1, 0, 1])
    assert bool(composition.default_parts) == draws_body
    assert ("fixture/Head.msh" in scene.parts) == draws_body
    assert composition.wearer == ("Fixture" if draws_body else None)


def test_accessory_fit_preserves_an_authored_frame_when_it_fits():
    camera = build_camera(load_profile("kr-812"), CameraRecord(1), 160, 120)
    original = camera.projection.copy()
    fit_accessory_in_view(camera, np.array([[25, 30, 0.5], [80, 95, 0.5]]))
    assert np.array_equal(camera.projection, original)


@pytest.mark.parametrize("root", [translation(0, 9.25, 0), yaw_pitch_roll(0, 0, np.pi / 2)])
def test_attachment_replaces_own_motion_root_and_preserves_child_articulation(monkeypatch, root):
    child = yaw_pitch_roll(.3, .2, 0) @ translation(2, 3, 4)
    motion = Motion(3, "Fixture", 3600, 120, 1,
                    [Bone("Slot_Root", root), Bone("Child", child, 0)])
    item = CatalogItem("kr-812", 100221, "Fixture", "Accessories", "FaceAccesory",
                       Kind.SLOT_ITEM, Status.RENDERABLE, model=ModelRef("fixture", ["fixture/kEye.msh"]))
    part = SimpleNamespace(slot="kEye", mesh_path="fixture/kEye.msh", motion=None, rigid=False)
    session = ClientSession.__new__(ClientSession)
    session.profile = load_profile("kr-812")
    session.catalog = SimpleNamespace(fighters=[Fighter(1, "Fixture", "Fixture", 1, True)])
    session.index = SimpleNamespace(exists=lambda path: path.endswith(".mtn"))
    session._composer = SimpleNamespace(_default_parts=lambda wearer: ["fixture/Head.msh"])
    session._slot_table = SlotTable(HEADER + SOCKET, HEADER + ITEM)
    monkeypatch.setattr("bf_item_viewer.service.load_motion", lambda index, path: motion)
    scene, composition = Scene([part], None, None), Composition(item.kind.value)

    session._wear_slot_item(item, scene, composition, None)

    authored = session._slot_table.preview_mount(1, "kEye", item.item_id)[1]
    pose = evaluate(part.motion)
    assert np.allclose(pose.worlds["slot_root"] @ part.attach_matrix, authored)
    assert np.allclose(pose.worlds["child"] @ part.attach_matrix, child @ authored)
    assert part.motion is motion and not part.rigid
    motion.bones[0].name = "OtherRoot"
    with pytest.raises(ValueError, match="no Slot_Root"):
        session._wear_slot_item(item, Scene([part], None, None), composition, None)


def test_accessory_fit_centres_clipped_geometry_without_changing_facing():
    camera = build_camera(load_profile("kr-812"), CameraRecord(1, zoom=40), 160, 120)
    vertices = np.array([[-20, -6, 0], [4, 12, 0]])
    original_world = camera.world.copy()
    fit_accessory_in_view(camera, camera.to_screen(vertices))
    fitted = camera.to_screen(vertices)
    assert np.allclose(fitted[:, :2].mean(axis=0), [80, 60])
    assert np.all(fitted[:, :2] > 0) and np.all(fitted[:, :2] < [160, 120])
    assert np.array_equal(camera.world, original_world)
