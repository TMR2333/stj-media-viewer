# macOS 版可行性评估与实施计划

> 结论先说：**当前构建环境无法产出可用的 `.dmg`**。原因不是打包工具，而是架构：
> 现有 10 万行 UI 全部建立在 WinUI 3 / Windows App SDK 之上（Windows 独占），且
> macOS 的 `.app`/`.dmg` 必须在 macOS（或 macOS runner）上编译、签名、打包。
> 本文是把它做出来的最短路径，以及每一步的代价。

## 1. 现状盘点（为什么不能直接编译）

| 层 | 规模 | 是否可移植 | 说明 |
| --- | --- | --- | --- |
| `StjMediaViewer.Core` | 79 文件 / 24.6k 行 | **≈90% 可移植** | 只有 7 个文件碰 Windows API（解码/编码、RAW 预览、TIFF/ISOBMFF 解析、设备枚举） |
| `StjMediaViewer.App` | 246 文件 / 101k 行 | **不可移植** | WinUI 3 XAML + Windows App SDK API（Mica、NavigationView、Shell 缩略图、WebView2、WinRT 文件选择器） |
| 工具链 | — | — | `MakeAppx`/`signtool` 只出 MSIX；macOS 产物需要 `xcodebuild` + `codesign` + `hdiutil`，**Windows 上无法生成 Mach-O 可执行文件** |

`.dmg` 本身只是"磁盘镜像压缩包"，Windows 上理论上能造出同名文件，但里面装的必须是
macOS 能执行的 `.app`（arm64/x86_64 Mach-O、ad-hoc 或 Developer ID 签名）。没有 macOS
工具链，产出的 dmg 装了也打不开 —— 所以不做假包。

## 2. 两条路线（二选一）

### 路线 A：Avalonia 跨平台 UI（推荐）

C#/.NET 同一套语言、同一个 Core，Windows 与 macOS 共用一份业务代码。

- **复用**：Core 全部业务（索引库 SQLite、九维分类、XMP、LUT 引擎、导入/导出/核验、
  AI 客户端、云同步、批量填写……）。
- **重写**：UI 层（101k 行 WinUI XAML → Avalonia XAML，约 60–70% 可机械迁移，
  其余为 Fluent 风格与 Windows 专有控件替换）。
- **替换 7 个 Windows 后端文件**：

  | 现有（Windows） | macOS 等价 |
  | --- | --- |
  | WIC `BitmapDecoder`（MediaDecoder） | `ImageSharp` / `SkiaSharp`（跨平台，托管解码） |
  | `ImageEncoder`（WIC JPEG 编码） | `ImageSharp` 编码 |
  | `RawPreviewExtractor` / `TiffParser`（WIC/TIFF IFD） | 纯托管 TIFF 解析（代码已手写，仅少量 WinRT 调用可去） |
  | `IsoBmffParser`（WinRT 容器解析） | 保留手写 box 解析，去掉 WinRT 依赖 |
  | `StorageDeviceService`（WMI/Shell） | `diskutil list -plist` 解析 |
  | `ProtectedData`（DPAPI 加密 API Key） | macOS Keychain（`Security.framework`，托管侧可用 `Security` P/Invoke） |
  | 视频抽帧/转码（ffmpeg.exe） | 同一个 ffmpeg，换 mac 版可执行文件（bundle 内） |

- **工期估计**：3 个阶段、约 **2–3 周**（人工），关键路径是 UI 迁移（占 70%）。
  - 阶段 1（3–4 天）：Core 多目标化（`net10.0` + `#if WINDOWS`），平台抽象接口
    （`IImageBackend`、`ISecretStore`、`IDeviceEnumerator`），Windows 端回归自检 244 项全绿。
  - 阶段 2（1–2 周）：Avalonia 头，先做"素材库网格 + 筛选 + 检视面板 + 导入"，再补
    AI/LUT/批量填写/云同步。
  - 阶段 3（2–3 天）：打包 `.app` + `.dmg`，签名/公证，写 CI。
- **产出**：`STJMediaViewer-1.0.0-macos-arm64.dmg`（Apple Silicon；Intel 可出 universal）。

### 路线 B：SwiftUI 原生 macOS 版

- **优点**：原生观感、启动快、内存低，App Store 分发顺畅。
- **代价**：UI 100% 重写（Swift），Core 需要用 Swift 重写关键逻辑或在中间做 JSON-RPC
  桥接一个 .NET 子进程。工期 **6–10 周**，且两套代码从此并行维护。
- 适合"确定要长期做 macOS 商业版"的场景。

### 路线 C：只读"素材库浏览器"（1–2 天，最小可用）

只做一个 macOS 端 **只读查看器**：读同一份 `catalog.db` + 缩略图缓存，提供网格、筛选、
大图、评分/关键词查看（不写库、不导入、不 AI）。

- 复用：SQLite 结构 + 缩略图命名规则（`{thumb_key}_d2_{size}.jpg`）。
- 成本：SwiftUI 约 800–1200 行 + `hdiutil` 打 dmg。
- 用途：**在 Mac 上就能看 Windows 端整理好的库**，也是验证 dmg 分发链路的探路版本。

## 3. 产出 dmg 的硬性条件（三条，缺一不可）

1. **macOS 构建环境**：Mac（Xcode 15+）或 GitHub Actions `macos-14` runner；
2. **签名身份**：无 Apple Developer ID 时只能 `codesign --sign -`（ad-hoc），
   用户首次打开需「右键 → 打开」或 `xattr -dr com.apple.quarantine`；
   要双击即开必须 Developer ID + **notarization**（年费 $99）；
3. **dmg 打包脚本**：
   ```bash
   hdiutil create -volname "STJ Media Viewer" -srcfolder dist/STJMediaViewer.app \
                  -ov -format UDZO dist/STJMediaViewer-1.0.0-macos-arm64.dmg
   ```
   （或 `create-dmg` 带背景图/图标布局的精致版）

## 4. 当前进展（已选路线 B：SwiftUI 原生）

- **P1 只读浏览器已落地**：`macos/`（SwiftUI + SwiftPM），读同一份 `catalog.db` + 缩略图缓存，
  原生三栏界面、筛选/搜索/排序、拖出、Quick Look 兜底。
- **dmg 构建链路已备好**：`macos/scripts/build-dmg.sh`（swift build → .app → 签名 → dmg → 可选公证）
  与 `.github/workflows/macos-dmg.yml`（`macos-14` runner，push 后直接下载 dmg 构件）。
- 本仓库的 Windows 构建环境**无法检验 Swift 代码**（没有 macOS SDK / SwiftUI），
  编译验证以 CI 或 Mac 上的 `swift build` 为准；P1 之后按 `macos/README.md` 的路线图推进 P2–P5。

## 5. 建议的下一步

告诉我选哪条路线，我可以立刻开始：

- **A（推荐）**：先做阶段 1 —— 把 Core 拆成 `Core`（portable）+ `Core.Windows`，
  Windows 端自检保持 244 项全绿，然后起 Avalonia 头，UI 迁移按页面批次交付；
- **C（最快拿到 dmg）**：1–2 天出一个只读浏览器 + CI，你就能在 Mac 上双击安装；
- **B**：先出架构文档与数据接口契约，再分批重写。

无论哪条路线，**dmg 的最终编译必须在 macOS 上完成**（本机 Mac 或 GitHub Actions），
我可以把 CI workflow 和打包脚本一起写好，你 push 一次就出包。