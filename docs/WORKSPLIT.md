# stj-media-viewer — 工程分工与硬性规则

> 本文件是多 agent 并发开发的**唯一协作契约**。任何人动手前先读完这一页。

## 1. 目标

面向专业创作者的桌面素材整理软件（Windows 原生，WinUI 3）。核心链路：

```
选媒体库文件夹 → 递归扫描/排除非媒体 → 元数据解析（EXIF/XMP/ISOBMFF/ID3/RAW预览）
→ 自动分类（格式/设备/时间/地点/后期/JPG-RAW/Log/主题/影调）→ SQLite 索引
→ 虚拟化网格 + 筛选 + 检视 + 详情 + AI 批量分类 + 重复检测 + 导出
```

设计语言：**微软 Fluent（单套，token 层驱动，支持明暗）**。Material Design 3 / Liquid Glass 已下线删除。

## 2. 技术底座（已冻结，不要改）

- .NET SDK 10.0.400，TFM 全部 `net10.0-windows10.0.19041.0`，`RuntimeIdentifier=win-x64`。
- 三个项目：
  - `src/StjMediaViewer.Core`（类库，无 UI 依赖；可引用 WinRT 投影）
  - `src/StjMediaViewer.App`（WinUI 3，`Microsoft.WindowsAppSDK 2.6.5`，unpackaged + self-contained）
  - `tools/SelfCheck`（控制台端到端自检）
- 已引入 NuGet：`Microsoft.Data.Sqlite`、`MetadataExtractor`、`CommunityToolkit.Mvvm`、`Microsoft.WindowsAppSDK`。
  **新增依赖必须找 Lead，不要自己改 csproj。**
- 构建：`dotnet build src/StjMediaViewer.App/StjMediaViewer.App.csproj -v q --nologo`（首次 3 分钟，之后 20 秒）。
- 运行产物：`src/StjMediaViewer.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/StjMediaViewer.exe`

## 3. 目录所有权（写作用域）

| 目录 | 所有者 | 内容 |
|---|---|---|
| `src/StjMediaViewer.Core/Models/` | **Lead** | 数据模型，所有人可读，禁止改 |
| `src/StjMediaViewer.Core/Abstractions/` | **Lead** | 接口契约，禁止改 |
| `src/StjMediaViewer.Core/Util/` | **Lead** | 哈希/格式化/日志/路径 |
| `src/StjMediaViewer.Core/Metadata/` | meta-engine | 元数据引擎 |
| `src/StjMediaViewer.Core/Catalog/` | catalog-engine | SQLite 索引库 |
| `src/StjMediaViewer.Core/Scanning/` | catalog-engine | 扫描器 / 库管理 |
| `src/StjMediaViewer.Core/Media/` | catalog-engine | 解码 / 缩略图 / 编码 / 图像分析 / ffmpeg |
| `src/StjMediaViewer.Core/Dedup/` | catalog-engine | 重复检测 |
| `src/StjMediaViewer.Core/Ai/` | intel-engine | AI 客户端与编排 |
| `src/StjMediaViewer.Core/Classification/` | intel-engine | 规则分类 |
| `src/StjMediaViewer.Core/Geo/` | intel-engine | 离线反向地理编码 |
| `src/StjMediaViewer.Core/Settings/` | intel-engine | 设置持久化 + DPAPI |
| `src/StjMediaViewer.App/Themes/` | ui-shell | 设计 token（Base 兜底 + Fluent 取值） |
| `src/StjMediaViewer.App/Services/` | Lead + ui-shell（见任务） | 服务层 |
| `src/StjMediaViewer.App/Controls/` | ui-shell | 通用控件 |
| `src/StjMediaViewer.App/Views/` | Lead（ShellPage）+ ui-views（页面） | 界面 |
| `src/StjMediaViewer.App/ViewModels/` | ui-views | 视图模型 |
| `tools/SampleLibrary/` | tooling | 测试素材库生成器 |
| `tools/SelfCheck/` | tooling | 端到端自检 |
| `tools/MediaSmoke/` | catalog-engine | 解码冒烟测试 |
| `docs/` | Lead | 文档 |

**跨目录改动一律先问 Lead。** 谁的目录谁写满，别人的目录只读。

## 4. 五条铁律

1. **契约冻结**：`Models/` 与 `Abstractions/` 由 Lead 维护。缺东西就绕，别改契约；确实要改，发消息给 lead。
2. **只增不改**：新增实现文件到自己的目录；不要动别人的文件、不要动 csproj、不要动 `.sln`。
3. **构建即真理**：提交前必须 `dotnet build` 0 error。允许 warning，不允许 error。
4. **不跑 GUI**：桌面只有一个屏幕，Lead 负责运行与截图验收。其他人只做编译 / 控制台自检。
5. **真实实现**：不写 TODO、不写空壳、不造模拟数据（除非任务明确要求 mock）。读不到就降级并记日志。

## 5. 数据流约定

```
LibraryScanner ──(IMetadataService)──> MediaMetadata ──ApplyTo──> MediaAsset
      │                                                             │
      ├──(IRuleClassifier.ApplyDerived)──────────────────────────────┤
      ├──(IGeocoder.Reverse)────────────────────────────────────────┤
      └──(IAssetStore.UpsertMany)───────────────────────────────────> SQLite
                                                                     │
     Toolbar/筛选 ──AssetQuery──> IAssetStore.QueryAsync <────────────┘
                                                                     │
  IAiClassificationService.AnalyzeAssetAsync(MediaAsset) ──> AiSuggestion（由调用方写回）
```

- 分类值的**存储形式是稳定 ID**（`nature`/`city`/`tealorange`…），显示走 `Vocabularies`。
- 每个分类维度在 `MediaAsset.Provenance[ClassDimension]` 记录来源（Derived/Sidecar/Manual/Ai）+ 置信度 + 模型 ID。
- **人工结果永远优先于 AI**：AI 批次默认不覆盖 `ClassSource.Manual`。

## 6. 设计语言 token 契约

界面**禁止硬编码颜色/圆角/字号**，一律 `{ThemeResource XxxKey}`。完整键表见
`docs/THEME-TOKENS.md`（ui-shell 负责持续更新）。兜底词典：`App/Themes/Tokens.Base.xaml`。

## 7. 沟通

- 完成后 `send_message` 给 `lead`：文件清单 / 实现要点 / 构建结果 / 风险 / 对外 API 变化。
- 卡住超过 15 分钟就先降级实现并上报，不要无限调试。