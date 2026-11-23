## v1.41.0 — 2026-11-23

### 新增
- **目录标签页（macOS + iOS）**：顶部标签条在多个图片目录之间切换，`⌘O` 打开图片文件夹、`⌘T` 新建标签页、`⇧⌘W` 关闭，标签与当前激活项跨启动恢复。
- **不再绑定「素材库文件」**：左下角选择直接识别目录中的图片/视频，素材目录保持只读——不创建 `catalog.db`、不生成 `Cache/`；索引与编辑结果存进应用私有工作区库 `~/Library/Application Support/STJMediaViewer/workspace.sqlite`，缩略图缓存也移到应用目录。
- 仍保留旧入口 `⌥⌘O` 打开带 `catalog.db` 的素材库（与 Windows 版共用，编辑直接写回）。

### 变更
- `STJCore`：新增 `WorkspacePaths`（应用目录/工作区库/缓存根）、`AssetFilter.libraryIDs`（按库过滤）、`Catalog(databaseURL:)` 与 `Catalog.libraryID(forDirectory:)`；`Importer.importFolder` 支持 `databaseURL` / `cacheDirectory` 覆盖，导入不再写素材目录。
- 缩略图 QuickLook 失败时视频统一用 `AVAssetImageGenerator` 抽帧兜底（macOS + iOS）。
- iOS 端同步切换为工作区模式：`MobileLibraryModel` 不再要求目录里有 `catalog.db`，首次选择目录自动索引；IPA 版本号 0.1.2。

### 修复
- 目录标签过滤值改为 `libraries.id`（UUID）反查，修复切换标签后列表为空。
- 恢复标签页时补设 `dataDirectory`，修复重启后主区域显示"未打开"空态。
- 缩略图缓存改写工作区目录，修复索引时把 `Cache/thumbs` 写进用户素材目录的问题。

### 测试
- 新增 `Tests/STJCoreTests/WorkspaceTests.swift` 3 项：导入不在素材目录落文件、多目录按库隔离、`/tmp` 与 `/private/tmp` 解析为同一库；`swift test` 共 **75 项全过**，`stjcheck` 20 项全过。


## v1.40.1 — 2026-11-23

### 移动端修复（iPadOS / iOS 预览）
- 竖屏 iPad 三栏宽度收敛（侧栏 240–300、检视 260–360），检视面板不再被裁切；元数据行改为单行截断 + 值列自适应。
- 视频缩略图在 QuickLook 不可用时自动用 `AVAssetImageGenerator` 抽帧兜底，视频卡显示真实画面。
- 新增 `-preselectAssetID` 启动参数（模拟器无辅助功能权限时用于验收截图）。

### 说明
- macOS 端功能与 1.40.0 一致；本版主要同步移动端修复与截图文档。


## v1.40.0 — 2026-11-23

### 新增

- **iPadOS / iOS 预览（P0）**：`STJCore` 去掉 AppKit 依赖，可在 iOS 17+ 编译；
  新增 `ios/` 模块（侧栏、网格、检视面板、全屏画廊、筛选、设置）与 `ios/build-sim-app.sh`
  （无需 Xcode 工程即可打包模拟器 `.app`）。范围与路线见 `docs/mobile-scope.md`。

### 变更

- 核心层图像处理统一为 CoreGraphics/ImageIO（`ImageCodecs`）：缩略图、LUT 预览、水印、AI 打标
  不再依赖 `NSImage`；`QuickLookController`（QLPreviewPanel）移到 macOS App 层。
- XMP 侧车写入改为 `Data.write(options: .atomic)`（跨平台）；日志目录在 iOS 下落到 App 容器 `Library/Logs`。

### 验证

- `swift test`：72 项通过；`stjcheck --data-dir .testdata`：20 项通过；macOS 界面与 1.39.0 一致。
- iOS：`xcrun swiftc -sdk iphoneos -target arm64-apple-ios17.0 -typecheck` 核心层与 `STJMobile` 均 0 error。

## v1.39.0 — 2026-11-22

RAW+JPG 合并显示（默认开启）与图片 / 视频分库显示。

### 新增
- **RAW+JPG 合并显示**：同目录、同基名的 `RAW` 与 `JPG` 在网格/列表里只占一格，优先显示 RAW，并在角标上写明「RAW+JPG」；设置 → 媒体库或「查看」菜单可关闭（关闭后两种格式各自成条）。
  - 配对判定在 SQL 里用「同目录 + 同基名 + 存在 JPG 伴生」完成，`Asset.hasJpegSibling` 暴露给卡片角标；对没有 RAW 的库无额外影响。
- **图片 / 视频分库显示**：设置 → 媒体库新增开关「图片与视频分库显示」（默认开启）。开启后侧栏「素材库」拆成「全部素材 / 图片素材 / 视频素材」三个入口并各带计数，原来的精选 / 废片 / 未归类归入「智能分类」小节；合并组、合集、工具区保持不变。
- 侧栏计数来自分面（facets），不必额外查询。

### 修复
- 折叠 RAW+JPG 时导出交换文件只包含封面，组内隐藏成员缺数据的遗留问题（1.38.0 已修，此版本不再回归）。

### 验证
- `swift test`：73 项 0 失败（新增 2 项：配对标记检测、合并显示后仅剩 RAW）；`stjcheck --data-dir .testdata`：20 项 0 失败。

## v1.38.0 — 2026-11-22

跨平台「设置与图片数据」导入导出。

### 新增
- **设置与图片数据迁移**（文件菜单 `⇧⌘X` / `⇧⌘I`，或 设置 → 通用）：把界面设置与素材编辑成果打包成 `stj-media-viewer/exchange` v1 JSON，可在 Windows / macOS 之间互通。
  - 导出：外观、密度、排序、面板开关、RAW+JPG 折叠；每条素材的评分、色标、选用三态、国家/省份/城市、主题、影调、关键词、标题、调色状态；合集与成员。
  - 导入：先预览（来源设备、匹配数、未匹配数、是否含设置、不含密钥），再选择「只填空字段」或「覆盖已有值」。
  - 匹配键 = 文件名（小写）+ 字节数，`relativePath` 仅消歧；不创建、不移动、不删除任何素材文件。
  - 导出文件**不含**任何密钥、口令、缓存路径与水印样式（两端水印模型不同）。
- 命令行等价入口：`stjcheck --export-exchange out.json`、`stjcheck --import-exchange in.json --merge fill|overwrite`（便于 CI 与跨平台联调）。
- 文档：`docs/windows-exchange-prompt.md`（给 Windows 端的实现提示词与验收用例）。

### 内部
- `SettingsExchange`（STJCore）统一编解码、匹配与合并逻辑；`Catalog.collectionMembers()` 读取合集成员。
- 导出改为包含合并组隐藏成员（此前只导出封面），合集成员不再丢失。
- 新增 7 项 XCTest（格式校验、默认值省略、无密钥、匹配规则、两种合并策略、合集还原、Windows 旧密度命名映射）。

### 验证
- `swift test`：71 项 0 失败；`stjcheck --data-dir .testdata`：20 项 0 失败。
- 端到端：`.testdata` 导出 28 条素材 + 3 个合集 → 清空评分/地点/关键词后导入（fill）→ 28 条匹配、19 条合集成员恢复；`overwrite` 模式可覆盖已有值。

## v1.37.1 — 2026-11-22

### 界面
- 侧栏新增「工具」分区（对齐 Windows 的功能分区）：扫描导入、快速批量填写、AI 打标所选、导出带水印、查找重复项、上传所选到云端、浏览云端、备份素材库、从备份恢复，按选中状态/数据目录自动置灰。

### 修复
- 侧栏工具行快捷键提示在窄栏下被裁切，改为悬停提示（`help`）。

# 更新日志

## v1.37.0 — 2026-11-22

对齐 Windows 交互 + 补全能力 + 界面打磨。

### 新增
- **多文件拖拽**：选中多张后拖到 Finder / 其它 App，会带上全部选中文件（此前只拖首选一张）；`DragPayload` 统一解析拖拽集合，`MultiFileDrag` 用 `NSDraggingSession` 实现。
- **检查更新**：帮助菜单 / 设置 → 关于可检查 GitCode 最新发行版（24 小时节流），发现新版本时状态栏出现「新版本 x.y.z」提醒；不自动替换自身（ad-hoc 签名自替换会打断 TCC 授权），引导到发行版页面手动下载。
- **卡片悬停快捷操作**：预览（进画廊）、AI 打标此条。
- **卡片角标与色标条**：合并组「合并·N」、AI 角标、底部 3px 色标条。
- **复制路径 / 复制文件名**：右键菜单，与 Windows 一致。
- **检视面板**：缩略图点击放大进画廊；新增相对路径、修改时间。
- **一键验证脚本** `scripts/verify.sh`；`.cnb.yml` 云端一致性校验流水线。

### 对齐 Windows
- 双击 / `↩` = 内置画廊预览（外部打开改为 `⌘↩`）。
- 快捷键：`0–5` 评分、`6–9` 色标、`P`/`X` 选用与废片、`Home`/`End` 首末（网格与画廊都生效）。
- 右键菜单顺序与 Windows 一致，右键未选中卡片会先把它设为操作对象。
- 状态栏：状态文本 9 秒回落「就绪」，新增「素材 N / 筛选 N / 已选 N」计数。
- 删除确认框显示数量与总体积。

### 修复
- 设置 → 关于页链接指向错误的仓库地址（`…-macos-macos`）。
- 搜索框与 macOS 26 工具栏玻璃底叠加造成的双层描边（改用顶部条原生 `NSSearchField`，1.35.0 起）。

### 测试
- 64 项 XCTest（新增拖拽载荷 3 项）、stjcheck 20 项全部通过。

## v1.36.0 — 2026-11-21

**首个独立版本（本仓库自 `stj-media-viewer` 的 macOS 分支迁出）**

### 新增

- **Liquid Glass 界面**（macOS 26 Tahoe）：原生 `glassEffect` / `GlassEffectContainer`
  - 顶部快捷筛选 chip 改为玻璃胶囊，并整体置于可形变的玻璃群组中
  - 底部状态条改为悬浮玻璃胶囊
  - 画廊（`⇧⌘G`）上下工具栏改为深色玻璃浮层，叠在照片/视频上
  - 地图视图的「N 个素材」「无坐标」提示胶囊与远端浏览器工具栏改为玻璃材质
  - 首次引导改为浮动的玻璃卡片（sheet 背景透明）
  - macOS 14–15 自动回退到 `.regularMaterial`，布局与信息层级不变
- 仓库独立：Windows（WinUI 3）与 macOS（SwiftUI）拆成两个仓库；本仓库只含 macOS 端与测试工具（MockWebDav / MockAiServer）

### 承接历史（原仓库 `macos/native-ui` 分支，1.25.0 → 1.35.0）

P0/P1/P2/P3/P4 的完整实现：三栏骨架与原生工具栏、全维度筛选、搜索排序、密度与框选、RAW+JPG 折叠、
检视与批量编辑、合集与合并组、画廊、扫描导入（Windows 兼容建库 + 增量 + 缩略图）、重复项与整理、
XMP 侧车回填、时间线与视频播放、地图与 LUT、AI 打标、WebDAV 上传/诊断与双向同步（云端浏览下载）、
水印导出、首次引导、素材库备份恢复、关于页。

## 构建与验收

```bash
python3 scripts/make-sample-library.py --data-dir .testdata   # 生成样例库
swift test                                                     # 61 项 XCTest
swift run -q stjcheck --data-dir .testdata                     # 20 项业务自检
bash scripts/build-dmg.sh                                      # → dist/*.dmg
```
