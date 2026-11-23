# 全平台下载（统一入口）

只维护**一个**聚合发行版：GitCode Release 的 `latest` 标签。

- 页面：<https://gitcode.com/TMR2333/stj-media-viewer/releases/tag/latest>
- 版本号说明：聚合页固定用 `latest` 标签，附件内容随各平台最新版本更新；具体版本历史看 [CHANGELOG](../CHANGELOG.md) 和各版本 Release。

## 直接下载

| 平台 | 文件 | 说明 |
| --- | --- | --- |
| Windows 10/11 x64 | [`STJMediaViewer-1.28.3-Setup.exe`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/latest/STJMediaViewer-1.28.3-Setup.exe) | 安装版（自包含，带开始菜单/桌面快捷方式） |
| Windows 10/11 x64 | [`STJMediaViewer-1.28.3-portable.exe`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/latest/STJMediaViewer-1.28.3-portable.exe) | 便携版（免安装，双击即用） |
| Android 8.0+ | [`STJMediaViewer-Android-0.4.0.apk`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/latest/STJMediaViewer-Android-0.4.0.apk) | release 签名；`adb install -r` 或直接点开安装 |
| macOS 12+（Apple Silicon / Intel） | [`STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/latest/STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg) | 通用二进制；macOS 源码在独立仓库 `stj-media-viewer-macos` |
| 校验 | [`downloads.json`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/latest/downloads.json) | 各附件的字节数与 SHA-256 |

## SHA-256

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| STJMediaViewer-1.28.3-Setup.exe | 155,444,193 B | `2ab6d294465f450f850587989bb604789e5d2a0724da00501ab748191ac7686a` |
| STJMediaViewer-1.28.3-portable.exe | 155,444,193 B | `2ab6d294465f450f850587989bb604789e5d2a0724da00501ab748191ac7686a` |
| STJMediaViewer-Android-0.4.0.apk | 14,096,922 B | `91414e7e855d74744c47e1d8d47be8bf8d70eb4e32ea35e721e59cbaea8d6f14` |
| STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg | 5,104,417 B | `027969cbdc5113fe2988ba43df30eb58363b19370168d54f3944474ff793ae90` |

> Windows 的 Setup 与 portable 是同一份自包含产物（安装器负责释放/建快捷方式），所以哈希相同。

## 各平台当前版本

| 平台 | 版本 | 对应版本 Release |
| --- | --- | --- |
| Windows | 1.28.3 | [v1.28.3](https://gitcode.com/TMR2333/stj-media-viewer/releases/tag/v1.28.3) |
| Android | 0.4.0 | 同仓库 `android/` |
| macOS | 1.36.0 | [v1.36.0-macos](https://gitcode.com/TMR2333/stj-media-viewer-macos/releases/tag/v1.36.0-macos) |

## 发版时怎么更新这个聚合页

1. 正常发版本 Release（Windows 安装包 + portabile、Android APK）；
2. 把新附件上传到 `latest` 聚合发行的**同名文件**：GitCode 会保留历史附件，建议直接改文件名带版本号（如上表），旧版本附件删除；
3. 同步更新本文件与 `downloads.json` 的版本号/哈希；
4. macOS 版从 `stj-media-viewer-macos` 的 Release 下载 dmg 后一并上传（保持同一份全平台入口）。