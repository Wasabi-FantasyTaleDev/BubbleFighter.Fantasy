# TRX record summary

[中文](README.zh.md)

Build `tools/TrxSummary/TrxSummary.csproj` in Release and pass its DLL a text file containing one TRX path per line. Run `tools/TrxSummary/tests/run-tests.ps1 -ToolDll <absolute-dll-path>` to validate the tool.

The CLI reports result records, including repeated inputs, rather than unique tests or a release verdict. Inputs are bounded by file count, list bytes, per-file bytes, total bytes and result count; the source documents the corresponding `--max-*` options. Invalid or excessive inputs fail. Complete gate validation is implemented separately in `tools/ci/serial-gates.py`.
