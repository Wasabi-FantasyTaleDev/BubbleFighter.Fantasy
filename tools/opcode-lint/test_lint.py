import importlib.util
from pathlib import Path


spec = importlib.util.spec_from_file_location("lint", Path(__file__).with_name("lint.py"))
lint = importlib.util.module_from_spec(spec)
assert spec.loader
spec.loader.exec_module(lint)


def check(rows, expected):
    import tempfile
    import yaml

    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "opcodes.yaml"
        path.write_text(yaml.safe_dump({"profile": "x-1", "opcodes": rows}), encoding="utf-8")
        codes = {item["code"] for item in lint.lint_file(path)}
    assert expected <= codes, codes


def valid(value=1, name="gcpExample", source="label@x-1", evidence="verified"):
    return {"value": value, "direction": "c2s", "connection": "game", "name": name,
            "name_source": source, "evidence": evidence, "source": "x-1 sub_ABCDEF"}


def test_duplicate_value_and_name():
    check([valid(), valid(1, "gcpExample")], {"duplicate_value", "duplicate_name"})


def test_missing_and_invalid_metadata():
    row = valid()
    row.pop("evidence")
    row["name_source"] = "bad"
    check([row], {"missing_evidence", "invalid_name_source"})
    check([valid(evidence="bad")], {"invalid_evidence"})


def test_name_rules():
    check([valid(name="coined", source="coined")], {"coined_name"})
    check([valid(name="pretty", source="none")], {"unknown_name"})
    check([valid(name="Unknown7", source="coined")], {"unknown_name_source"})


def test_unqualified_sub_address():
    row = valid()
    row["source"] = "dispatcher sub_ABCDEF"
    check([row], {"unqualified_sub"})
