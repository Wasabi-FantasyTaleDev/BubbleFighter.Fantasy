"""Pin missing-asset evidence, decode failures and the distinction between design exclusions and missing previews."""

import pytest

from report_availability import assignments, classify, missing_lookup


def metadata(kind="weapon", has_model=True, status="Renderable"):
    return {"kind": kind, "has_model": has_model, "catalog_status": status}


def row(error=None):
    return {"id": 7, "cell": None, "error": error}


def test_staged_archive_failure_is_native_unavailable_only_when_requested_member_is_missing():
    entry = row("ArchiveError: Pkg/Models/Weapon/Test/Test.bfz: member 'Models/Weapon/Test/Gun.msh' does not decrypt")
    assert classify(entry, metadata(), {"weapon"}, lambda path: False) == ("unavailable_native", "Models/Weapon/Test/Gun.msh")
    assert classify(entry, metadata(), {"weapon"}, lambda path: True) == ("render_failed", None)


def test_missing_requested_asset_is_distinct_from_a_design_exclusion():
    entry = row("FileNotFoundError: Models/Characters/Test/Default/Head.msh is not in this client")
    assert classify(entry, metadata("other", False, "Metadata-only"), {"weapon"}, lambda path: False)[0] == "unavailable_native"
    assert classify(row(), metadata("other", False, "Metadata-only"), {"weapon"}, lambda path: False)[0] == "not_rendered"


@pytest.mark.parametrize("status", ["Missing mesh", "Missing texture", "Model partially resolved"])
def test_explicit_missing_catalog_states_are_native_unavailable(status):
    assert classify(row(), metadata(status=status), {"weapon"}, lambda path: True)[0] == "unavailable_native"


def test_decoder_failure_is_never_reclassified_as_missing_without_a_missing_lookup():
    assert classify(row("ValueError: damaged mesh"), metadata(), {"weapon"}, lambda path: False)[0] == "render_failed"
    assert classify(row(), metadata(status="Failed to decode"), {"weapon"}, lambda path: True)[0] == "render_failed"
    assert missing_lookup("ValueError: arbitrary path-like message") is None


def test_unexplained_null_preview_and_duplicate_override_fail_loudly():
    with pytest.raises(ValueError, match="no supported availability explanation"):
        classify(row(), metadata(), {"weapon"}, lambda path: True)
    with pytest.raises(ValueError, match="unique"):
        assignments(["cn-706=D:/one", "cn-706=D:/two"])
