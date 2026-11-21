# STJ Media Viewer · macOS 原生版实施计划

> 目标：在 **Swift 6 + SwiftUI + 少量 AppKit** 上实现与 Windows 版共用 `catalog.db` 的原生 Mac 应用，
> 遵循 Apple HIG，产物为可双击安装的 `.app` / `.dmg`。
> 本文件随每个里程碑更新；仓库根目录即 Swift Package 根。

## 1. 约束与基线

| 项 | 决定 |
| --- | --- |
| 语言 / UI | Swift 6（语言模式 5.9）、SwiftUI，仅在系统能力缺口处用 AppKit（NSOpenPanel/NSWorkspace/NSEvent/NSFileCoordinator/QLPreviewPanel） |
| 最低系统 | macOS 14 Sonoma（可用 `.inspector`、`NavigationSplitView`、Settings 场景） |
| 工程 | 保留 `Package.swift`，SwiftPM 多 target；不引入 Xcode 工程、Electron/Tauri/Flutter/Catalyst |
| 数据 | 直接读写 Windows 版 `catalog.db`（SQLite，libsqlite3），schema 以 `src/StjMediaViewer.Core/Catalog/CatalogSchema.cs` 为准 |
| 依赖 | 只用系统框架（SQLite3、AppKit、ImageIO、AVFoundation、QuickLookThumbnailing、Security、CoreServices），不引入商业闭源库 |
| 分发 | `scripts/build-dmg.sh` 产 universal `.app` + `.dmg`；无 Developer ID 时 ad-hoc 签名 |

## 2. 目标结构

```text
macos/
├── Package.swift              # STJCore / STJMediaViewer / stjcheck / STJCoreTests
├── Sources/
│   ├── STJCore/               # 业务层：模型、SQLite、Catalog 读写、缩略图、日志、Keychain、XMP、QuickLook
│   ├── STJMediaViewer/        # 原生 UI：三栏、工具栏、检视面板、设置、菜单、快捷键
│   └── stjcheck/              # 命令行自检（给 CI/发布前跑）
├── Tests/STJCoreTests/        # XCTest 业务断言
├── Resources/Info.plist       # bundle id / 版本 / 最低系统
├── scripts/build-dmg.sh       # universal 构建 + 签名 + dmg
├── scripts/make-sample-library.py
└── shots/                     # 浅色/深色验收截图
```

## 3. 里程碑

### M0 · 骨架与数据层（已完成）

- [x] SwiftPM 多 target（STJCore / App / stjcheck / XCTest）
- [x] `Catalog`：列自动探测（兼容旧库缺列）、筛选 SQL、facet 聚合、读写、合集/合并组、`__unset__` 并集、多主题命中
- [x] `ThumbnailStore`：`Cache/thumbs` 键解析 + QuickLook 兜底 + 失败按文件名写日志
- [x] `AppLog`（`~/Library/Logs/STJMediaViewer/app.log`，含 `[nav]` 面包屑与 `[net]` 一行摘要）、Keychain、`XMPWriter`
- [x] 样例素材库生成器 + `stjcheck` + XCTest

### M1 · P0 骨架（已完成）

- [x] `NavigationSplitView` 三栏（侧栏 / 网格 / `.inspector` 检视面板），列宽可调、侧栏可折叠
- [x] 打开 `catalog.db`（`⌘O`，NSOpenPanel）；记住上次素材库
- [x] 网格视图 + 列表视图，密度小/中/大/列表；选中、⌘A、方向键移动、空格 QuickLook、双击打开
- [x] 完整菜单栏（文件/编辑/查看/素材/帮助）与快捷键；Settings 场景（`⌘,`）
- [x] 深浅色跟随系统 + 手动切换；中文界面
- [x] universal release 构建 + dmg

### M2 · P1 浏览与编辑（已完成）

- [x] 筛选面板：类型/格式/年份/城市/相机/主题/影调/评分/色标/选用/调色/Log/未填写维度
- [x] 快捷筛选 chips、搜索（文件名/关键词/城市，多 token）、6 种排序
- [x] 检视面板：只读元数据 + 评分/色标/选用/城市/主题/影调/Log/调色状态直接编辑
- [x] 批量填写（⇧⌘E）、写入 XMP 侧车、移到废纸篓（回收站 + 清库）
- [x] 合集 CRUD、加入/移出合集；合并组封面 + 展开组内成员
- [x] 拖到 Finder/外部软件（`NSItemProvider(contentsOf:)`）
- [x] 画廊模式（⇧⌘G，大图 + ←/→ 翻页 + Esc 关闭）
- [x] RAW+JPG 折叠（设置/查看菜单开关，SQL 内按同目录同基名匹配 RAW）
- [x] 日志：启动/导航/编辑/删除/网络一行摘要
- [x] 框选多选（橡皮筋，1.25.2）
- [x] 配对导出 JPG+RAW（`⌥⌘P` 菜单/快捷键，1.25.2）
- [ ] 多文件拖拽（需 `NSDraggingSession`，替换 SwiftUI `onDrag`）

### M3 · P2（下一阶段）

- [x] 扫描导入（ImageIO/AVFoundation 抽元数据、按 Windows schema 建库、增量去重、生成缩略图，1.26.0）
- [x] 整理动作（复制/移动/重命名模板 + 冲突改名 + 数据库路径回写）与重复项检测（1.27.0）
- [x] 批量填写对话框补齐 P2 字段；XMP 读取回填（1.28.0：导入时读 `.xmp` 侧车，仅回填空字段）

### M4 · P3（进行中）

- [x] AI 打标签（1.32.0，OpenAI 兼容接口，Keychain 存 Key，字段对齐 Windows AiJsonParser）
- [x] 时间线视图（按拍摄日分组的横向缩略图带，1.30.0）
- [x] 画廊视频播放（AVPlayerView 内联控件，1.30.0）
- [x] LUT 预览与地图视图（1.31.0）

### M5 · P4（进行中）

- [x] WebDAV 上传（1.29.0）：Basic 认证、`Expect: 100-continue`、429/5xx 退避重试、上传后核验大小、XMP 边车同目录、按 平铺/年月/地点/年月+设备 分类、连接诊断六步
- [ ] 远端浏览 / 双向同步 / 断点续传
- [x] 水印导出、引导页、关于页完整化（1.33.0）
- [x] 库备份/恢复（1.34.0）
- [ ] 双向同步/远端浏览

## 4. 验收口径

- `swift build -c release` 与 `bash scripts/build-dmg.sh` 通过，dmg 可挂载、`.app` 可在本机启动
- `stjcheck --data-dir .testdata` 全绿；`swift test` 全绿
- 浅色/深色两张截图进 `shots/`；日志写入 `~/Library/Logs/STJMediaViewer/app.log`
- 8000 条素材首屏加载 < 1.5s（分页 2000 条，先渲染首屏）

## 5. 风险与已知问题

| 风险 | 处理 |
| --- | --- |
| Windows 库为 WAL 模式、只读打开失败 | macOS 端用读写连接 + `busy_timeout`，自动建 `-wal/-shm`；WAL 库不要只拷贝 `.db` 单文件 |
| 无 Developer ID 证书 | ad-hoc 签名，首次打开需右键 → 打开；GitHub Actions/DMG 分发说明已写进 README |
| 多选拖拽 | SwiftUI `onDrag` 一次一个 provider，当前只拖首选文件；后续用 `NSDraggingSession` 补多文件 |
| 无辅助功能权限的环境 | 自动化点击不可用；用 `-dataDirectory` / `-preselectAssetID` 启动参数做截图验收 |
## M6 · Liquid Glass（1.36.0）

- [x] `GlassSupport.swift`：`stjGlass` / `stjGlassCapsule` / `stjGlassButtonStyle` / `GlassCluster`，macOS 26+ 原生玻璃、低版本回退 `.regularMaterial`
- [x] 快捷筛选 chip、状态条、画廊上下栏、地图提示、远端浏览器工具栏、首次引导卡片接入玻璃
- [x] 真机截图（浅色 / 深色 / 画廊 / 地图 / 引导）与 dmg 验收
- [ ] 跟进 macOS 26 后续 API（`glassEffectTransition`、容器 morphing 动画）
