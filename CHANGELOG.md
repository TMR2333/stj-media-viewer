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
