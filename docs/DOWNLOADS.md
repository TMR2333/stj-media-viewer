# 全平台下载（统一仓库，按平台分发行版）

所有平台的源码和安装包都在 **同一个仓库** `TMR2333/stj-media-viewer`：

- 主分支 `master`：Windows + Android 源码；
- 分支 `macos`：macOS + iPadOS 源码（原独立 macOS 仓库内容迁入）；
- 发行版按平台划分：主发行版只放 Windows/Android，macOS 发行版只放 macOS/iPadOS，互不混放。

## 1. 主发行版（Windows + Android）

👉 <https://gitcode.com/TMR2333/stj-media-viewer/releases/tag/v1.29.0>

| 平台 | 文件 |
| --- | --- |
| Windows 10/11 x64 | [`STJMediaViewer-1.29.0-Setup.exe`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.29.0/STJMediaViewer-1.29.0-Setup.exe) |
| Windows 10/11 x64 | [`STJMediaViewer-1.29.0-portable.exe`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.29.0/STJMediaViewer-1.29.0-portable.exe) |
| Android 8.0+ | [`STJMediaViewer-Android-0.4.0.apk`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.29.0/STJMediaViewer-Android-0.4.0.apk) |

## 2. macOS 发行版（只放 macOS）

👉 <https://gitcode.com/TMR2333/stj-media-viewer/releases/tag/v1.42.1-macos>

| 平台 | 文件 |
| --- | --- |
| macOS 12+（arm64/x86_64） | [`STJMediaViewer-1.42.1-macos-arm64-x86_64.dmg`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.42.1-macos/STJMediaViewer-1.42.1-macos-arm64-x86_64.dmg) |

## 3. iPadOS 发行版（只放 iPadOS）

👉 <https://gitcode.com/TMR2333/stj-media-viewer/releases/tag/v0.3.1-ios>

| 平台 | 文件 |
| --- | --- |
| iPadOS | [`STJMobile-0.3.1-unsigned.ipa`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v0.3.1-ios/STJMobile-0.3.1-unsigned.ipa)（未签名，需自签安装） |

## 版本对照

| 平台 | 版本 | 源码位置 |
| --- | --- | --- |
| Windows | 1.29.0 | `master` 分支 `src/` |
| Android | 0.4.0 | `master` 分支 `android/` |
| macOS | 1.42.1 | `macos` 分支 |
| iPadOS | 0.3.1 | `macos` 分支 `ios/` |

> 历史 Release 里 1.28.3 及更早的附件包含过 macOS dmg / 全平台校验文件，属旧结构，不再作为入口；新发版一律按上面三类分开发行。

## 发版规则

1. Windows/Android 改动：升版本 → 构建 Setup/portable/APK → 打 `vX.Y.Z` → 只把这三类附件传到该 Release；
2. macOS/iPadOS 改动：在 `macos` 分支开发 → 打 `vX.Y.Z-macos` / `vX.Y.Z-ios` → 只把 dmg / ipa 传到对应 Release；
3. 不再把 macOS dmg 放进主发行版，也不把 Windows/Android 放进 macOS/iPadOS 发行版。