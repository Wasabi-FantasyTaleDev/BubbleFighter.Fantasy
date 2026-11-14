# 道具预览工具

[English](README.md)

独立的 Python 工具：从已安装的客户端预览商店和背包道具，默认导出透明 PNG，也可选择 WebP。它直接读取客户端的 .pkn/.bfz 档案，
用客户端自带的商店镜头（`Config/ShopItemCamera.csv`）渲染 3D 道具，并在同一卡片布局中显示 2D 图标。
支持 kr-812 与 kr-1256。不引用服务器代码。

```sh
python -m venv .venv
.venv\Scripts\pip install PySide6 numpy Pillow pytest
.venv\Scripts\python -m pytest -q
run_gui.bat <客户端目录>
```

解出的游戏文件、缓存与导出结果（`.cache/`、`output/`）属于专有内容，不得提交。
饰品预览会用资源中登记的挂点变换替换道具自身动画的根，只加载登记插槽对应的网格。
缺少挂点节点或登记网格时明确报错。客户端同时提供模型和原生道具图标时，预览包优先使用图标。

## 命令行导出

```sh
python -m bf_item_viewer export <客户端目录> --items 201001 --output output/single
python -m bf_item_viewer export <客户端目录> --items 201001 --output output/single --format webp --lossless
python -m bf_item_viewer atlas <客户端目录> --filter kind=weapon,status=Renderable --output output/atlas --format webp --quality 85
python -m bf_item_viewer pack --games-dir <客户端父目录> --builds kr-812 --output output/pilot --jobs 1
python -m bf_item_viewer pack --games-dir <客户端父目录> --output output/preview-pack --jobs 2
```

`export` 和 `export-all` 默认导出 PNG，`atlas` 和 `pack` 默认使用 WebP。两种 WebP 都保留透明通道。
`--quality` 调节有损质量，`--lossless` 保留全部 RGBA 字节。`atlas` 在图片旁写入
`atlas-index.json`，记录每个预览的页码和像素裁切区域。

`pack` 使用 `bf_item_viewer/data/builds.json` 中的相对客户端路径。通过 `--games-dir` 或
`BFIV_GAMES_DIR` 指定共同的父目录。省略 `--builds` 会选择全部已映射版本。输出包含共享资源索引
`pack.json` 和每个版本自己的道具信息索引。预览单格为 328×240，跨版本 RGBA 完全相同的预览共用一个图集位置。
没有生成预览的道具，其图集与单格索引为空。供启动器读取时使用默认的 WebP 格式。

每次生成交付包时使用新的输出目录。独立的 `--work-dir`（默认 `<output>-work`）存放可续跑的渲染缓存和统计，
包括跳过的版本、渲染错误及三种图片格式的体积。渲染器源码、相关资源或导出设置变化时，旧缓存会失效。
使用相同设置重新运行时可以复用缓存目录。

渲染后可以独立统计预览可用性，不修改公开资源包格式：

```sh
python tools/report_availability.py --work-dir output/preview-pack-work --games-dir <客户端父目录> --output output/availability.json
```

报告把无预览道具的编号分为三个互斥集合：`not_rendered` 表示按设计未渲染，`unavailable_native` 表示
所选客户端缺少资源，`render_failed` 表示解码或渲染失败。它读取已有目录和索引缓存，不重新扫描，也不修改
渲染记录。使用独立捕获的客户端视图时，同时传入 `--client-root <版本>=<目录>` 和
`--client-cache <版本>=<索引缓存父目录>`。从其他工作区生成报告时，可用 `--renderer-root` 和
`--renderer-sha256` 固定归档渲染器。缺少的资源会按所选根目录实际检查；暂存归档出错不会自动被当作原生资源缺失。

## 检查与定向修补已有预览包

无需把整包图集同时解码到内存，即可扫描可疑预览：

```sh
python tools/audit_pack.py output/preview-pack --builds kr-812,kr-1256 --output output/review
```

报告和联系表标记空白、覆盖面积小、包围盒偏离中心、碰到边缘和远距离分离部件。这些标记需要人工判断：
原生服装特写经常碰到边缘，方向错误的饰品也可能具有居中的包围盒。客户端图片、联系表和资源证据不得提交。
可通过 `--font` 指定本地标签字体。

对已审查的子集，在包外准备替换图片和明确的 JSON 计划：

```json
{
  "baseline_pack_sha256": "<原始 pack.json 的 SHA-256>",
  "renderer_sha256": "<替换图片使用的渲染器源码指纹>",
  "replacements": [
    {"build": "kr-812", "id": 100032, "kind": "cell", "image": "reviewed-preview.png", "reason": "authored attachment root replacement"}
  ]
}
```

客户端选择原生图标时，以 `kind: "icon"` 提供原始 PNG。图片路径相对于计划所在目录。
渲染单元必须与基准包的单元尺寸相同，且 alpha 非空。

```sh
python tools/repair_pack.py output/preview-pack output/replacements/plan.json --output output/candidate --receipt output/repair-receipt.json
```

输出目录必须是新目录。工具追加去重的无损图集或原始 PNG 图标，仅修改选定的图片引用。它逐字节校验全部保留文件，
并校验未选道具的引用，以及全部文字、数值条、允许角色、套装和角色信息。共享的旧单元不会被覆盖。
原始 generator 描述保留图片，`generator.repair` 分别固定基准包、已审查计划和替换渲染器。
回执列出改动及各版本替换／保留数量。这是混合来源的定向修补，不能当作整包重新渲染或缓存命中。
