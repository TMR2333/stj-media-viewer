# 全平台下载（统一入口）

**一个发行版装下所有平台**：最新版本 Release 即全平台下载页。

- 统一入口（当前 = v1.28.3）：<https://gitcode.com/TMR2333/stj-media-viewer/releases/tag/v1.28.3>
- 历史版本：各 `vX.Y.Z` Release（回滚用）
- 旧聚合页 `latest` 已改为跳转提示，仅保留旧附件。

## 直接下载（v1.28.3）

| 平台 | 文件 | 说明 |
| --- | --- | --- |
| Windows 10/11 x64 | [`STJMediaViewer-1.28.3-Setup.exe`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.28.3/STJMediaViewer-1.28.3-Setup.exe) | 安装版（自包含） |
| Windows 10/11 x64 | [`STJMediaViewer-1.28.3-portable.exe`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.28.3/STJMediaViewer-1.28.3-portable.exe) | 便携版（免安装） |
| Android 8.0+ | [`STJMediaViewer-Android-0.4.0.apk`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.28.3/STJMediaViewer-Android-0.4.0.apk) | release 签名 |
| macOS 12+（arm64/x86_64） | [`STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.28.3/STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg) | 通用二进制 |
| 校验 | [`downloads.json`](https://gitcode.com/TMR2333/stj-media-viewer/releases/download/v1.28.3/downloads.json) | 大小 + SHA-256 + 直链 |

## SHA-256

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| STJMediaViewer-1.28.3-Setup.exe | 155,444,193 B | `2ab6d294465f450f850587989bb604789e5d2a0724da00501ab748191ac7686a` |
| STJMediaViewer-1.28.3-portable.exe | 155,444,193 B | `2ab6d294465f450f850587989bb604789e5d2a0724da00501ab748191ac7686a` |
| STJMediaViewer-Android-0.4.0.apk | 14,096,922 B | `91414e7e855d74744c47e1d8d47be8bf8d70eb4e32ea35e721e59cbaea8d6f14` |
| STJMediaViewer-1.36.0-macos-arm64-x86_64.dmg | 5,104,417 B | `027969cbdc5113fe2988ba43df30eb58363b19370168d54f3944474ff793ae90` |

> Windows 的 Setup 与 portable 是同一份自包含产物，哈希相同（安装器负责释放/建快捷方式）。

## 各平台版本对照

| 平台 | 版本 | 源码位置 |
| --- | --- | --- |
| Windows | 1.28.3 | 本仓库 `src/` |
| Android | 0.4.0 | 本仓库 `android/` |
| macOS | 1.36.0 | 独立仓库 `stj-media-viewer-macos` |

## 发版时怎么保持"一个发行版"

1. 正常构建 Windows 安装包/便携版、Android APK；
2. 创建该版本的 Release（`vX.Y.Z`）；
3. 把 **Windows Setup / portable / Android APK / macOS dmg / downloads.json** 全部上传到这一个 Release；
4. 更新本文件与 README 的统一入口指向新版本；
5. 旧版本 Release 保留作回滚，不再上传新附件。

## 自动化脚本

- `tools/gc-upload-cfg.py <tag> <文件名> <字节数> <输出cfg>`：向 GitCode 申请上传地址并生成 curl 配置；
- `tools/gc-release.py <upload-url|patch|list|delete-all|asset-ids> ...`：申请上传地址 / 改发行说明 / 查看附件 / 尽力删除附件。

示例（上传一个附件）：

```bash
python tools/gc-upload-cfg.py v1.28.3 STJMediaViewer-Android-0.4.0.apk 14096922 /tmp/up.cfg
curl -sS --fail -T dist/STJMediaViewer-Android-0.4.0.apk -K /tmp/up.cfg -w ' -> %{http_code}\n'
```