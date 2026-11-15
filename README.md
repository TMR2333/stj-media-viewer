# stj-media-viewer

面向专业创作者的 **Windows 原生素材整理软件**。选一个媒体库文件夹，它递归扫描、排除非媒体、解析元数据、自动分类，并给你一个能按 9 个维度检索的素材库。界面遵循**微软 Fluent 设计语言**（WinUI 3 原生、Mica 背板、Segoe UI Variable 字阶），支持跟随系统 / 浅色 / 深色。

<p align="center">
  <img src="shots/final-fluent.png" width="70%" alt="素材库主界面" />
</p>
<p align="center">
  <img src="shots/final-timeline.png" width="32%" alt="时间线" />
  <img src="shots/final-aiqueue.png" width="32%" alt="AI 分类队列" />
</p>
<p align="center">
  <img src="shots/final-settings.png" width="49%" alt="设置页：接入你自己的视觉模型" />
  <img src="shots/final-duplicates.png" width="49%" alt="重复项" />
</p>

> 截图由应用自身渲染导出（`STJ_CAPTURE=<png>` 环境变量），不依赖外部截屏工具。

---

## 1. 它解决什么问题

专业创作者的硬盘里通常躺着几万条素材：相机 RAW、手机 HEIC、无人机视频、Log 素材、现场录音、修过的成片和没修的原片混在一起，散落在 `2023/上海/`、`导出/`、`Desktop/` 之类的目录里。传统文件管理器只能按文件名和日期看，Lightroom/Capture One 又把你锁在单一后期工作流里。

这个软件把"素材整理"本身当成第一性问题：**先把素材变成结构化数据，再让你用任何维度去看它**。

## 2. 功能清单

### 2.1 扫描与排除

- 选择一个或多个媒体库文件夹，递归扫描（可关子目录）。
- **自动排除非媒体**：扩展名白名单 + 魔数嗅探（`.jpg` 里装的是文本会被识别为损坏而不是图片）、AppleDouble `._*`、隐藏文件、`Thumbs.db`/`.DS_Store`/`desktop.ini`、`$RECYCLE.BIN`、`System Volume Information`、`node_modules`、`.git`、临时文件（`.tmp/.part/.crdownload`）。
- 单文件解析失败不中断整体扫描：坏文件照样入库并标记 `LastError`，可用「只看问题」筛出来。
- 扫描进度实时（枚举 / 读元数据 / 写索引三段），可取消。
- 增量重扫：按完整路径 UPSERT，不会产生重复记录；同时清理已从磁盘消失的条目。

### 2.2 元数据引擎（自研，不依赖外部 exe）

| 类型 | 覆盖 |
|---|---|
| 图片 | JPEG / PNG / TIFF / HEIF / AVIF / WebP / BMP / GIF / PSD |
| RAW | CR2 / CR3 / NEF / NRW / ARW / SRF / RAF / ORF / RW2 / PEF / SRW / X3F / DNG / IIQ / 3FR / MOS / GPR … |
| 视频 | MP4 / MOV / M4V / MKV / WebM / MXF（基础）/ AVI（基础） |
| 音频 | WAV（含 RF64）/ MP3（ID3v2.2–2.4）/ FLAC / OGG / Opus / AIFF / M4A(AAC) |

解析内容：EXIF / IPTC / **XMP**（内嵌 APP1、TIFF tag 700、PNG iTXt、WebP、以及 `.xmp/.dop/.cos/.on1/.pp3/.arp` 边车）、ISOBMFF 盒子（时长/尺寸/帧率/编解码器/`colr` CICP/`udta` 的 make/model/GPS/creation_time/comment）、Matroska EBML、ID3/Vorbis 标签、**RAW 内嵌预览提取**（TIFF IFD 513/514 快路径 + 全文件流式扫描最大 JPEG，60MB 大 RAW 12ms）。

### 2.3 九个分类维度

| # | 维度 | 自动来源 | 手动 | AI |
|---|---|---|---|---|
| 0 | 大类（图片/视频/音频） | 扩展名 + 魔数 | — | — |
| 1 | 文件格式 | 扩展名 + 容器/编解码器 | — | — |
| 2 | 拍摄设备 | EXIF Make/Model、QuickTime、文件名 | ✓ | — |
| 3 | 拍摄时间 | EXIF DateTimeOriginal + 时区、QuickTime creation_time、文件时间兜底 | ✓ | — |
| 4 | 拍摄地点（城市） | GPS → **离线城市库**（31,701 城市，含中文名）；XMP `photoshop:City` 优先 | ✓ | — |
| 5 | 是否已后期调色 | XMP `crs:HasSettings`、边车存在、RAW+同名 JPG、文件名启发式 | ✓ 三态 | — |
| 6 | JPG / RAW（RAW 特殊标记） | 扩展名 + RAW 格式库 | — | — |
| 7 | 视频是否 Log | CICP transfer、相机型号、容器内字符串、XMP | ✓ | ✓ |
| 8 | 拍摄主题 | — | ✓ | ✓ 视觉模型 |
| 9 | 拍摄影调 | 直方图/饱和度/色温规则建议 | ✓ | ✓ 视觉模型 |

每个维度的取值都带**来源与置信度**（`Derived` / `Sidecar` / `Manual` / `Ai` + model id），界面上以徽标显示；**人工结果永远优先，AI 默认不覆盖人工值**。

### 2.4 AI 分类：接你自己的模型

- 只要求 **OpenAI 兼容协议**（`POST {BaseUrl}/chat/completions`），因此 OpenAI / Azure OpenAI / Ollama / vLLM / 任意自建网关都能用。
- 设置页填 BaseUrl、模型名、API Key（**DPAPI 加密**后落盘），「测试连接」直接告诉你延迟、模型回显或错误原文。
- 图片会先经 WIC 解码 + 缩放到 `MaxImageDimension`（默认 1024）再以 base64 上传；RAW 自动使用内嵌预览。
- `response_format` 三级回退：json_schema → json_object → 纯文本解析（兼容 markdown 代码块、中文引号、百分制分数等脏输出）。
- 批量队列：并发度可调、失败重试 + 指数退避、进度/耗时/成功率统计、结果可一键写回 XMP。
- **没有 API Key 也能验证全链路**：仓库自带 `tools/MockAiServer`（纯标准库 Python，确定性输出），设置页把 BaseUrl 指向 `http://127.0.0.1:8787/v1` 即可。

### 2.5 整理与输出

- 三栏素材库：**左侧 11 组分面筛选（带计数）+ 中间虚拟化网格 + 右侧检视面板**。
- 网格：小/中/大/列表四种密度、RAW/LOG/AI/时长徽标、颜色标签条、5 星评分、多选（Ctrl/Shift）、方向键导航、右键菜单、200 条/页滚动加载。
- 检视面板：大图预览、8 组元数据（带来源徽标）、手动分类（主题/影调/后期三态/城市/评分/色标/选用废片）、保存 / AI 分类此条 / 写入 XMP。
- 全屏预览：滚轮指针锚点缩放 0.1–8x、拖拽平移、双击 1:1、左右切图、视频/音频播放。
- 时间线（年/月/天）、地图（城市分布）、重复项（精确 + 近重复 aHash）、AI 队列。
- 批量导出（按 `{yyyy}/{city}/{device}` 模板分目录，可移动或复制，边车一起带走）、批量重命名（模板变量）、回收站删除。
- 重复检测：`size + 快速 FNV 指纹` 粗筛 → `SHA-256` 精确 → `8x8 aHash` 近重复。
- 缩略图磁盘缓存 + 内存 LRU；缓存大小可见、可清理。

### 2.6 外部存储设备 → 导入（JPG 预览 + RAW 同步）

![导入页](shots/final-import.png)

- **读外部存储**：枚举 U 盘 / SD 卡 / 相机 / 移动硬盘（`DriveInfo` 快枚举 + `IOCTL_STORAGE_QUERY_PROPERTY` 探总线类型，所以"USB 接进来的固定盘"也能认出来），显示卷标、盘符、文件系统、容量条与剩余空间；有 `DCIM/100CANON` 这类结构时直接给出**相机卡线索**并作为扫描起点。也可以"浏览任意文件夹"导入。
- **JPG 当预览、RAW 同时导入**：扫描源目录时按"同目录 + 同基名"配对，JPEG 作为预览与组代表，RAW 原件一起进计划；边车（`.xmp/.dop/.cos`）自动跟随。
- **一组一张照片**：导入计划与图库都按组展示 —— 网格里一张照片只出现一次（代表是 JPG），角标 **RAW&JPEG**；要看全部文件时工具条勾「RAW 副本」即可展开。
- **移动模式更安全**：`移动` = 拷贝 → 逐文件核验通过 → 才删源文件，中途失败不丢素材。
- **核验机制（导出/导入通用）**：严格模式逐文件 **SHA-256** 比对、快速模式比对"大小 + 首尾指纹"；再叠加**数量对账**（源侧每个文件必须在目标侧找到同名同大小同哈希的文件），任何"损坏 / 漏导"都会在报告里逐条列出。页尾还有独立的「导出核验」区，可对任意两棵目录树做一次完整对账。

### 2.7 云同步（坚果云 / Nextcloud / 群晖 NAS / 任意挂载目录）

![云同步页](shots/final-cloud.png)

- **两种接入方式**：`WebDAV`（坚果云、Nextcloud/ownCloud、群晖 DSM、威联通 QTS，自定义地址/账号/应用密码/云端子目录）与`本地挂载目录`（OneDrive、坚果云客户端目录、SMB 映射盘、已挂载 NAS）；同步路径、方向、冲突策略都可在界面里改。
- **双向 / 单向**：双向（两边互补）、仅上传（本地→云端）、仅下载（云端→本地，云端照片自动落到本地图库目录）；默认**永不删除**任何一端的文件，只补齐缺失与传输变更。
- **冲突策略**：较新者胜（默认）/ 本地优先 / 云端优先 / 两边都保留（本地另存 `xxx (本地 时间戳).jpg`、云端另存 `xxx (云端 时间戳).jpg`）。
- **先预览再执行**：差异计划（上传 x / 下载 y / 冲突 z + 逐条明细）→ 演练（dry-run，不落盘）→ 正式执行；进度条 + 逐文件失败明细。
- **自动同步**：后台按轮询间隔（默认 5 分钟）检查，本地目录变化由 `FileSystemWatcher` 5 秒抖动合并触发；同一目标不会并发跑两轮；传输后可逐文件哈希核验（本地目录目标用 SHA-256 双边比对，WebDAV 用大小 + 回列校验）。
- **凭据安全**：WebDAV 密码用 Windows DPAPI（当前用户域）加密后存 settings.json，明文不落盘。
- 实现细节：`Core/Cloud/` 里 `ICloudStorage` 把协议抹平（`WebDavCloudStorage` 用 `PROPFIND Depth:1` 逐层下钻 + Basic 认证 + 分段 URL 编码；`LocalFolderCloudStorage` 走文件系统），`CloudSyncService` 只做一次"本地索引 ↔ 云端条目"比对再分发动作，所以以后加 S3/FTP 只需再写一个适配器。

### 2.8 视频编码识别与转码 / 压缩

- **编码显示**：视频卡片与检视面板显示归一化编码（`avc1/h264/x264` → **H.264 / AVC**，`hev1/hvc1/hevc` → **H.265 / HEVC**，另有 AV1 / VP9 / ProRes / DNxHR / Dolby Vision / MPEG-4/2 等），一眼看出是否需要转码。
- **探测**：不依赖 ffprobe —— 直接解析 `ffmpeg -i` 的流信息，拿到编码、分辨率、码率、时长、帧率、位深、像素格式、HDR（HLG/PQ）、音轨、旋转。
- **内置预设**：H.265 1080p 压缩、H.264 1080p 压缩（兼容性最好）、H.265 720p 手机分享、高画质 H.264 原分辨率、**转封装（`-c copy` 不重编码，零画质损失）**、AV1 720p（最省体积）。
- **可调参数**：编码器（H.265 / H.264 / AV1 / 不重编码）、CRF 质量、目标分辨率、速度预设、硬件加速开关；输出体积按源码率/时长/CRF 估算并给出"预计减少 %"。
- **硬件加速**：启动时用 `ffmpeg -encoders` 探测本机能力，优先 `h264_nvenc` / `hevc_nvenc` / `av1_nvenc`（QSV/AMF 同理），不可用自动回落 `libx264` / `libx265` / `libsvtav1`；对话框里直接显示当前机器可用编码器。
- **执行与校验**：实时进度（已处理时长、倍速、剩余 ETA）、完成后**重新解析输出**确认编码/分辨率正确，并给出"体积从 x 到 y、减少 z%"；输出文件名自动带 `_hevc1080p` 之类的后缀，避免覆盖原件。
- **批处理**：`ITranscodeService.TranscodeManyAsync` 支持批量（进度按文件数加权），供后续"选中多个视频一起压"使用。

### 2.9 画廊视图（Apple Photos / Camera Raw 风格）

![画廊视图](shots/final-gallery.png)

- **大画布 + 底部胶片带**：上方信息条（文件名、设备、时间、真实分辨率、体积、视频编码、评分/色标/选用状态、序号），中间画布只给照片让路（深色 scrim），底部横向胶片带显示当前项强调描边、评分与编码角标。
- **缩放与平移**：滚轮以鼠标位置为锚点缩放（5%–800%）、双击在「适应窗口 / 100% 实际像素」之间切换、按住左键拖拽平移；窗口、胶片带、面板尺寸变化时自动重新适应。
- **键盘流**：左右方向键翻页、Home/End 首尾、0–5 评分（0 清除）、6–9 色标（红/黄/绿/蓝）、P 选用、X 废片、I 元数据面板、T 胶片带 —— 全程不碰鼠标。
- **复用现有能力**：数据源是素材库的**当前筛选条件**（搜索 / 分面 / 分组都继承），右侧内嵌同一个检视面板（元数据 + 手工分类 + 保存 / AI / XMP），视频可直接转码压缩，快速编辑立即落库。
- 解码只取当前一张（2400px），切换时取消上一张请求；胶片带缩略图走同一套磁盘缓存，4000+ 张的库翻页也不掉帧。

### 2.10 界面

- 微软 Fluent 设计语言：Mica 窗口背板、Segoe UI Variable 字阶、细描边、键盘友好的焦点视觉；标准控件（NavigationView / TextBox / ComboBox / Button…）由 ~140 个框架资源键统一覆盖。
- 明暗模式：跟随系统 / 浅色 / 深色，切换只重解析 token，不重建界面。
- 全部界面只用语义 token（`{ThemeResource ...}`），页面里没有一处硬编码颜色/字号/圆角。

## 3. 快速开始

```powershell
# 环境：Windows 10 1809+ / Windows 11，.NET 10 SDK
git clone <repo> ; cd stj-m-v

# 1) 编译
dotnet build src/StjMediaViewer.App/StjMediaViewer.App.csproj

# 2) 运行（免安装运行时，self-contained）
.\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe
```

首次启动点顶部「扫描」→ 选一个素材文件夹。想先用测试数据体验：

```powershell
# 生成 85 个文件的真实测试素材库（含 EXIF/XMP/RAW/真实 H.264 视频/音频/反例/重复）
python tools/SampleLibrary/generate.py
# 把它扫进 App 的索引库
.\tools\Seed\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.Seed.exe
```

ffmpeg 是可选的：把 `ffmpeg.exe` 放到 `tools/ffmpeg/bin/`（或 PATH）即可获得视频抽帧/代理转码；没有则降级到系统缩略图，不报错。

## 4. 工程与验证

```
src/StjMediaViewer.Core    # 无 UI 依赖：模型/契约/元数据/索引/扫描/解码/AI/地理/设置
src/StjMediaViewer.App     # WinUI 3：Views / ViewModels / Themes / Controls / Services
tools/SelfCheck            # 端到端自检（扫描→元数据→分类→查询→缩略图→去重→AI→导出→性能）
tools/SampleLibrary        # 测试素材库生成器（Python，幂等）
tools/MockAiServer         # 本地 OpenAI 兼容 mock 服务器（纯标准库）
tools/Seed                 # 把素材库扫进索引（命令行）
tools/MetaSmoke / MediaSmoke / dev
```

**验证结果**（本机 Windows 11 / .NET 10 / 85 文件样本库）：

| 指标 | 结果 |
|---|---|
| 端到端自检 | **69 项检查全部通过 / 0 失败**；其中 manifest 逐字段断言 **618/618 全过** |
| 真实 HTTP AI 链路 | 用本地 MockAiServer 实测：连接测试 / 单张分类 / 确定性 / 批量写回 全通过 |
| 59 个媒体文件全链路扫描 | 1 线程 308ms，8 线程 38ms（结果逐字段一致） |
| 单文件元数据解析 | 平均 3.3ms（稳态 <5ms），60MB RAW 预览提取 12ms |
| 1 万条素材入库 | 1.24–1.43s（约 7000 条/秒） |
| 筛选查询 | 2–10ms；分面统计 13–17ms |
| 缩略图 | 4678 张/分钟（目标 200/分钟），缓存命中 <1ms |
| 重复检测 | 58 条素材 319ms，精确 3 组 + 近重复 2 组 |
| 离线地理编码 | 31,701 城市 / 加载 48ms / 8 个 manifest 坐标全部命中 |

```powershell
# 自检
dotnet build tools/SelfCheck/StjMediaViewer.SelfCheck.csproj
.\tools\SelfCheck\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.SelfCheck.exe
```

## 5. 设计系统（Fluent）

界面只认**语义 token**，不认具体颜色值：

- `App/Themes/Tokens.Base.xaml` 定义全量键（颜色/圆角/字号/动效/控件尺寸）作为兜底与契约。
- `App/Themes/Tokens.Fluent.xaml` 用**同名键**覆盖成 Fluent 取值，另含 ~140 个 WinUI 框架资源键（`NavigationView*`、`TextControl*`、`ComboBox*`、`Button*`…），所以标准控件也跟着 token 走；Light / Dark 两套 `ThemeDictionaries` 让明暗切换只重解析、不重建界面。
- 早期版本尝试过 Material Design 3 与 Liquid Glass 皮肤，因观感不达标已下线（词典文件与切换入口都已删除，`DesignLanguageIds` 会把旧的 `"md3"/"glass"` 配置安全回落到 Fluent）。

细节见 [docs/THEME-TOKENS.md](docs/THEME-TOKENS.md) 与 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 6. 已知限制

- 暂未深度解析：AVI / MXF / BRAW / R3D / CRM / INSV / MPEG-TS（只给扩展名 + warning）；PSB 不做图层解析。
- 真实相机 HEIC 的 EXIF/XMP 若在 `iloc` 引用的 item 里，走特征扫描兜底（限 64MB）。
- 城市反查用离线 GeoNames cities15000（31,701 城市，含中文名）：极少数坐标会落到邻近区县；非中国城市的 `Province` 仍是 admin1 代码。
- 视频色彩字段用可读名（`HLG` / `S-Log3` / `BT.709`），与 ffprobe 的 `arib-std-b67` 之类原始枚举名不同（仅命名口径差异）。
- 批量 AI 分类的 token 用量未落库（只统计本次运行）。
- 未做 HighContrast 主题。
- 时间线一次构建全部日桶（1 万条约 1000 天，已折叠到月份里），超大库建议改成按月懒加载。
- AI 队列的"暂停"是取消当前批次并保留进度，不是协程级暂停。
- 没有 GPU 加速的图片渲染（WIC 解码 + XAML 虚拟化），10 万级库需要分库或增量加载策略。

## 7. 开源致谢

| 项目 | 用途 | 许可 |
|---|---|---|
| [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) | EXIF/IPTC/ICC 解析 | Apache-2.0 |
| [FFmpeg](https://ffmpeg.org/) | 视频抽帧、代理转码（外部可执行，不静态链接） | LGPL/GPL |
| [GeoNames cities15000](https://download.geonames.org/export/dump/) | 离线反向地理编码 | CC BY 4.0 |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | 索引库 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM | MIT |
| [ExifTool 标签文档](https://exiftool.org/TagNames/) / [ISO/IEC 14496-12](https://www.iso.org/standard/83102.html) | 标签与容器语义参考 | — |
| [Fluent 2 设计规范](https://fluent2.microsoft.design/) · [WinUI 3 文档](https://learn.microsoft.com/windows/apps/winui/winui3/) | 界面设计语言与控件规范 | — |

> 界面字体使用系统内置的 Segoe UI Variable / Segoe Fluent Icons，不额外分发第三方字体。