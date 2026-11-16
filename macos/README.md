# STJ Media Viewer · macOS（SwiftUI 原生）

Windows 主版本是 WinUI 3；这里是**原生 SwiftUI 的 macOS 版本**，按阶段推进：

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| **P1 只读浏览器** | 读同一份 `catalog.db` + 缩略图缓存：网格、筛选（类型/年份/城市/设备/主题/评分/选用/Log）、搜索、排序、检视面板、拖出到外部软件、Finder 打开 | ✅ 本仓库 |
| P2 本地导入 | 选文件夹 → 扫盘 → 抽元数据（ImageIO/AVFoundation）→ 自己写 catalog | ⬜ |
| P3 写库能力 | 评分/色标/选用/关键词/批量填写，XMP 回写 | ⬜ |
| P4 AI 与 LUT | 复用 Core 的提示词与 LUT 算法（Swift 重写或 .NET sidecar） | ⬜ |
| P5 云同步 | 挂载目录 / WebDAV | ⬜ |

> P1 的意义：**在 Mac 上直接浏览 Windows 端整理好的库**，同时把签名/公证/dmg 分发链路先跑通。

## 环境要求

- macOS 13 Ventura 或更新
- Xcode 15+ / Command Line Tools（`swift --version` 能输出即可）

## 构建与打包

```bash
cd macos
bash scripts/build-dmg.sh
# 产出：macos/dist/STJMediaViewer-1.0.0-macos-arm64-x86_64.dmg
```

可选：正式签名 + 公证（否则用户首次打开要右键 → 打开）

```bash
export CODESIGN_IDENTITY="Developer ID Application: YOUR NAME (TEAMID)"
export NOTARY_PROFILE="stj-notary"     # xcrun notarytool store-credentials 建好的 profile
bash scripts/build-dmg.sh
```

没有 Mac？用 GitHub Actions（`.github/workflows/macos-dmg.yml`，`macos-14` runner）：
push 后到 Actions 页面下载 `STJMediaViewer-macos-dmg` 构件即可。

## 首次使用

macOS 版目前**不自己建库**，读 Windows 版的数据库：

1. 在 Windows 上找到 `%LOCALAPPDATA%\StjMediaViewer`（里面有 `catalog.db`、`Cache\thumbs\`）；
2. 整个目录拷到 Mac（例如 `~/Library/Application Support/StjMediaViewer`）；
3. 打开 app → 「选择数据目录…」→ 选中该目录。

也可以先用「选择数据目录」随便指一个包含 `catalog.db` 的目录，程序会记住路径（`UserDefaults` 的 `dataDirectory`）。

## 数据与代码结构

```
macos/
├── Package.swift                     SwiftPM（可执行 target，链接 sqlite3 / QuickLookThumbnailing）
├── Resources/Info.plist              bundle 元信息（bundle id、图标、最低系统版本）
├── Resources/AppIcon.png             1024² 图标源（构建时转 .icns）
├── Sources/STJMediaViewer/
│   ├── STJMediaViewerApp.swift       @main + 菜单命令
│   ├── Catalog.swift                 SQLite 只读访问（与 Windows 同一份 schema）
│   ├── LibraryModel.swift            界面状态、筛选、加载
│   ├── ThumbnailStore.swift          缩略图缓存索引 + Quick Look 兜底
│   └── ContentView.swift             筛选栏 / 网格 / 检视面板
└── scripts/build-dmg.sh              swift build → .app → 签名 → .dmg
```

数据库只读打开（`SQLITE_OPEN_READONLY`），不会影响 Windows 端的 WAL；缩略图命名规则与
Windows 端一致：`{thumb_key}_{revision}_{size}.jpg`，直接复用现成缓存（命中即 0ms，和 Windows 上一样）。

## 已知限制（P1）

- 只读：评分/关键词等编辑不落库，XMP 不回写；
- 不导入新素材（P2）；
- 视频缩略图依赖 Quick Look 生成，未命中 Windows 缓存时首屏会稍慢；
- 没有 LUT 调色预览（P4）。

## 路线图与取舍

完整的可移植性盘点和三条路线的对比（Avalonia / SwiftUI 原生 / 只读浏览器）见
[`docs/macos-port.md`](../docs/macos-port.md)。选 SwiftUI 原生意味着：Core 业务逻辑需要在
Swift 侧重写或用 .NET sidecar 桥接，两套代码并行维护 —— 换来的是原生观感与 App Store 分发能力。