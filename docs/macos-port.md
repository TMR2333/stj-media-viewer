# macOS 版：实施结果与说明

> **状态更新（2026-11-21）**：macOS 原生版已在 Swift 6 + SwiftUI 上**实际实现并通过本机验收**，
> 不再是"评估是否可行"。代码即本仓库根目录，产物为
> `STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg`（universal，ad-hoc 签名）。

## Liquid Glass（1.36.0）

macOS 26 起使用系统原生玻璃材质：`Sources/STJMediaViewer/GlassSupport.swift` 提供
`stjGlass` / `stjGlassCapsule` / `stjGlassButtonStyle` / `GlassCluster` 四个封装，
内部按 `#available(macOS 26.0, *)` 分流到 `glassEffect(_:in:)` 与 `GlassEffectContainer`；
macOS 14–15 回退 `.regularMaterial`，布局不变。接入点：快捷筛选 chip、底部状态条、
画廊上下工具栏、地图提示胶囊、远端浏览器工具栏、首次引导卡片。

## 云端浏览与下载（1.35.0，P4 双向同步）

WebDAV 反向同步：列出云端目录（PROPFIND Depth:1），勾选后下载到「数据目录/云同步下载/远端路径」，随后增量导入当前素材库；导入按符号链接解析后的规范路径匹配，避免同一文件重复入库。

## 1. 最终选型

| 层 | 方案 |
| --- | --- |
| UI | SwiftUI（`NavigationSplitView` 三栏、`.toolbar`、`.inspector`、`Settings` 场景、完整菜单栏），少量 AppKit 处理系统能力 |
| 业务 | 新建 `STJCore` Swift 包：SQLite 直读直写 Windows 版 `catalog.db`，不复制业务逻辑到 UI |
| 数据 | libsqlite3 + 手写轻量封装（`Database`/`Statement`），列自动探测，兼容旧库缺列 |
| 图像/视频 | ImageIO + QuickLookThumbnailing（缓存缩略图 + 系统 QuickLook 兜底）、AVFoundation 读取时长/元数据 |
| 安全 | API Key 存钥匙串（`Security.framework`） |
| 分发 | SwiftPM + `scripts/build-dmg.sh`：universal `.app`、图标、签名（可选 Developer ID + 公证）、`.dmg` |

曾评估过的 Avalonia 路线未采用：C#/WinUI 的 UI 层 10 万行不跨平台，移植成本高于用 SwiftUI 重写一套原生 UI；
且 macOS 产物必须在 macOS 工具链上编译签名，Avalonia 只能解决"同一套 UI 语言"，解决不了 HIG 与原生体验问题。

## 2. 已交付能力（P0/P1）

- 三栏原生骨架 + 可折叠侧栏（素材库/合并组/合集）+ 可折叠检视面板
- 打开现有 `catalog.db`、记住上次素材库；网格（三档密度）与列表视图
- 九维检索：类型/格式/年份/城市/相机/主题/影调/评分/色标/选用/调色/Log/未填写
- 搜索（文件名/关键词/城市）、6 种排序、快捷筛选 chips、分面计数
- 选中/多选/范围选/⌘A/方向键/空格 QuickLook/↩ 打开/⌘⌫ 删除/拖拽到 Finder
- 检视面板直接编辑评分、色标、选用三态、城市、省份、国家、主题、影调、Log、调色状态
- 批量填写、写入 XMP 侧车、合集 CRUD、合并组封面与展开、移到废纸篓
- 深浅色自动跟随、中文界面、Settings（通用/媒体库/AI/云同步/关于）
- 时间线视图（P3，1.30.0）：`⌥⌘T` 切换，按拍摄日分组的横向缩略图带，日期倒序、未标注日期排最后
- 画廊视频播放（P3，1.30.0）：`⇧⌘G` 打开画廊后，视频用 `AVPlayerView` 直接播放（内联控件/全屏）
- 地图视图（P3，1.31.0）：`⌥⌘M`，GPS 素材以缩略图图钉呈现，点选/双击进画廊，自动缩放
- LUT 预览（P3，1.31.0）：数据目录 `LUTs/*.cube`，画廊「LUT」菜单即时套用（CIColorCube）
- AI 打标（P3，1.32.0）：`⌥⌘A`，OpenAI 兼容 `chat/completions`，设置 → AI 配置 BaseUrl/模型/Key；
  写回主题（首位）、影调、关键词（并集）、`ai_subject`/`ai_mood`/`quality_score`，设置页与检视面板可见
- 日志 `~/Library/Logs/STJMediaViewer/app.log`（启动、导航面包屑、编辑、网络一行摘要）
- 扫描导入（P2，1.26.0）：`⌘I` 选目录，没有 `catalog.db` 时按 Windows `CatalogSchema` 建库；
  ImageIO/AVFoundation 抽元数据，按 `full_path`+大小+修改时间增量去重，生成 `Cache/thumbs` 缩略图，
  刷新 `libraries` 计数；变化文件只更新技术元数据，保留用户评分/主题等编辑
- XMP 侧车读取回填（P2，1.28.0）：导入时解析同目录 `.xmp`，回填评分/色标/城市/国家/省份/标题/说明/作者/关键词；库内用户编辑优先，只填空字段。
- WebDAV 云同步（P4，1.29.0）：设置 → 云同步 填地址/用户名/口令（口令存钥匙串）、远端根目录与布局
  （平铺 / 年月 / 地点 / 年月+设备）；`⌥⌘U` 上传所选素材，XMP 边车同目录上传，上传后按 `getcontentlength` 核验；
  `Expect: 100-continue`、429/5xx 按 1.2s→3.5s 退避重试、600s 超时、文件之间可取消；「测试连接」跑
  连接/鉴权 → 列目录 → 上传探针 → 核验 → 清理 六步诊断
- 重复项检测与整理动作（P2，1.27.0）：`⌥⌘D` 按大小/尺寸/时长聚类后以「大小 + 首 64KB」哈希确认，
  面板给出分组、可回收空间与建议保留项；「整理所选 → 复制/移动」按模板（`{date}/{year}/{city}/{original}` 等）
  分目录重命名，冲突自动加序号，移动后回写 `full_path/file_name/relative_path`

## 3. 数据兼容语义

- `is_pick`：`1` 精选、`0` 废片、`NULL` 未标记；"废片"筛选必须是 `= 0`，不是"不等于 1"
- `__unset__`：按 `IS NULL OR ''` 处理，且与具体值筛选取**并集**（选"城市未填写"同时选"上海"时都显示）
- `themes_json`：数组，第一个为主主题 `theme`；按任意主题都应命中
- 缩略图键：`路径+大小+修改时间+编辑状态+LUT` 的 SHA-1（40 hex），缓存目录 `Cache/thumbs/{hash}_{rev}_{size}.jpg`
- 写操作：读写连接 + `busy_timeout` + 事务；不主动把库切到 WAL（保持 Windows 默认的 `journal_mode=delete`），
  若素材库本身是 WAL 模式，只读打开需连同 `-wal/-shm` 一起拷贝

## 4. 构建与验收

```bash
cd macos
swift build -c release
bash scripts/build-dmg.sh          # → dist/STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg

python3 scripts/make-sample-library.py --data-dir .testdata
swift build --target stjcheck && .build/debug/stjcheck --data-dir .testdata
swift test
```

验收要点：release 构建通过、dmg 可挂载且含 `Applications` 链接、`.app` 可启动、浅/深色截图在
`shots/`、`stjcheck` 与 XCTest 全绿、日志文件写入正常。

## 5. 已知限制与后续

| 项 | 现状 / 计划 |
| --- | --- |
| 多选拖拽 | 目前只拖首选文件（SwiftUI 单 provider），P2 用 `NSDraggingSession` 补齐 |
| 扫描导入 | 已完成（1.26.0）；命令行等价入口 `stjcheck --import <目录>` |
| 整理动作 / 重复项 | 已完成（1.27.0）：模板复制/移动、冲突改名、移动后回写数据库；重复项按哈希确认 |
| XMP 读取回填 | 已完成（1.28.0）：Lightroom/Bridge/ExifTool 常见字段；仅空字段回填，用户编辑优先 |
| 时间线 / 视频 | 已完成（1.35.0）：时间线视图、画廊视频播放；视频抽帧缩略图仍用 QuickLook |
| AI | 已完成打标（1.35.0）：OpenAI 兼容接口 + Keychain Key；批处理队列与失败重试待做 |
| WebDAV | 已完成单向上传与连接诊断（1.29.0）；远端浏览、双向同步、断点续传待做 |
| 水印、引导页 | P4 里程碑 |
| 签名 | 本机无 Developer ID，发行版为 ad-hoc；首次打开需右键 → 打开。有证书时 `CODESIGN_IDENTITY`/`NOTARY_PROFILE` 自动签名+公证 |
| 辅助功能 | 未授予"辅助功能"权限的机器无法被脚本模拟点击；截图验收用 `-preselectAssetID` 启动参数 |