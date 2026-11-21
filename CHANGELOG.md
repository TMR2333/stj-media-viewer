# 更新日志

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
