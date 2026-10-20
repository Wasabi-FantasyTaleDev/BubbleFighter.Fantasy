# 道具预览工具

[English](README.md)

独立的 Python 工具：从已安装的客户端预览商店和背包道具，并导出透明 PNG。它直接读取客户端的 .pkn/.bfz 档案，
用客户端自带的商店镜头（`Config/ShopItemCamera.csv`）渲染 3D 道具，并在同一卡片布局中显示 2D 图标。
支持 kr-812 与 kr-1256。不引用服务器代码。

```sh
python -m venv .venv
.venv\Scripts\pip install PySide6 numpy Pillow pytest
.venv\Scripts\python -m pytest -q
run_gui.bat <客户端目录>
```

解出的游戏文件、缓存与导出结果（`.cache/`、`output/`）属于专有内容，不得提交。
已知问题：饰品（slot item）挂点经常位置不对。
