# TRX 记录汇总

[English](README.md)

以 Release 构建 `tools/TrxSummary/TrxSummary.csproj`，向生成的 DLL 传入文本文件，其中每行是一个 TRX 路径。使用 `tools/TrxSummary/tests/run-tests.ps1 -ToolDll <DLL绝对路径>` 验证工具。

CLI 汇总结果记录，会计入重复输入，不表示唯一测试数或发布结论。输入受文件数、清单字节数、单文件字节数、总字节数和结果数限制；对应 `--max-*` 选项见源码。无效或超限输入会失败。完整门禁验证另由 `tools/ci/serial-gates.py` 实现。
