# 云相册（Cloud Album）跨平台规范 v1

> 目标：Windows / macOS / Android 三端提供同一套「云相册」：**浏览云端 → 预览 → 导入导出 → （原有）云同步**。
> 云同步不再是独立功能，合并进云相册页面/入口。

## 1. 概念

- **云相册** = 一个 WebDAV 目标（也可用本地挂载目录模拟）上的远程素材目录树。
- **浏览**：只列一层目录（PROPFIND Depth:1），面包屑导航，可返回上级。
- **预览**：缩略图 + 点击看大图/视频；缩略图**按需生成并本地缓存**，不预下载整库。
- **导入**（云 → 本地）：把选中的远程文件下载进本地素材库目录，保持相对目录结构，并触发扫描入库。
- **导出**（本地 → 云）：把本地素材库里选中的素材（含 XMP 边车）上传到当前云相册目录。
- **同步**：原云同步（差异计划、双向/单向、按分类/筛选上传、暂停/取消、报告）保留，入口并入本页。
- **交换文件**（`stj-media-viewer/exchange` v1）仍独立：用于设置+编辑成果跨端迁移，与云相册无关。

## 2. 服务器能力要求与降级

| 能力 | 必需 | 缺失时 |
| --- | --- | --- |
| PROPFIND Depth:1（目录列举） | ✅ | 不能浏览；可用 `Depth:infinity` 全量列举后前端过滤 |
| GET（下载） | ✅ | 不能预览/导入 |
| **Range GET（`bytes=off-len`）** | 建议 | 缩略图/预览退化为「整文件下载后生成」；大 RAW 不做缩略图 |
| PUT（上传） | 导出需要 | 只能导入，不能导出 |
| MKCOL（建目录） | 导出需要 | 只允许导出到已存在目录 |
| Basic 认证 | ✅ | Digest/OAuth 暂不支持 |

### 实测（123pan，2026-11）

- `HEAD` 返回 200 + `Content-Length`（不声明 `Accept-Ranges`）；
- `GET Range` 先 **302 到 CDN 签名地址**，跟随重定向后返回 **206 + Content-Range**；
- 结论：**每次请求都要重新走 302 并重发 Range 头**；不要把 CDN URL 缓存到过期。

## 3. 缩略图策略（三端一致）

1. 文件 ≤ 12 MB：整文件下载 → 解码/取内嵌预览 → 生成 ≤ 320px JPEG 缩略图 → 缓存。
2. 大文件（RAW/视频）：
   - 先 Range 读 **2 MB 头**；TIFF 家族解析 IFD0/IFD1/SubIFD 的 `JPEGInterchangeFormat(513)/Length(514)`；
   - 命中且 preview ≤ 20 MB：Range 读该段（JPEG）→ 解码 → 缩略图；
   - 未命中：文件 ≤ 48 MB 才整文件下载，否则显示占位。
3. 视频：不做服务端转码；≤ 60 MB 可整文件下载取首帧（桌面端用 ffmpeg/系统缩略图，Android 用 MediaMetadataRetriever），否则占位。
4. 缓存键 = `sha1(相对路径 | 大小 | 修改时间 | 目标像素)`；磁盘缓存目录：
   - Windows `%LOCALAPPDATA%\StjMediaViewer\Cache\cloud-album`
   - macOS `~/Library/Caches/StjMediaViewer/cloud-album`
   - Android `cacheDir/cloud-album`
5. 并发上限 3；失败负缓存 5 分钟。

## 4. 导入导出语义

- 导入：目标 = 本地素材库根目录 + 远程相对路径；同名覆盖；只下载文件（目录跳过）；完成后触发该库扫描；报告「下载 N / 失败 M / 字节数 / 耗时」。
- 导出：目标 = 当前云相册目录（扁平）；每个素材上传原文件 + 已存在的同名/stem XMP 边车；报告同上；上传前必要时 MKCOL。
- 安全：任何 `..`、绝对路径、越界组合一律拒绝；密码永不写入交换文件/日志（日志脱敏）。

## 5. 页面结构（三端一致的心智模型）

```
云相册
├── 账号 / 目标（WebDAV 地址、账号、密码；本地挂载目录）
├── 浏览区：面包屑 | 上一级 | 刷新 | 全选 | 下载到素材库 | 上传所选素材
│   └── 网格：文件夹 / 文件缩略图 + 名称 + 大小（选中态）
├── 状态行（连接结果、进度、错误原因）
└── 同步区（原云同步）：目标列表、差异预览、立即同步、按分类上传、暂停/取消、报告、诊断
```

- Windows：导航「云相册」，`CloudPage` 顶部浏览卡片 + 原同步卡片。
- macOS：侧栏「云相册」，SwiftUI 网格 + 原同步面板。
- Android：顶部「云相册」按钮进入独立页面，底部下载/上传操作条。

## 6. 验收（三端通用）

- [ ] 配置错误地址/密码 → 明确错误原因，不崩溃。
- [ ] 列出 > 500 个文件的目录，首屏 < 3s 出骨架，缩略图逐步出现。
- [ ] 双击文件夹进入、上一级返回；中文/空格/`#` 路径正常。
- [ ] 123pan（302+Range）：缩略图能出、预览能打开。
- [ ] 勾选 3 张 → 下载到素材库 → 扫描后能在库中看到，且文件大小一致。
- [ ] 素材库选 2 张（含 XMP）→ 上传 → 云端两个文件 + `.xmp` 可见；再刷新列表出现。
- [ ] 服务器不支持 Range：仍能靠整文件下载出缩略图；≤ 12MB 文件不报错。
- [ ] 原云同步功能（差异预览、执行、按分类上传、暂停/取消）在同一页面仍可用。
- [ ] 断开网络：浏览/缩略图/导入/导出分别给出可读错误；恢复后可重试。

## 7. 实现映射

| 层 | Windows | Android | macOS |
| --- | --- | --- | --- |
| 列目录 | `ICloudStorage.ListFolderAsync`（WebDAV PROPFIND Depth:1 / 本地目录） | `WebDavClient.list` | `CloudStorage.listFolder` |
| Range | `ReadRangeAsync`（HttpClient + Range，123pan 302 由 HttpClient 跟随） | `WebDavClient.range`（OkHttp 手动 302 重发 Range） | `URLSession` Range + 手动重定向 |
| 缩略图 | `CloudAlbumService.GetThumbnailPathAsync` | `CloudAlbumRepository.thumbnail` | `CloudAlbumService.thumbnail` |
| 导入/导出 | `ImportAsync` / `ExportAsync` | `importToDevice` / `upload` | 同名 API |
| UI | `Views/CloudPage.xaml` 浏览卡片 | `cloud/CloudAlbumScreen.kt` | `CloudAlbumView.swift` |

## 8. 已知限制 / 后续

- 不做服务端缩略图（除非未来接 Nextcloud preview API）。
- 视频不转码；弱网下大 RAW 只显示内嵌预览。
- 云端编辑仍只写 XMP 边车（原图不动）；删除/移动暂不做（下一步）。
- 早期版本留下的 `xxx.jpg.xmp.xmp`、`.xmp.xmp.bak` 递归边车文件建议在云端清理；扫描器的边车发现逻辑应忽略 `.xmp`/`.bak` 自身的边车。