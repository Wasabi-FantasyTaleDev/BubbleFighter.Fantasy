# 包表能力覆盖检查

[English](README.md)

完成 Release 构建后，在仓库根目录运行 `python tools/legacy-audit/audit.py`。需要 Python 3 和 PyYAML。`--builds kr-812 cn-324` 选择部分版本；`--out report.md --json raw.json` 保存报告。

工具对比功能开关、源码绑定、opcode 配置行和解析后的包结构。读取 Host 配置时使用隔离内存补丁。结果表示结构覆盖范围，不能证明真实客户端通过验收。生成报告应放在源码树之外。
