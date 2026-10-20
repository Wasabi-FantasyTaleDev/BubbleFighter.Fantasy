# 开发工具

[English](README.md)

公开工具用于构建和部署打包、CI 检查、包表覆盖检查及测试报告。先阅读[贡献验证](../CONTRIBUTING.zh.md)和[部署指南](../docs/deployment.zh.md)。

- `ci/serial-gates.py` 执行 Release 构建、SDK 检查、逐项目串行测试以及包表和配置检查。完整模式需要指南列出的本地数据；`--public` 只排除需要本地数据的用例。
- `legacy-audit/audit.py` 检查已声明功能的覆盖范围。它读取源码和隔离配置，不能证明实际玩法通过验收。
- `TrxSummary/` 在输入大小限制内汇总测试结果记录。
- `ReloadLeakProbe/` 测量合成程序集上下文样本，不测量 Host 重载。
- `publish-windows-x64.ps1`、`publish-linux.ps1` 和 `prepare-linux-deployment.py` 准备依赖运行时的发布产物。使用前检查参数，凭证和客户端数据不应进入发布归档。

本地 Host 检查必须提供 `--patch tools/local-checks/isolated-memory.patch.yaml`，不能连接部署使用的持久化服务。
