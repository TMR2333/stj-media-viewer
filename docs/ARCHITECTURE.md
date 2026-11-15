# stj-media-viewer 架构说明

> 面向专业创作者的 Windows 原生素材整理软件。核心命题：**把一堆散落的照片/视频/音频，变成一个可按 9 个维度检索、可人工/AI 分类、可导出的结构化素材库**，界面遵循微软 Fluent 设计语言（WinUI 3 原生），支持明暗主题。

## 1. 技术选型与理由

| 决策 | 选择 | 理由 |
|---|---|---|
| UI 框架 | WinUI 3（Windows App SDK 2.6.5） | 原生 DWM 背板（Mica/Acrylic）让 Fluent 的窗口材质成为可能；WPF 只能模拟 |
| 运行时 | .NET 10，self-contained | 免装运行时，双击即跑 |
| 打包 | unpackaged | 免 MSIX 签名/商店，方便交付绿色版 |
| 索引 | SQLite（Microsoft.Data.Sqlite） | 万级素材的筛选/分面/时间线必须走 SQL，不能内存 LINQ |
| 元数据 | MetadataExtractor + 自写解析器 | 前者覆盖 EXIF/IPTC/ICC；ISOBMFF/Matroska/ID3/RAW 预览必须自己写才能拿到色彩与 Log 信息 |
| 解码 | Windows Imaging Component（WinRT BitmapDecoder） | 系统级解码器覆盖面最全（含相机厂商 RAW 编解码包）；RAW 额外走"内嵌预览提取"兜底 |
| 视频 | ffmpeg（可选增强） | 抽帧/代理转码；缺失时降级到系统缩略图，不阻断核心功能 |
| 地理编码 | 离线城市库（GeoNames cities15000 + 中文别名） | 隐私友好、零网络依赖、毫秒级 |
| AI | OpenAI 兼容协议 | 一套客户端覆盖 OpenAI / Azure / Ollama / 任意自建网关；用户自带 Key |

## 2. 分层

```
┌──────────────────────────────────────────────────────────────┐
│  StjMediaViewer.App (WinUI 3)                                │
│  Views/ ─ 壳、素材库、检视面板、预览、时间线、地图、设置…        │
│  ViewModels/ ─ CommunityToolkit.Mvvm                         │
│  Themes/ ─ 设计 token（Base 兜底 + Fluent 取值）              │
│  Controls/ ─ GlassPanel / FacetChip / RatingStars / Badge …   │
│  Services/ ─ DesignLanguageService / WindowChrome / Dialog    │
├──────────────────────────────────────────────────────────────┤
│  StjMediaViewer.Core (无 UI)                                  │
│  Models/        数据模型（MediaAsset 是中心）                   │
│  Abstractions/  接口契约（唯一跨模块边界）                      │
│  Metadata/      元数据引擎                                     │
│  Catalog/       SQLite 索引库                                  │
│  Scanning/      扫描器 + 库管理                                │
│  Media/         解码 / 缩略图 / 编码 / 图像分析 / ffmpeg        │
│  Dedup/         重复与近重复检测                               │
│  Ai/            模型客户端 + 分类编排 + 提示词                  │
│  Classification/ 规则推断                                     │
│  Geo/           离线反向地理编码                               │
│  Settings/      设置持久化（DPAPI 保护 API Key）                │
│  Util/          哈希 / 时间 / 格式化 / 日志                     │
└──────────────────────────────────────────────────────────────┘
```

**依赖方向严格单向**：App → Core；Core 内部只依赖 `Abstractions` + `Models`，实现之间通过构造函数注入接口，互相不直接 new。这保证了 6 个模块可以并行开发。

## 3. 数据流水线

```
FolderPicker ──> LibraryRoot
                    │
              LibraryScanner  (递归枚举 / 排除非媒体 / 魔数嗅探 / 边车挂接)
                    │  并行解析
                    ├─ IMetadataService.Read(path) ──> MediaMetadata
                    │      ├─ ImageMetadataReader (EXIF/IPTC)
                    │      ├─ XmpReader (+ 边车 .xmp/.dop/.cos)
                    │      ├─ IsoBmffParser (MP4/MOV/CR3/HEIC)
                    │      ├─ MatroskaParser (MKV/WebM)
                    │      ├─ AudioMetadataReader (WAV/MP3/FLAC/…)
                    │      └─ ColorLogDetector (Log/HLG/PQ/色域)
                    ├─ MediaMetadata.ApplyTo(MediaAsset)
                    ├─ IRuleClassifier.ApplyDerived     (JPG/RAW/Log/后期/设备归一)
                    ├─ IGeocoder.Reverse(lat,lon)       (离线最近城市，XMP 已有城市则不覆盖)
                    └─ FileHasher.QuickHash             (重复检测粗筛)
                    │
              IAssetStore.UpsertMany  ──> SQLite (assets / libraries)
                    │
   UI: AssetQuery ──> IAssetStore.QueryAsync / GetFacetsAsync / GetTimelineAsync
                    │
   AI: IAiClassificationService.AnalyzeAssetAsync
         └─ IMediaDecoder → IImageEncoder(压缩) → IAiClient(视觉模型) → AiSuggestion
         └─ 写回 Theme/Tone/Keywords + Provenance(Ai, confidence, modelId)
```

## 4. 九个分类维度

| # | 维度 | 数据来源 | 存储字段 | 说明 |
|---|---|---|---|---|
| 0 | 大类 | 扩展名 + 魔数 | `Kind` | 图片 / 视频 / 音频 |
| 1 | 文件格式 | 扩展名 + 容器/编解码器解析 | `FormatCode` `MimeType` `ContainerName` `VideoCodec` | JPEG/HEIC/CR3/MP4/MOV/WAV… |
| 2 | 拍摄设备 | EXIF Make/Model、QuickTime make/model、文件名推断 | `CameraMake` `CameraModel` `DeviceKey` | `DeviceNormalizer` 归一为 "Sony A7 IV" |
| 3 | 拍摄时间 | EXIF DateTimeOriginal + OffsetTime、QuickTime creation_time、文件时间兜底 | `CapturedAt` `CapturedAtSource` | 带时区，统一 DateTimeOffset |
| 4 | 拍摄地点 | EXIF GPS → 离线城市库；XMP photoshop:City 优先 | `Latitude` `Longitude` `Country` `Province` `City` `CitySource` | 中文城市名（GeoNames 别名提取） |
| 5 | 是否已后期 | XMP `crs:HasSettings`/`ProcessVersion`、边车存在、RAW+JPG 配对、文件名启发式；**用户可勾选覆盖** | `PostProcess` `HasDevelopEdits` `DevelopEngine` | 三态：未知/未调色/已调色 |
| 6 | 图片类型 | 扩展名 → `ImageFlavor` / `RawFormat` | `ImageFlavor` `RawFormat` | RAW 在界面上有独立琥珀色徽标 |
| 7 | 视频是否 Log | CICP transfer characteristics、相机型号、文件内字符串、XMP | `IsLog` `LogProfile` `TransferCharacteristics` | S-Log3/V-Log/C-Log3/Apple Log/D-Log/HLG/PQ |
| 8 | 拍摄主题 | AI 视觉模型（或人工） | `Theme` | 稳定 ID，词表见 `Vocabularies.Themes` |
| 9 | 拍摄影调 | AI 视觉模型（或人工），规则分类器可从直方图给建议 | `Tone` | 词表见 `Vocabularies.Tones` |

每个维度在 `MediaAsset.Provenance[ClassDimension]` 记录 **来源**（Derived/Sidecar/Manual/Ai）+ **置信度** + **模型 ID**，UI 上以徽标显示。**人工结果优先，AI 默认不覆盖人工值。**

## 5. 设计系统（Fluent + token 契约）

关键设计：**界面只认语义 token，不认具体颜色值**。

- `Themes/Tokens.Base.xaml` 定义全量键（颜色/圆角/字号/动效/控件尺寸）作为兜底与契约。
- `Themes/Tokens.Fluent.xaml` 用**同名键覆盖**成 Fluent 取值，并内含 `ThemeDictionaries` 的 Light / Dark 两套；
  另外覆盖约 140 个 WinUI 框架资源键，让 `NavigationView` / `TextBox` / `ComboBox` / `Button` 等标准控件同步跟随。
- `DesignLanguageService` 负责在启动时把 Fluent 词典合并进 `Application.Current.Resources.MergedDictionaries`，
  并管明暗模式（只改根元素 `RequestedTheme`，既有元素会由框架重新求值，不重建界面）。
- 窗口背板：优先 `MicaBackdrop`，不支持时退 `DesktopAcrylicBackdrop`，再退纯色。
- 历史版本的 Material Design 3 / Liquid Glass 皮肤因观感不达标已整体删除（词典、控件词典、背景光场控件、
  切换入口全部移除；`DesignLanguageIds.Normalize` 会把旧的 `"md3"/"glass"` 配置回落到 Fluent）。

详见 `docs/THEME-TOKENS.md`。

## 6. 性能约束

| 场景 | 目标 | 手段 |
|---|---|---|
| 1 万条素材入库 | < 15s | 单事务批量 upsert、并行解析、魔数只读前 32 字节 |
| 筛选查询 | < 50ms | SQL 索引 + 分页 + 计数走 GROUP BY |
| 网格滚动 | 60fps | 虚拟化容器 + 缩略图异步加载 + 内存 LRU |
| 缩略图生成 | 200 张/分钟 | 磁盘缓存（路径+大小+修改时间 → hash 键） |
| AI 批量分类 | 并发 3 | SemaphoreSlim + 失败重试 + 本地结果缓存 |

## 7. 目录与验收

- 构建：`dotnet build src/StjMediaViewer.App/StjMediaViewer.App.csproj`
- 运行：`src/StjMediaViewer.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/StjMediaViewer.exe`
- 端到端自检：`dotnet run --project tools/SelfCheck`
- 测试素材库：`python tools/SampleLibrary/generate.py`
- 截图验收：`pwsh -File tools/dev/shot.ps1 -Out shots/x.png`

## 8. 借鉴的开源项目

| 项目 | 用途 |
|---|---|
| [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) | EXIF/IPTC/ICC 解析（Apache-2.0） |
| [FFmpeg](https://ffmpeg.org/) | 视频抽帧、代理转码、音频生成（LGPL/GPL，作为外部可执行文件调用，不静态链接） |
| [GeoNames cities15000](https://download.geonames.org/export/dump/) | 离线反向地理编码（CC BY 4.0） |
| [exiftool 文档](https://exiftool.org/TagNames/) | EXIF/XMP/QuickTime 标签语义参考 |
| [ISO/IEC 14496-12 (ISOBMFF)](https://www.iso.org/standard/83102.html) | MP4/MOV 盒子结构参考 |
| [Fluent 2](https://fluent2.microsoft.design/) / [WinUI 3 文档](https://learn.microsoft.com/windows/apps/winui/winui3/) | 界面设计语言与控件规范来源 |