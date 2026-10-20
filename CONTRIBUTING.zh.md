# 参与贡献

实现前阅读 [AGENTS.md](AGENTS.md)。在独立分支提交范围明确的改动，说明行为变化并提供验证证据。用户文档和更新日志的中英文版本必须一起更新；公开站点还需要同步繁体中文和韩文翻译。

## 构建与检查

安装 .NET 10 SDK。构建必须零警告，不得屏蔽警告。公开类型和成员需要有实际含义的 XML 摘要。通过声明的接口和有归属的 effect 扩展系统，限制资源规模；版本包差异写入数据，部署参数写入经过校验的配置。

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
python -m unittest discover -s tools/ci -p "test_*.py"
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

门禁还需要 PATH 中的 Python、Node.js/npm、Go、Rust/cargo/rustup 及 `wasm32-wasip1` 目标。公开模式运行每个已跟踪测试项目，仅过滤 `Category=LocalClientData` 用例，并明确记录过滤范围，不能当作完整发布门禁。每次使用新的结果目录；非空 TRX 的结果行必须与全部通过、无跳过的计数一致。

## 完整验证

省略 `--public` 执行全部测试。准备以下本地取得的客户端工作表；仓库不分发这些资源：

| 配置或路径 | 所需文件 |
| --- | --- |
| `client-data/cn-324/` | `Config/ShopItem2.xml` |
| `client-data/kr-1256/data/` | `Packs/Config_00000.pkn`（商店与角色工作表）和 `Packs/ConfigSh_00000.pkn`（宠物覆盖表） |
| `artifacts/shop-tables-survey/id400/xml/` | `Config/ShopItem2.xml` |

完整门禁构建 Release、检查四种 SDK、串行运行测试项目、检查包表并导出隔离配置。发布审查还需要隔离启动及受影响的真实客户端流程验证；自动检查不能代替客户端验收。

自托管 CI 通过 `BFF_CI_FIXTURE_ROOT` 提供两个夹具目录与 kr-1256 客户端数据目录。其检出目录不得包含 `.env` 或本地配置补丁。夹具辅助脚本会记录所有权，只删除它自己创建的联接点，从不删除夹具数据。

## 本地 Host 检查

按[快速开始](docs/quickstart.zh.md)设置，包括非空的本地 `BFF_MYSQL_PASSWORD`。每次本地执行 `run` 都必须添加 `--patch tools/local-checks/isolated-memory.patch.yaml`，包括 `--dump-config`。不要假定开发环境连接的是临时数据库。

## 协议与文档修改

发现疑似安全漏洞时，请按[安全政策](SECURITY.zh.md)使用私密渠道，不要在公开 issue 中描述利用细节。

修改布局前阅读[包表](packets/README.zh.md)和 [opcode 规则](profiles/opcode-policy.md)。客户端地址要带版本前缀，保留未知字段及真实证据等级，用测试固定字节。实现历史和私有材料不得进入公开目录；不要加入凭证、个人身份、专有客户端资源或参考资料。

公开指南位于 `docs/`，站点使用明确的页面清单。按[站点说明](docs-site/README.zh.md)同步四种语言，并在两份更新日志中添加带日期的条目。提交源码，不提交生成内容或构建产物。
