"""Verify selective repair preserves shared baseline pictures and unrelated native metadata."""

import json
from pathlib import Path
import sys

from PIL import Image
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from repair_pack import file_hash, repair, renderer_source_hash, write_json


def fixture(tmp_path):
    baseline = tmp_path / "baseline"
    (baseline / "builds").mkdir(parents=True)
    (baseline / "atlas").mkdir()
    Image.new("RGBA", (328, 240), (10, 20, 30, 255)).save(baseline / "atlas/shared.png")
    entry = {"atlas": 0, "cell": 0, "name": "Fixture", "description": "Keep", "main": "Accessories",
             "sub": "Face", "spec": [1, 2, 3, 4, 5], "allow": "1 2"}
    for build in ("kr-812", "cn-717"):
        write_json(baseline / f"builds/{build}.json", {"format": 1, "build": build, "items": {"1": entry, "2": entry},
                                                     "sets": {"7": {"name": "Keep set"}}, "characters": {"1": "Keep character"}})
    write_json(baseline / "pack.json", {"format": 1, "generator": {"renderer_sha256": "original"},
                                       "cell": [328, 240], "atlases": [{"file": "atlas/shared.png", "cells": 1,
                                         "columns": 1, "width": 328, "height": 240}],
                                       "builds": {b: f"builds/{b}.json" for b in ("kr-812", "cn-717")}})
    Image.new("RGBA", (328, 240), (40, 60, 80, 255)).save(tmp_path / "replacement.png")
    Image.new("RGBA", (7, 5), (60, 90, 120, 128)).save(tmp_path / "native.png")
    plan = {"baseline_pack_sha256": file_hash(baseline / "pack.json"), "renderer_sha256": renderer_source_hash(),
            "replacements": [{"build": "kr-812", "id": 1, "kind": "cell", "image": "replacement.png", "reason": "root replacement"},
                             {"build": "cn-717", "id": 2, "kind": "icon", "image": "native.png", "reason": "native icon"}]}
    path = tmp_path / "plan.json"
    write_json(path, plan)
    return baseline, path, plan


def test_selective_repair_retains_shared_cells_and_all_nonvisual_metadata(tmp_path):
    baseline, plan, _ = fixture(tmp_path)
    original = {path.relative_to(baseline): path.read_bytes() for path in baseline.rglob("*") if path.is_file()}
    output = tmp_path / "candidate"
    receipt = repair(baseline, plan, output)
    for relative, encoded in original.items():
        assert (baseline / relative).read_bytes() == encoded
    assert (output / "atlas/shared.png").read_bytes() == original[Path("atlas/shared.png")]
    kr = json.loads((output / "builds/kr-812.json").read_text())
    cn = json.loads((output / "builds/cn-717.json").read_text())
    assert kr["items"]["2"] == cn["items"]["1"]
    assert (kr["items"]["1"]["atlas"], kr["items"]["1"]["cell"]) == (1, 0)
    for current in (kr, cn):
        before = json.loads(original[Path(f'builds/{current["build"]}.json')])
        assert current["sets"] == before["sets"] and current["characters"] == before["characters"]
        for key, entry in current["items"].items():
            assert {k: v for k, v in entry.items() if k not in ("atlas", "cell", "icon")} == {
                k: v for k, v in before["items"][key].items() if k not in ("atlas", "cell", "icon")}
    assert (output / cn["items"]["2"]["icon"]).read_bytes() == (tmp_path / "native.png").read_bytes()
    manifest = json.loads((output / "pack.json").read_text())
    assert manifest["generator"]["renderer_sha256"] == "original"
    assert manifest["generator"]["repair"]["renderer_sha256"] == renderer_source_hash()
    with Image.open(output / manifest["atlases"][1]["file"]) as decoded:
        with Image.open(tmp_path / "replacement.png") as expected:
            assert decoded.crop((0, 0, 328, 240)).tobytes() == expected.tobytes()
    assert receipt["per_build"]["kr-812"] == {"replaced": 1, "cells": 1, "icons": 0, "preserved": 1}
    assert receipt["retained_files_verified"] == 1


@pytest.mark.parametrize("bad", ["baseline", "renderer", "duplicate", "traversal", "blank"])
def test_unreviewed_or_invalid_replacement_fails_before_creating_output(tmp_path, bad):
    baseline, path, plan = fixture(tmp_path)
    if bad == "baseline":
        plan["baseline_pack_sha256"] = "wrong"
    elif bad == "renderer":
        plan["renderer_sha256"] = "wrong"
    elif bad == "duplicate":
        plan["replacements"].append(plan["replacements"][0])
    elif bad == "traversal":
        plan["replacements"][0]["image"] = "../outside.png"
    else:
        Image.new("RGBA", (328, 240)).save(tmp_path / "replacement.png")
    write_json(path, plan)
    with pytest.raises(ValueError):
        repair(baseline, path, tmp_path / "candidate")
    assert not (tmp_path / "candidate").exists()
