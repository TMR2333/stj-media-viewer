# 移动端（iPadOS / iOS）范围与路线

> 本文记录「iPad/iPhone 版与 macOS 版对齐」的现状、可复用范围、明确不做的功能，以及本地怎么跑起来。
> 对应代码：`ios/Package.swift`（STJMobile 库）、`ios/Sources/STJMobile/`（SwiftUI 视图与移动端库模型）、
> `ios/App/`（App 入口与 Info.plist）、`ios/build-sim-app.sh`（模拟器打包脚本）。

## 1. 结论先说

- **数据层已经平台无关**：`Sources/STJCore`（约 6.5k 行、22 个文件）不再依赖 AppKit，
  用 iPhoneOS 26.5 SDK `-typecheck` 零错误；macOS 端 72 项 XCTest 全绿，行为未变。
- **iPad P0 界面可编译并能在模拟器打包**：侧栏（全部/图片/视频/精选/废片/合集）、网格、
  检视面板、筛选、搜索、排序、全屏画廊、评分/色标/精选/废片编辑、设置（RAW+JPG 合并、图片/视频分库、密度）。
- **尚未做**：iPhone 专用布局、后台扫描、LUT/地图/时间线/云同步的移动入口、App Store 打包。

## 2. 复用 / 分叉

| 能力 | macOS | 移动端 | 说明 |
| --- | --- | --- | --- |
| catalog.db 读写、筛选/搜索/排序 | ✅ | ✅ 同一套 `STJCore` | 与 Windows 版同 schema |
| 缩略图（缓存 + QuickLook） | ✅ | ✅ | `ThumbnailStore` 已改为返回 `CGImage` |
| 评分/色标/精选/废片/批量编辑 | ✅ | ✅ | 移动端用简化的检视面板 |
| 合集 / 合并组 / RAW+JPG 折叠 | ✅ | ✅ | 折叠开关默认开启 |
| 全屏画廊 | ✅ | ✅（含左右切换） | 视频播放待接 |
| 扫描导入 | ✅（`Importer`） | ✅ 选目录后导入 | 用 `.fileImporter` 选文件夹 |
| LUT / 地图 / 时间线 | ✅ | ⏳ | P1 再接，移动端空间有限 |
| AI 打标 | ✅ | ⏳ | 复用 `AITagger`，注意流量与后台限制 |
| WebDAV 云同步 | ✅ | ⏳ | `WebDAVClient` 已平台无关 |
| 多文件拖拽到 Finder | ✅ | ❌ | iOS 用「分享/导出」替代 |
| 菜单栏、多个独立窗口、Finder 菜单 | ✅ | ❌ | 系统差异 |
| 后台常驻扫描 | ✅ | ❌ | iOS 只能 BGProcessingTask，改为「打开时增量扫描」 |
| 自动更新 | ✅（检查 + 引导下载） | ❌ | 走 App Store |

## 3. 移动端特有的约束

1. **文件访问**：iOS 不能常驻任意目录。做法是用户用「文件」App 选一次素材库目录，
   App 保存 security-scoped bookmark，之后每次启动解析书签继续访问（`MobileLibraryModel.restoreLastLibrary()`）。
2. **路径身份**：容器路径每次安装可能变化，因此 `catalog.db` 内建议保存相对路径；
   当前 P0 直接用绝对路径 + 书签，跨设备迁移时请用「设置与图片数据导入导出」重定位。
3. **分发**：需要付费 Apple Developer 账号（TestFlight / App Store）。本仓库只发布源码，
   不提供 `.ipa`/`.app` 下载。
4. **缩略图缓存**：写入素材库下的 `Cache/thumbs`（与 macOS 版同一个键名规则，可共用缓存）。

## 4. 本地运行（无 Xcode 工程）

```bash
# 1) 只验证 iOS 编译（不需要模拟器 runtime）
xcrun swiftc -sdk "$(xcrun --sdk iphoneos --show-sdk-path)" \
  -target arm64-apple-ios17.0 -typecheck \
  -I /tmp/iosbuild ios/Sources/STJMobile/*.swift

# 2) 打包成模拟器 .app（自动编译 STJCore + STJMobile + App 入口）
bash ios/build-sim-app.sh

# 3) 安装到已启动的模拟器并拉起
xcrun simctl boot "iPad Pro 13-inch (M4)"      # 或 xcrun simctl list devices
bash ios/build-sim-app.sh --install

# 4) 截图
xcrun simctl io booted screenshot shots/ipad-main.png
```

首次运行 App 会提示「打开 / 导入素材库…」：
- 想快速看效果，把样例库放进 App 沙箱的 Documents/DemoLibrary（脚本 `--demo-library .testdata` 会代劳）；
- 或用「文件」App 选中真实素材库目录（含 `catalog.db`）。

> 用 Xcode 的方式：新建 iOS App 工程 → 把 `ios/Sources/STJMobile` 与 `ios/App/STJMobileApp.swift`
> 拖进 target，依赖 `ios/Package.swift`（本地包），Bundle Identifier 用
> `com.stjarnaperspective.stjmediaviewer.mobile`，图标与签名按自己的开发者账号设置。

## 5. 路线图

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| M0 | `STJCore` 去 AppKit 化、iOS 编译通过、macOS 回归 | ✅ |
| M1 | iPad P0：库浏览 / 筛选 / 搜索 / 检视编辑 / 画廊 / 导入 | ✅（可编译、可模拟器运行） |
| M2 | 视频播放、LUT 预览、时间线、多选批处理、合集管理 | ⏳ |
| M3 | AI 打标、WebDAV 上传/浏览、设置迁移（复用 `SettingsExchange`） | ⏳ |
| M4 | iPhone 布局（TabView + 单列）、App Store 打包与 TestFlight | ⏳ |

## 6. 已知限制

- 模拟器首次启动要先下载 iOS Runtime（`xcodebuild -downloadPlatform iOS`，约 8.5 GB）。
- 视频缩略图依赖 QuickLook，模拟器上偶发取不到，会回退到文件图标。
- 移动端暂不支持 RAW 解码预览（和 macOS 版一致，走 QuickLook/伴生 JPG）。
- 未做深色/浅色手工切换同步（跟随系统 + 设置项已复用）。


## 安装到真机（未签名 IPA，v0.1.1 起）

```bash
bash ios/build-ipa.sh                     # 产出 dist/STJMobile-<版本>-unsigned.ipa
bash ios/build-ipa.sh --sign "Apple Development: 你的名字 (TEAMID)" \
                      --profile ~/Downloads/STJMobile.mobileprovision   # 有证书时签名
```

- 未签名 IPA 结构：`Payload/STJMediaViewer.app/{STJMobile,Info.plist,AppIcon.png}`，
  可执行文件为 `arm64`、`platform IOS`、`minos 17.0`。
- 安装方式：Sideloadly / AltStore / Xcode 个人免费账号重签；免费证书 7 天过期。
- 应用内不会自动更新；素材库路径通过「打开 / 导入素材库」选择文件夹（安全作用域书签持久化）。

## 1.41.0 起的目录模式（与 macOS 一致）

iPad/iPhone 端也不再要求目录里存在 `catalog.db`：用户用「文件」App 选一个目录后，应用直接用工作区库
（沙箱内 `Library/Application Support/STJMediaViewer/workspace.sqlite`）记录索引，素材目录保持只读；
缩略图缓存写在应用沙箱内。旧素材库（带 `catalog.db`）仍可通过同一入口浏览。


## 系统相册（iOS 0.2.0 起）

- 数据源：PhotoKit（`PHPhotoLibrary` / `PHCachingImageManager`），与素材库完全解耦——相册内容不进入
  `workspace.sqlite`，也不会写回系统照片库（只有用户主动点「收藏」时才改收藏状态）。
- 权限：`NSPhotoLibraryUsageDescription`（读取）+ `NSPhotoLibraryAddUsageDescription`（收藏写回），
  首次进入相册请求 `.readWrite`；拒绝时展示「去设置里允许」引导并可重试。
- 范围：按 `creationDate` 倒序浏览最近 5000 张照片，网格缩略图（PHCachingImageManager + 内存缓存）、
  全屏翻页大图、日期/像素尺寸展示、收藏切换；暂不支持评分/色标/主题/删除等素材库级编辑。
- **导入素材库（0.2.1）**：相册「多选 → 导入 N」用 `PHAssetResourceManager` 把原图写到 `Documents/相册导入-时间戳/`，
  再交给 `Importer.importFolder` 建索引；导入后即可用素材库全部能力（评分/色标/主题/AI/整理）。
- 多选：素材库与相册共用「多选」模式（勾选圈 + 底部批量操作）。
- 后续可做：按相册/收藏/时间范围筛选、Live Photo 与视频、导入时可选目标目录。
