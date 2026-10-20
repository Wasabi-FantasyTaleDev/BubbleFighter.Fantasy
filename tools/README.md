# Development tools

[中文](README.zh.md)

Public tools cover build and deployment packaging, CI checks, packet-table coverage and test reports. Start with [contribution validation](../CONTRIBUTING.md) and [deployment](../docs/deployment.md).

- `ci/serial-gates.py` builds Release, checks SDKs, runs each test project serially and checks packet/configuration data. Complete mode requires the documented local fixtures; `--public` excludes only local-data cases.
- `legacy-audit/audit.py` checks declared feature coverage. It reads source and isolated configuration; it cannot prove gameplay acceptance.
- `TrxSummary/` summarizes result records with bounded input sizes.
- `ReloadLeakProbe/` measures synthetic assembly-context fixtures; it does not measure Host reloads.
- `publish-windows-x64.ps1`, `publish-linux.ps1` and `prepare-linux-deployment.py` prepare framework-dependent outputs. Review their parameters before use and keep credentials and client data outside release archives.

For local Host checks, always supply `--patch tools/local-checks/isolated-memory.patch.yaml`. Never run local validation against deployment persistence.
