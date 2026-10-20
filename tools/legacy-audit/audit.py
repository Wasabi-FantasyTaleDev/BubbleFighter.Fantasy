#!/usr/bin/env python3
"""Read-only audit: does each legacy build have what each legacy feature needs?

Features are the bool switches of MessagesOptions and RoomsOptions. Each is mapped to the
C# files that implement it by reading the plugins' ActivateAsync `if (options.X)` blocks; the opcode
names and packet structure names those files use are checked against each build's opcodes.yaml,
opcode-catalog.yaml and `bff packets json`. Nothing in the repository is modified.
See tools/legacy-audit/README.md.
"""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
# CN2013 is a shared migration target after its basic plaza/browser path was
# accepted. Keeping it in the same read-only audit prevents later capability
# changes from silently covering only the older seven profiles.
BUILDS = ["kr-812", "cn-324", "cn-279", "cn-717", "id-400", "id-1809", "cn-706", "cn-406", "cn-404", "cn-408"]
HOST_DIR = ROOT / "src/BubbleFighter.Fantasy.Host/bin/Release/net10.0"
# key -> (plugin folder, plugin file, options class, plugin row id)
PLUGINS = {
    "messages": ("src/Plugins/BubbleFighter.Fantasy.Dialect.Messages", "MessagesPlugin.cs",
                 "MessagesOptions", "dialect.messages"),
    "room": ("src/Plugins/BubbleFighter.Fantasy.Dialect.Rooms", "RoomsPlugin.cs",
             "RoomsOptions", "dialect.rooms"),
}
# Files the plugin reaches without a `new X(` in the switch's block (static layout classes, wire helpers).
EXTRA_FILES = {
    "enable_purchases": ["ShopLayout_Legacy.cs", "EquipmentRequestWire.cs", "InventoryWire.cs",
                         "InventoryTime.cs", "PresetWire.cs"],
    "typed_play_rooms": ["PlazaRoomWire.cs", "UpdatePartyCodec.cs"],
}
# Switches read outside an `if (options.X)` block: names and structures taken from the code that reads
# them (RoomBinding in RoomsPlugin.cs, PlayRoomCodec.cs, GotoLobbyHandler wiring).
MANUAL = {
    "tutorial_move_in": dict(req=[], opt=[("s2c", "gspRepMoveInto"), ("s2c", "gspNotifyAutoTutorial_")],
                             structs=["TutorialMoveInto"]),
    "echo_direct_item_effects": dict(req=[("c2s", "gppUseItemDirect")], opt=[], structs=[]),
    "typed_plaza_enter": dict(req=[], opt=[("c2s", "gcpReqGotoLobby"), ("s2c", "gspGotoLobby")], structs=[]),
}
# Requirements the code enforces outside names and structures (dotted path into `packets json` values).
VALUE_NEEDS = {"tutorial_use_old_map": ["play.tutorial.old_map_key"]}
# A native empty-body request is a real layout, not a missing build-specific table.
EMPTY_BODY_STRUCTS = {"PveRevivalRequest"}
# Preset senders are mounted only when their S2C names exist; the bag and wear routes
# remain usable on earlier builds that do not mount that optional family.
STRUCT_ROUTES = {
    "WeaponPresetChangeReply": {("s2c", "gspReqInventoryChangeWeaponPresetIdx")},
    "WeaponPresetUpdate": {("s2c", "gspReqWeaponPreSetUpdate")},
}
# Switches that the plugin rejects unless the build states a packets/builds.yaml capability.
CAPABILITY_GATES = {"character_creation": "character_creation", "character_creation_preview": "character_creation",
                    "server_notices": "server_notice", "instant_revival": "instant_revival",
                    "quick_matching": "quick_matching", "supply_items": "map_supplies",
                    "echo_direct_item_effects": "direct_item_effect_echo", "horde_lottery": "horde_lottery",
                    "lucci_drops": "lucci_drops"}
BASE = "(always on)"

RE_C2S = re.compile(r'\bC2S\("(\w+)"\)')
RE_S2C = re.compile(r'\bS2C\("(\w+)"\)')
RE_FIND = re.compile(r'\bFind\(\s*PacketDirection\.(ClientToServer|ServerToClient)\s*,\s*"(\w+)"\)')
RE_STRUCT = [re.compile(p) for p in (
    r'\.Resolve\(\s*[\w.]+\s*,\s*"(\w+)"\)',
    r'(?<![\w.])(?:Write|Read)\("(\w+)"')]
RE_PACKET_CODEC = re.compile(r'\bPacketCodec\.(?:Write|Read|ReadScalars)\s*\(')


def snake(name):
    return re.sub(r'(?<!^)(?=[A-Z])', '_', name).lower()


def strip_comments(text):
    """Blank out comments, keeping offsets."""
    text = re.sub(r'/\*.*?\*/', lambda m: ' ' * len(m.group(0)), text, flags=re.S)
    return re.sub(r'//[^\n]*', lambda m: ' ' * len(m.group(0)), text)


def bool_options(text, cls):
    """Return [(PascalCase name, C# default)] of the bool properties of the options class."""
    body = text[text.index("class " + cls):]
    return [(m.group(1), m.group(2) == "true") for m in
            re.finditer(r'public\s+bool\s+(\w+)\s*\{\s*get;\s*set;\s*\}(?:\s*=\s*(true|false);)?', body)]


def statement_end(text, i):
    """Index just past the statement or block starting at text[i]."""
    depth = 0
    while i < len(text):
        c = text[i]
        if c in "({[":
            depth += 1
        elif c in ")}]":
            depth -= 1
            if depth == 0 and c == "}":
                return i + 1
        elif c == ";" and depth == 0:
            return i + 1
        i += 1
    return len(text)


def option_blocks(text):
    """Spans of `if (<positive options>)` statements: (start, end, [PascalCase options])."""
    spans = []
    for m in re.finditer(r'\bif\s*\(', text):
        j, depth = m.end(), 1
        while depth:
            depth += {"(": 1, ")": -1}.get(text[j], 0)
            j += 1
        cond = text[m.end():j - 1]
        opts = [o for o in re.findall(r'(?<![!\w])options\.(\w+)', cond)
                if not re.search(r'!\s*options\.' + o, cond)]
        if opts:
            while text[j].isspace():
                j += 1
            spans.append((m.start(), statement_end(text, j), opts))
    return spans


def packet_codec_structs(text):
    """Read literal third arguments without mistaking a later field key for a structure."""
    for match in RE_PACKET_CODEC.finditer(text):
        start = match.end()
        argument_start = start
        arguments = []
        depth = 0
        quote = None
        escaped = False
        for index in range(start, len(text)):
            char = text[index]
            if quote is not None:
                if escaped:
                    escaped = False
                elif char == '\\':
                    escaped = True
                elif char == quote:
                    quote = None
                continue
            if char in ('"', "'"):
                quote = char
            elif char in '([{':
                depth += 1
            elif char == ')' and depth == 0:
                arguments.append(text[argument_start:index].strip())
                break
            elif char in ')]}':
                depth -= 1
            elif char == ',' and depth == 0:
                arguments.append(text[argument_start:index].strip())
                argument_start = index + 1
                if len(arguments) == 3:
                    break
        if len(arguments) >= 3:
            literal = re.fullmatch(r'(?:\w+\s*:\s*)?"(\w+)"', arguments[2])
            if literal:
                yield literal.group(1)


def scan_names(text):
    """Return (required opcodes, optional opcodes, structures) found in text."""
    req, opt, structs = set(), set(), set()
    for rx, d in ((RE_C2S, "c2s"), (RE_S2C, "s2c")):
        req.update((d, n) for n in rx.findall(text))
    opt.update(({"ClientToServer": "c2s", "ServerToClient": "s2c"}[d], n) for d, n in RE_FIND.findall(text))
    for rx in RE_STRUCT:
        structs.update(rx.findall(text))
    structs.update(packet_codec_structs(text))
    return req, opt - req, structs


def class_files(folder):
    """Map declared type name -> files declaring it (partial classes span several)."""
    out = {}
    for f in folder.rglob("*.cs"):
        for m in re.finditer(r'\b(?:class|struct|record)\s+(\w+)', strip_comments(f.read_text(encoding="utf8"))):
            out.setdefault(m.group(1), set()).add(f)
    return out


def new_files(block, decl, skip):
    return {f for c in set(re.findall(r'\bnew\s+(\w+)\s*[(<{]', block)) for f in decl.get(c, ()) if f.name != skip}


def map_features():
    """Return {feature: {plugin, option, files, req, opt, structs, mapped, values}}."""
    features = {}
    for folder, plugin_file, cls, row in PLUGINS.values():
        folder = ROOT / folder
        text = strip_comments((folder / plugin_file).read_text(encoding="utf8"))
        decl = class_files(folder)
        mine = []
        for pascal, default in bool_options(text, cls):
            features[snake(pascal)] = dict(plugin=row, option=pascal, files=set(), req=set(), opt=set(),
                                           structs=set(), mapped=False, values=[], default=default)
            mine.append(snake(pascal))
        base = dict(plugin=row, option=None, files={folder / plugin_file}, req=set(), opt=set(), structs=set(),
                    mapped=True, values=[], default=True)
        features[f"{row} {BASE}"] = base
        mine.append(f"{row} {BASE}")
        # Names in the plugin file go to the innermost switch block that encloses them, else to the base.
        taken = [False] * len(text)
        for s, e, opts in sorted(option_blocks(text), key=lambda x: x[1] - x[0]):
            block = "".join(" " if taken[i] else text[i] for i in range(s, e))
            for i in range(s, e):
                taken[i] = True
            req, opt, st = scan_names(block)
            for pascal in opts:
                f = features.get(snake(pascal))
                if f is None or f["plugin"] != row:
                    continue
                f["mapped"] = True
                f["req"] |= req
                f["opt"] |= opt
                f["structs"] |= st
                f["files"] |= new_files(block, decl, plugin_file)
        rest = "".join(" " if taken[i] else text[i] for i in range(len(text)))
        base["req"], base["opt"], base["structs"] = scan_names(rest)
        base["files"] |= new_files(rest, decl, plugin_file)
        for snk, extra in EXTRA_FILES.items():
            if snk in features and features[snk]["plugin"] == row:
                for n in extra:
                    features[snk]["files"].update(folder.rglob(n))
                features[snk]["mapped"] = True
        # Add the names of every mapped helper file.
        for name in mine:
            f = features[name]
            for file in f["files"]:
                if file.name != plugin_file:
                    req, opt, st = scan_names(strip_comments(file.read_text(encoding="utf8")))
                    f["req"] |= req
                    f["opt"] |= opt
                    f["structs"] |= st
            f["opt"] -= f["req"]
    for snk, m in MANUAL.items():
        features[snk]["req"] |= set(m["req"])
        features[snk]["opt"] |= set(m["opt"]) - features[snk]["req"]
        features[snk]["structs"] |= set(m["structs"])
        features[snk]["mapped"] = True
    for snk, needs in VALUE_NEEDS.items():
        features[snk]["values"] = needs
        features[snk]["mapped"] = True
    return features


def dump_config(build, env):
    host = HOST_DIR / "BubbleFighter.Fantasy.Host.dll"
    p = subprocess.run(["dotnet", str(host), "run", "--profile", build, "--patch",
                        "tools/local-checks/isolated-memory.patch.yaml", "--dump-config"], cwd=ROOT,
                       env=env, capture_output=True, text=True, encoding="utf8", errors="replace")
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip()[:300])
    # The dump is not always strict YAML (unquoted text with ": " in some rows), so the two legacy rows
    # are cut out by line and only their top-level `key: scalar` config lines are read.
    out, plugin, in_config = {}, None, False
    wanted = [v[3] for v in PLUGINS.values()]
    for line in p.stdout.splitlines():
        if line.startswith("- id: "):
            plugin, in_config = None, False
        elif line.startswith("  plugin: "):
            plugin = line[len("  plugin: "):].strip()
            if plugin in wanted:
                out[plugin] = {}
        elif line.startswith("  config:"):
            in_config = True
        elif line.startswith("  ") and not line.startswith("   "):
            in_config = False
        elif in_config and plugin in out and re.match(r"    [A-Za-z_]\w*: ", line):
            key, _, value = line.strip().partition(": ")
            out[plugin][key] = {"true": True, "false": False}.get(value.strip(), value.strip())
    if not out:
        raise RuntimeError("no legacy rows in dump-config output")
    return out


def build_data(build, env, bases):
    out = {"method": "dump-config"}
    try:
        cfg = dump_config(build, env)
    except Exception as ex:  # noqa: BLE001 - reported in the output, then fall back
        raise RuntimeError(f"Composed configuration for {build} is unavailable: {ex}") from ex
    out["config"] = cfg
    prof = yaml.safe_load((ROOT / "profiles" / build / "profile.yaml").read_text(encoding="utf8"))
    out["profile_parent"] = prof.get("parent")
    ops = yaml.safe_load((ROOT / "profiles" / build / "opcodes.yaml").read_text(encoding="utf8"))["opcodes"]
    out["opcodes"] = {(o["direction"], o["name"]): o.get("evidence", "?") for o in ops
                      if o.get("connection", "game") in ("game", "both")}
    out["catalog"] = {}
    cat = ROOT / "profiles" / build / "opcode-catalog.yaml"
    if cat.exists():
        c = yaml.safe_load(cat.read_text(encoding="utf8"))
        for o in c.get("opcodes", []):
            out["catalog"].setdefault((o["direction"], o["name"]), f'{o.get("grade", "?")}:{o["value"]}')
        for u in c.get("unmapped_classes", []):
            out["catalog"].setdefault(("*", u["name"]), "unmapped-class")
    p = subprocess.run(["dotnet", str(HOST_DIR / "BubbleFighter.Fantasy.Host.dll"), "packets", "json", build], cwd=ROOT,
                       env=env, capture_output=True, text=True, encoding="utf8", errors="replace")
    if p.returncode != 0:
        raise RuntimeError(f"packets json {build}: {p.stderr.strip()[:200]}")
    # Host startup may log that it loaded unset environment variables before the JSON document.
    start = re.search(r"(?m)^\{", p.stdout)
    if start is None:
        raise RuntimeError(f"packets json {build} returned no JSON document")
    pj = json.loads(p.stdout[start.start():])
    out["structs"] = {k: len(v) for k, v in pj["structs"].items()}
    out["values"] = pj.get("values", {})
    out["base"] = pj.get("base")
    lineage, cur = [build], out["base"]
    while cur:
        lineage.append(cur)
        cur = bases.get(cur)
    out["lineage"] = lineage
    return out


def has_value(values, dotted):
    cur = values
    for part in dotted.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return False
        cur = cur[part]
    return cur not in ("", None, False, "false")


def short(items, n=2):
    return ",".join(items[:n]) + (f"+{len(items) - n}" if len(items) > n else "")


def evaluate(name, f, b):
    """Return (cell, details) for one feature and build."""
    row_cfg = b["config"].get(f["plugin"])
    d = {"switch": None, "missing_names": [], "missing_optional": [], "missing_structs": [],
         "missing_values": [], "inactive_structs": [], "gated": None}
    if f["option"] and row_cfg is not None:
        value = row_cfg.get(name, f["default"])
        if value not in (True, False):
            raise ValueError(f"{name} must be a composed boolean, got {value!r}")
        d["switch"] = value
    d["missing_names"] = [f"{x}:{n}" for x, n in sorted(f["req"]) if (x, n) not in b["opcodes"]]
    d["missing_optional"] = [f"{x}:{n}" for x, n in sorted(f["opt"]) if (x, n) not in b["opcodes"]]
    d["inactive_structs"] = sorted(s for s in f["structs"] if s in STRUCT_ROUTES
                                   and not STRUCT_ROUTES[s] & b["opcodes"].keys())
    d["missing_structs"] = sorted(s for s in f["structs"] - set(d["inactive_structs"])
                                 if s not in b["structs"] or (b["structs"][s] == 0 and s not in EMPTY_BODY_STRUCTS))
    d["missing_values"] = [v for v in f["values"] if not has_value(b["values"], v)]
    if name in CAPABILITY_GATES and not has_value(b["values"], "capabilities." + CAPABILITY_GATES[name]):
        d["gated"] = "capability"
    if row_cfg is None:
        cell = "no-row"
    elif not f["mapped"]:
        cell = "unmapped"
    elif d["missing_names"]:
        cell = "names-missing:" + short([n.split(":", 1)[1] for n in d["missing_names"]])
    elif d["missing_structs"]:
        cell = "struct-missing:" + short(d["missing_structs"])
    elif d["missing_values"]:
        cell = "values-missing:" + short(d["missing_values"])
    elif d["gated"]:
        cell = "gated:capability"
    elif f["option"] and not d["switch"]:
        cell = "off"
    else:
        cell = "static-ready"
    return cell, d


def main():
    ap = argparse.ArgumentParser(description="Audit legacy features per build (read-only).")
    ap.add_argument("--builds", nargs="*", default=BUILDS)
    ap.add_argument("--out", help="write the Markdown report here instead of stdout")
    ap.add_argument("--json", metavar="FILE", help="also write the raw data as JSON")
    args = ap.parse_args()
    env = dict(os.environ)
    # dump-config only needs the variable to exist; the value is never used or printed.
    env.setdefault("BFF_MYSQL_PASSWORD", "audit-placeholder")
    builds_yaml = yaml.safe_load((ROOT / "packets/builds.yaml").read_text(encoding="utf8"))["builds"]
    bases = {k: (v or {}).get("base") for k, v in builds_yaml.items()}
    features = map_features()
    data = {b: build_data(b, env, bases) for b in args.builds}
    results = {fn: {b: evaluate(fn, f, data[b]) for b in args.builds} for fn, f in features.items()}
    md = render(features, results, data, args.builds)
    if args.out:
        Path(args.out).write_text(md, encoding="utf8", newline="\n")
    else:
        sys.stdout.reconfigure(encoding="utf8")
        print(md)
    if args.json:
        raw = {"builds": {b: {"method": data[b]["method"], "profile_parent": data[b]["profile_parent"],
                              "packet_base": data[b]["base"]} for b in args.builds},
               "features": {fn: {"plugin": f["plugin"], "option": f["option"], "mapped": f["mapped"],
                                 "files": sorted(p.name for p in f["files"]),
                                 "required_opcodes": sorted(f"{x}:{n}" for x, n in f["req"]),
                                 "optional_opcodes": sorted(f"{x}:{n}" for x, n in f["opt"]),
                                 "structures": sorted(f["structs"]),
                                 "cells": {b: {"cell": results[fn][b][0], **results[fn][b][1]} for b in args.builds}}
                            for fn, f in features.items()}}
        Path(args.json).write_text(json.dumps(raw, indent=1), encoding="utf8")


def render(features, results, data, builds):
    L = ["# Legacy build audit", "",
         "Generated by `tools/legacy-audit/audit.py`. Cells: `static-ready` (switch on, names and structures present; client validation pending),",
         "`off` (everything present, switch off: candidate to enable), `names-missing:<names>`,",
         "`struct-missing:<structs>`, `values-missing:<packet value>`, `gated:capability`, `no-row`, `unmapped`.", "",
         "Config method: " + "; ".join(f"{b}={data[b]['method']}" for b in builds), "",
         "Packet-table base / profile parent: " + "; ".join(
             f"{b} {data[b]['base']} / {data[b]['profile_parent']}" for b in builds), "",
         "| feature | " + " | ".join(builds) + " |", "|---|" + "---|" * len(builds)]
    for fn in features:
        L.append(f"| `{fn}` | " + " | ".join(results[fn][b][0] for b in builds) + " |")
    L += ["", "## Details", ""]
    for fn, f in features.items():
        L.append(f"### `{fn}`")
        L.append(f"- row: `{f['plugin']}`; switch: " + (f"`{f['option']}`" if f["option"] else "none (base of the row)"))
        L.append("- files: " + (", ".join(sorted(p.name for p in f["files"])) or "none mapped")
                 + ("" if f["mapped"] else " (no `if (options.X)` block uses this switch)"))
        L.append(f"- required opcodes ({len(f['req'])}): " + (", ".join(f"{x}:{n}" for x, n in sorted(f["req"])) or "none"))
        L.append(f"- optional opcodes, `Find` ({len(f['opt'])}): " + (", ".join(f"{x}:{n}" for x, n in sorted(f["opt"])) or "none"))
        L.append("- structures: " + (", ".join(sorted(f["structs"])) or "none"))
        for b in builds:
            cell, d = results[fn][b]
            if cell == "static-ready" and not d["missing_optional"]:
                continue
            parts = []
            if d["switch"] is not None:
                parts.append("switch=" + ("on" if d["switch"] else "off"))
            for key, label in (("missing_names", "missing names"), ("missing_optional", "missing optional"),
                               ("missing_structs", "empty structures"), ("missing_values", "missing values")):
                items = []
                for item in d[key]:
                    if key in ("missing_names", "missing_optional"):
                        x, n = item.split(":", 1)
                        hit = data[b]["catalog"].get((x, n)) or data[b]["catalog"].get(("*", n))
                        items.append(f"{item} (catalog: {hit or 'none'})")
                    else:
                        items.append(item)
                if items:
                    parts.append(f"{label}: " + ", ".join(items))
            if d["gated"]:
                parts.append("code requires capability " + CAPABILITY_GATES[fn])
            L.append(f"  - {b}: " + "; ".join(parts))
        if f["req"]:
            ev = []
            for b in builds:
                got = [data[b]["opcodes"][k] for k in sorted(f["req"]) if k in data[b]["opcodes"]]
                ev.append(f"{b} " + ("/".join(f"{got.count(g)}{g}" for g in sorted(set(got))) or "none"))
            L.append("  - evidence of present required names: " + "; ".join(ev))
        L.append("")
    return "\n".join(L)


if __name__ == "__main__":
    main()
