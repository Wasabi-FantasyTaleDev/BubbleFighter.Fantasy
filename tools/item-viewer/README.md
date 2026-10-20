# Item viewer

[Chinese](README.zh.md)

A standalone Python tool that previews shop and inventory items from an installed client and exports transparent PNG
images. It reads the client's .pkn/.bfz archives directly, renders 3D items with the client's own shop camera
(`Config/ShopItemCamera.csv`) and shows 2D icons on the same card layout. Supported builds: kr-812 and kr-1256.
It imports nothing from the server.

```sh
python -m venv .venv
.venv\Scripts\pip install PySide6 numpy Pillow pytest
.venv\Scripts\python -m pytest -q
run_gui.bat <client folder>
```

Extracted game files, caches and exports (`.cache/`, `output/`) are proprietary and never committed.
Known issue: slot-item (accessory) attachment is often placed wrongly.
