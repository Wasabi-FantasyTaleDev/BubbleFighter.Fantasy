# Packet capability coverage

[中文](README.zh.md)

Build Release, then run `python tools/legacy-audit/audit.py` from the repository root. Python 3 and PyYAML are required. `--builds kr-812 cn-324` selects a subset; `--out report.md --json raw.json` saves reports.

The audit compares declared feature switches with source bindings, opcode rows and resolved packet structures. Host configuration reads use the isolated memory patch. Results describe structural coverage, not real-client acceptance. Keep generated reports outside the source tree.
