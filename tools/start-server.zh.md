# 从源码启动

[English](start-server.md)

本地隔离实例请按[快速开始](../docs/quickstart.zh.md)操作。每次本地 Host 调用必须提供 `--patch tools/local-checks/isolated-memory.patch.yaml`。

启动脚本读取仓库本地配置，可能使用持久化服务。在用于计划中的部署前，先检查参数和 `.env`。构建及部署更新脚本保留旧产物供回滚；本地检查不能替换运行中的部署或删除备份。参阅[部署指南](../docs/deployment.zh.md)。
