# MySQL 持久化

[English](README.md)

`persistence.mysql` 配置行通过 MySQL 提供仓储接口。通过 profile 和仓库 `.env.example` 中的环境变量配置；数据库名称默认为 `bf_game_db`，也可以是任何已有的数据库。环境变量插值需要非空的 `BFF_MYSQL_PASSWORD`。

连接验证和 `auto_migrate` 决定启动行为，启用前请阅读配置选项。对已有数据库执行迁移前先备份。内嵌迁移脚本是按版本排序的输入，不能通过修改已应用的迁移来改变已部署结构。

本地 Host 检查应使用隔离内存补丁。构建成功或内存测试通过不能证明真实数据库可用。参阅[部署指南](../../../docs/deployment.zh.md)。
