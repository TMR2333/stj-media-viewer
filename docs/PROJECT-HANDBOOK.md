# STJ Media Viewer · 项目手册（功能 · 问题 · 经验）

> 版本：v1.25.0 ｜ 平台：Windows（WinUI 3 / .NET 10） ｜ 代码量：Core 约 29.5k 行 / 95 文件，App 约 128k 行 / 267 文件 ｜ 自检：451 项 / 0 失败
>
> 本文目的：把两年多的产品决策、踩过的坑、可复用的工程经验固化下来。
> 新人（或另一个平台的实现者，比如 macOS 版）读这一篇就能知道**该做成什么、为什么这样做、哪些地方一定会踩坑**。

---

## 1. 产品定位

**面向专业创作者的素材整理软件**，不是修图软件，也不是文件管理器。

核心判断：

- 创作者的痛点不是"打不开照片"，而是**几万条素材没有结构**：RAW、JPG、手机 HEIC、无人机视频、Log 素材、录音、成片与工程文件混在 `2023/上海/`、`导出/`、`Desktop/` 里。
- 传统文件管理器只按名字和日期看；Lightroom / Capture One 把你锁进单一后期工作流。
- 所以本产品把"**先把素材变成结构化数据，再让用户用任何维度去看**"当第一性问题。

三条产品原则（贯穿所有功能）：

1. **不破坏用户文件**：删只进回收站；整理先出计划再移动、可撤销；合并/合集/评分都只写索引与 XMP，不搬文件。
2. **人工永远优先**：任何自动分类（EXIF、文件名启发式、AI）都可被人工覆盖，AI 默认不覆盖人工值，每个值都带来源与置信度。
3. **不锁定工作流**：素材可拖到任何外部软件（PS / LR / C1 / 达芬奇 / 资源管理器），不要求用户先导出。

---

## 2. 技术栈

| 层 | 选型 | 说明 |
| --- | --- | --- |
| 运行时 | .NET 10 / WinAppSDK 2.6.5（WinUI 3），x64 自包含 | 单文件便携版 + Inno 风格安装包 |
| UI | WinUI 3 XAML + Fluent 设计语言 | Mica 背板、Segoe UI Variable 字阶、主题跟随系统/浅色/深色 |
| 索引库 | SQLite（Microsoft.Data.Sqlite） | `%LOCALAPPDATA%\StjMediaViewer\catalog.db`，WAL |
| 元数据 | **自研解析器**（EXIF / IPTC / XMP / ISOBMFF / Matroska / ID3 / TIFF IFD） | 不依赖 exiftool 等外部 exe，保证便携 |
| 图像 | WIC（系统解码）+ SkiaSharp（水印渲染、导出） | RAW 用内嵌预览快路径 |
| 视频 | MediaPlayerElement + ffmpeg（代理转码、LUT 烧录、视频转码） | 系统解不了的 4:2:2 / 10bit 走代理 |
| AI | OpenAI 兼容 Chat Completions（JSON Schema → json_object → 纯文本三级回退） | 用户自带 Key，DPAPI 加密落盘 |
| 云同步 | WebDAV（坚果云/Nextcloud/群晖/123pan）+ 本地挂载目录 | `Expect: 100-continue` 等兼容处理见 §7.4 |
| 地理 | 离线城市库（31,701 城市，GeoNames） | 嵌入程序，GPS → 城市名，无需联网 |
| 测试 | `tools/SelfCheck` 控制台自检（451 项） | 每次发版必跑；另有 MockAiServer / MockWebDav |

---

## 3. 架构分层

```
StjMediaViewer.Core        无 UI 依赖，可单独测试
├── Abstractions/           接口（IAssetStore、IScanner、IXmpWriter、ICloudStorage…）
├── Catalog/                SQLite 实现（SqliteAssetStore、AssetMapper、迁移）
├── Scanning/               LibraryService（扫描/入库/增量/失效清理/重新定位）
├── Metadata/               EXIF/IPTC/XMP/容器解析
├── Media/                  解码、缩略图、RAW 预览、代理转码
├── Classification/         9 维分类规则、设备规范化、词表
├── Ai/                     OpenAI 兼容客户端、提示词、JSON 解析回退
├── Color/                  .cube LUT 解析、色彩处理
├── Write/                  OrganizerService（整理）、XmpWriter、XmpRepairService
├── Cloud/                  CloudSyncService、WebDav/LocalFolder 适配器、暂停/重试
├── Watermark/              SkiaSharp 渲染、品牌字标、导出
├── Geo/                    离线城市库、GPS 聚类
├── Dedup/                  精确 + 近重复
├── Transcode/              ffmpeg 封装
└── Util/                   日志、路径规范化、DPAPI、哈希

StjMediaViewer.App         WinUI 3 表现层
├── Views/                  ShellPage + 12 个页面 + 预览/画廊/查重/检视面板
├── ViewModels/             每个页面一个 VM（CommunityToolkit.Mvvm）
├── Services/               AppServices（组合根）、ThumbnailLoader、预览、设置
├── Controls/               缩略图控件、徽标、空状态、导航等
└── Themes/                 Fluent 主题资源与设计令牌
```

**关键约定**：Core 里不许出现 `Windows.*`（除了极少数 P/Invoke 封装在具体实现里）；
UI 只通过 ViewModel 访问服务；所有长任务都吃 `CancellationToken`；所有 IO 失败都要给可读中文提示。

---

## 4. 数据模型（SQLite）

核心表（schema 稳定，macOS 版可直接复用）：

| 表 | 作用 |
| --- | --- |
| `assets` | 素材主表：路径（唯一索引）、类型、格式、尺寸、时间、相机、GPS、城市、主题/影调、评分、色标、选用/废片、后期状态、Log、origin、编辑状态（旋转/翻转）、AI 来源与置信度、LastError |
| `themes_json`（assets 列） | 多主题（一张图多个主题，第一个为主主题） |
| `asset_custom(asset_id, dimension, value)` | 用户自建分类维度（项目/客户/场地…） |
| `collections` / `collection_assets` | 影像合集（多对多，纯归类） |
| `merge_groups` / `merge_group_assets` | 合并组（多重曝光/星轨等折叠成一张卡） |
| `libraries` | 媒体库根路径（可多库、可重新定位） |
| `settings.json`（非 SQLite） | 应用设置、云同步目标（密码 DPAPI）、设备别名、LUT 绑定、CloudOrigins（来自云同步） |

几个容易做错的语义：

- `is_pick`：`NULL` = 未标记，`1` = 选用，`0` = 废片。**查询"废片"必须用 `is_pick = 0`，不能拿"非 1"**（v1.15.0 踩过）。
- 城市等维度有"未填写"概念：筛选伪值 `__unset__` 在 SQL 层展开成 `IS NULL OR ''`。
- `full_path` 是唯一键；移动/改名必须同步更新 `full_path` **和 `file_name`**（v1.20.2 踩过）。
- 缩略图缓存键包含：路径 + 大小 + 修改时间 + **编辑状态（旋转/翻转）** + **LUT**，少一项就会显示旧图。

---

## 5. 功能全景

### 5.1 扫描与入库

- 多库、可递归/不递归；扩展名白名单 + **魔数嗅探**（`.jpg` 里是文本 → 识别为损坏而不是图片）。
- 排除：`._*` AppleDouble、隐藏文件、`Thumbs.db`/`desktop.ini`/`.DS_Store`、`$RECYCLE.BIN`、`System Volume Information`、`node_modules`、`.git`、`.tmp/.part/.crdownload`。
- 单文件失败不中断扫描：坏文件入库并写 `LastError`，用「只看问题」筛出来。
- 进度三段（枚举/解析/写入），可取消；增量 UPSERT（按 `full_path`），自动清理磁盘上已消失的记录（仅当库根可访问时）。
- 「重新定位」：库目录搬家后按相对路径批量改索引，缩略图自动重建。

### 5.2 元数据引擎（自研）

- 图片：JPEG/PNG/TIFF/HEIF/AVIF/WebP/BMP/GIF/PSD；RAW：CR2/CR3/NEF/ARW/RAF/ORF/RW2/PEF/DNG…；视频：MP4/MOV/MKV/WebM/MXF/AVI；音频：WAV/MP3/FLAC/OGG/AIFF/M4A。
- 覆盖 EXIF、IPTC、**XMP**（内嵌 APP1 / TIFF tag 700 / PNG iTXt / WebP，以及 `.xmp/.dop/.cos/.on1/.pp3/.arp` 边车）、ISOBMFF、Matroska EBML、ID3/Vorbis。
- RAW 内嵌预览：TIFF IFD 513/514 快路径 + 全文件流式扫描最大 JPEG（60MB RAW ≈ 12ms）。

### 5.3 九个分类维度

| 维度 | 来源 |
| --- | --- |
| 大类（图片/视频/音频） | 扩展名 + 魔数 |
| 文件格式 | 扩展名 + 容器/编解码器 |
| 拍摄设备 | EXIF Make/Model、QuickTime、文件名启发式 |
| 拍摄时间 | EXIF DateTimeOriginal（含时区）、QuickTime creation_time、文件时间兜底 |
| 拍摄地点 | GPS → 离线城市库；XMP `photoshop:City` 优先 |
| 是否已调色 | XMP `crs:HasSettings`、边车存在、RAW+同名 JPG、文件名启发式；三态人工 |
| JPG/RAW | 扩展名 + RAW 格式库 |
| 视频是否 Log | CICP transfer、相机型号、容器字符串、XMP |
| 主题 / 影调 | 人工 + AI 视觉模型；"调色"= 已后期 或 已套 LUT |

每个值带来源（Derived/Sidecar/Manual/Ai + model id）与置信度；人工优先。

### 5.4 素材库（主界面）

- 三栏：左侧筛选面板、中间网格、右侧检视面板；窗口 <1000 / <1280 DIP 自动收栏。
- 筛选：类型、格式、设备（含"常用设备 >100 张"）、城市（含"未分类"）、年份/月份、主题/影调（含"未填写"）、评分、色标、选用/废片、后期、Log、影像合集、合并组、**云同步来源**、自定义类别、只看问题。
- 快捷筛选 chip 随媒体类型收敛（视频库只留 Log，图片库只留 RAW）。
- 排序（拍摄时间/文件名/修改时间/大小/评分/入库时间）、升降序、密度（小/中/大/列表）、RAW+JPG 折叠开关。
- 交互：单击/多选/Ctrl 追加/Shift 连选/`Ctrl+A` 全选（= 当前筛选**全部**，不受分页限制）/ **框选**（含边缘自动连续滚动、累计命中、拖动暂停缩略图解码）/ 拖拽到外部软件（JPG+RAW 配对一起给）。
- 合并组：库中只显示封面卡（角标"合并 · N 张"），双击展开；展开时顶部横幅 + **返回**（恢复筛选、库范围、快捷筛选、搜索词、滚动位置，视图状态栈）。
- 影像合集：工具条/筛选面板 `+` 创建；筛到单个合集时出现合集横幅（全选/批量编辑/查看元数据/重命名/删除）。
- 工具条：画廊、查重、批量填写、筛选、检视面板、合并、合集、全选、删除废片。
- 底部状态栏：素材计数、筛选数、已选数、缓存占用、皮肤。

### 5.5 导入

- 外部存储设备（相机卡）识别：按 DCIM 结构、显示设备名与可用空间、JPG 预览 + RAW 同步勾选。
- 子目录模板（`{yyyy}/{MM}`、`{device}`、`{geo}`…）、`{geo}` 按 GPS 聚类（200m–5km，一个范围一个文件夹，命名"上海-黄浦区"或坐标）。
- 导入后按类型统计并自动跳到照片库/视频库；导入结果核验（数量/大小/SHA-256）。

### 5.6 批量填写（导卡/扫描后补信息）

- 支持从素材库选择、从合集传入、从筛选"全选"传入。
- 一次写：评分、色标、选用/废片、城市（单框自由输入，"上海·外滩"）、主题（多选）、影调、后期状态、Log、自定义类别（维度+值）。
- 选中合并组封面会自动展开成组内全部成员（`SelectionExpander`）。

### 5.7 预览与编辑

- 大图预览 + 胶片带 + 键盘浏览；缩放（滚轮/双击/适合/100%）、拖拽平移。
- 旋转/翻转会写库并让缩略图按新方向重建；视频播放失败自动 ffmpeg 生成 720p H.264 代理（缓存键含 LUT）。
- 可用设备绑定 LUT 自动套监视色（仅 Log + 命中绑定才套）。

### 5.8 LUT

- 导入 `.cube`（1D/3D）、按设备绑定、自动套用预览/缩略图/视频代理；用户 LUT 目录可放文件。

### 5.9 地图

- GPS 点聚合、城市反查、按区域浏览；作者之一负责该功能。

### 5.10 时间线

- 按年/月/日聚合，快速跳到某天。

### 5.11 整理（真实移动文件）

- 维度：设备/品牌/年份/年月/月份/日期/城市/国家/主题/影调/自定义类别/格式/类型/评分/色标/扩展名/**细分位置（GPS 范围）**/**AI 整理（主题/影调两层）**。
- **一级/二级/三级目录**（同维度重复选只保留一层）；自定义类别可"有值单独成夹"（`记录=卓越` → `卓越/`，其余照常三级）。
- 先出计划（原→新、冲突数、总大小）再执行；冲突策略：加序号/跳过/覆盖。
- **边车一定跟着走**：直接扫磁盘认 `.xmp/.XMP/.xmp.bak/.acr/.dop/.pp3`，全名式与同名式都认。
- 可选 **删除空的原文件夹**（仅库根内、确实为空的目录）。
- 「找回未转移的 XMP」：扫描孤儿 XMP，按文件名匹配索引，复制到素材现目录（只复制不删）。
- 云端整理：对 WebDAV 用 MOVE、本地挂载用文件移动；撤销上次整理。

### 5.12 云同步（WebDAV / 本地挂载）

- 目标：名称、本地目录、方向（双向/仅上传/仅下载）、冲突策略（较新/本地优先/云端优先/保留两边）、自动同步轮询、传输后核验。
- 预设：坚果云、Nextcloud、群晖、**123 云盘**；密码 DPAPI 加密。
- **连接诊断**：连接→鉴权→列目录→上传探针→核验→清理，逐步 ✓/✗ + 耗时。
- 断连退避重试（3 次，1.2s/3.5s）、上传节奏节流、WebDAV 全局串行、单请求 600s 超时。
- **暂停/继续/取消**：文件之间停住，继续从断点接着传；暂停中也能取消。
- **只上传某一类素材**：分类维度+分类值（如 拍摄地点=平遥）+ 文件格式（全部/RAW/JPG/ARW/MP4/MOV…），可勾"只看选用"；云端可选**一级/二级/三级目录**重建路径；**XMP 边车跟随主文件同目录**；报告区分 上传/失败/跳过。
- 群组协作：上传写 `dc:creator`、云端 `.stj-authors.json` 作者清单（合并更新）。
- 下载回来的素材打 `origin` 标记，素材库可按「来自 123 云盘」筛选。

### 5.13 AI 分类

- OpenAI 兼容（OpenAI / Azure / Ollama / vLLM / 自建网关），Key DPAPI 保存；测试连接显示延迟与模型回显。
- 图片先缩放到 1024 再 base64 上传，RAW 用内嵌预览；`response_format` 三级回退 + 脏输出解析（markdown 代码块、中文引号、百分制）。
- 批量队列：并发可调、重试退避、进度/耗时/成功率、结果写回 XMP；AI 结果默认不覆盖人工值。
- `tools/MockAiServer` 可在没有 Key 时验证全链路。

### 5.14 水印工作台

- 5 个品牌字标（STJARNA/HASSELBLAD/Canon/Nikon/SONY）、6 种风格预设、大小/透明度/垂直位置/投影、Canon 红色原标、Nikon 黄底方块。
- 导出 JPG/PNG/WEBP × 四档质量，保留原像素；批量导出选中素材；本地 SkiaSharp 渲染，不上传。

### 5.15 关于 / 设置

- 关于：作者卡（TMR、一个货成、一只马子、大肥鱼），可点进 B 站/抖音，项目地址。
- 设置：媒体库（增删/重新定位/清理失效记录）、分库开关、扫描并行度、缓存位置与自动清理、设备别名、设备↔LUT 绑定、AI（BaseUrl/模型/Key/尺寸）、ffmpeg 路径、日志/诊断、皮肤与设计语言。

### 5.16 新用户引导

- 首次启动自动进入，引导期间整窗浅色；三步：选库 → 扫描 → 认识筛选/批量填写。

---

## 6. 设计系统（Fluent）

- 令牌集中在 `App/Themes/*`：颜色（背景/表面/描边/强调/危险/文本三级）、圆角（卡片/控件）、字号阶梯（Micro/Caption/Body/Subtitle/SectionHeader/Display）、间距。
- 页面内容左对齐、**1760 DIP 封顶**（超宽屏不拉成一条）；<1180 DIP 两栏变一栏。
- 所有可点选项都是**圆角矩形描边**（chip、工具条按钮、筛选行），选中态 = 强调色描边 + 强调色文字。
- 页面切换 200ms 淡入；网格切换 180ms；子项错峰入场。
- 深浅色跟随系统并可手动，标题栏/窗口按钮区跟着明暗走（`DWMWA_USE_IMMERSIVE_DARK_MODE`）。
- 截图由应用自身渲染（`STJ_CAPTURE=<png>` + `STJ_CAPTURE_DELAY_MS` + `STJ_WINDOW_SIZE`），保证体积小、可复现。

---

## 7. 遇到的问题与根因（按主题）

> 每条都写清：**现象 → 根因 → 修法 → 可复用经验**。这是本文最有价值的部分。

### 7.1 WinUI / 线程 / 崩溃

| 现象 | 根因 | 修法 |
| --- | --- | --- |
| 打开云同步页直接闪退，日志只有 `COMException 0x80004005: Cannot find a resource with the given key: BoolToVisibilityConverter` | XAML 里 `{StaticResource BoolToVisibilityConverter}` 不存在，x:Bind 初始化时查找资源失败 → stowed exception | 改成 VM 暴露 `Visibility` 属性，不依赖转换器 |
| 同上，另一处：`CloudViewModel` 类型初始化失败（NRE at ctor） | `static Shared { get; } = new()` 早于类里后面的 `static readonly` 字段初始化，构造函数读到 null | 构造函数里不使用同类的静态字段（改局部字面量），或把字段声明放到 `Shared` 之前 |
| 双击打不开 / 启动即退，退出码 `0xC00000FD`（栈溢出） | `FirstChanceException` 处理器里写日志；日志文件被占用抛 `UnauthorizedAccessException` → 再次触发处理器 → 无限递归 | 处理器内**绝不写日志**；跳过 IO/日志类异常；`[ThreadStatic]` 递归保护；日志用 `FileShare.ReadWrite|Delete` |
| 导入几百张时偶发 `0xC000027B` 崩溃 | 缩略图解码并发过高（6–16），大量 COM RCW 包装把全局锁打热，UI 线程首次实例化控件被卡死，异常逃逸 | 解码并发限制 2–4（`STJ_THUMB_CONCURRENCY` 可调），`BitmapImage` 创建再限 3 并发；控件构造 try/catch |
| 日志里 1000+ 条 `0x8001010E (RPC_E_WRONG_THREAD)` | 视频切代理/套 LUT 的 `await` 续体在后台线程写 `VideoSource` / 状态文字 | 统一 `RunOnUiAsync` 回 UI 线程再写 UI 属性 |
| `UnobservedTaskException: ObjectDisposedException: SemaphoreSlim` | `Prefetch` 里 `using var gate` 在后台任务 Release 前就 dispose | 等这一批任务全部结束再释放（`Task.WhenAll`） |
| 后台线程未处理异常直接杀进程 | 线程池异常不会进 `Application.UnhandledException` | 同时挂 `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`，并写日志 |
| 无任何日志的闪退（stowed exception 不写 WER） | XAML/绑定层异常绕过托管处理器 | `SetUnhandledExceptionFilter` 原生兜底：写异常码 + `MiniDumpWriteDump` 到 `crashes\*.dmp`；导航加 `[nav] → 页面` 面包屑 |

**经验**：WinUI 3 里"闪退且日志什么都没有"几乎都是 stowed exception：
先在 XAML 里找**不存在的 StaticResource / x:Bind 目标属性 / 构造函数里的静态字段顺序**，再看跨线程写 UI。

### 7.2 索引 / 路径 / 一致性

| 现象 | 根因 | 修法 |
| --- | --- | --- |
| 视频库多出十几条 `C1469.MP4`，点开空白 | 整理冲突改名后只更新 `full_path` 没更新 `file_name`，留下"路径是 `(2)`、文件名是原版"的鬼记录；扫描因记录未变而跳过 | `UpdateAssetPathAsync` 同步更新 `file_name`；启动后清理"索引有、磁盘没有"的记录（仅库根可访问时）；设置页可手动清理 |
| 「只看 RAW」永远 0 条 | JPG+RAW 折叠后只剩 JPG 代表，再叠加"只看 RAW"就没了 | 勾选 RAW 时放行组里的 RAW 行 |
| 批量填写只作用到 200 张 | "全选"只选已加载的一页 | 全选 = 直接查询当前筛选全部结果（`Limit=0`），选集合独立于网格加载 |
| 选中合并组封面，批量填写只改封面 1 张 | 卡片只代表封面素材 | 批量操作前把封面展开成全体成员（去重保序） |
| 合并组折叠后「加载更多」又冒出组员 | 分页追加没走折叠逻辑；且折叠后卡片数 ≠ 原始素材数，用卡片数当 offset 会漏/重 | 加载更多同样折叠；单独记录已取原始条数 |
| 素材文件夹搬家后预览不生成 | 索引存绝对路径，缩略图缓存也指向旧目录 | 库重新定位（按相对路径批量改）；缓存目录不存在时自动回退默认 |
| 删除/移动软件或素材后看到大量"失效" | 索引与磁盘不同步 | 启动自动清理（可访问库根才做）+ 手动"清理失效记录" + 「只看问题」 |

**经验**：路径是唯一键时，**任何"改路径"的写操作都要同时维护 `full_path` 与 `file_name`**；
分页/折叠同时存在时，offset 必须基于"原始行数"而不是"卡片数"。

### 7.3 扫描 / 性能

| 场景 | 数字 / 做法 |
| --- | --- |
| 60MB RAW 取内嵌预览 | TIFF IFD 快路径 + 流式扫描，≈12ms |
| 框选单帧计算 | 从十几 ms 降到 ≈2.5ms：每帧只算一次、只扫已实现容器（≈30 个）、拖动中不改 ListView 选中、松手统一同步、`SetSelection` 用 HashSet |
| 拖动到边缘自动滚动 | 16ms 定时器连续滚（2–20px/帧，越靠边越快），滚动后即时重算命中；拖动期间暂停缩略图解码 |
| 缩略图并发 | 默认 2–4 解码 + 3 个 UI 创建；缓存按"路径+大小+修改时间+编辑状态+LUT"分键 |
| 全库预热 | 设置里可后台预热缩略图，错开首屏高峰 |

**经验**：列表虚拟化场景下，"每个 pointer 事件全量重算"是卡顿根源；
**选中状态要有一份权威集合**（VM），ListView 只是视图，两者在关键时点（松手/全选/切页）同步一次。

### 7.4 云同步 / WebDAV

| 现象 | 根因 | 修法 |
| --- | --- | --- |
| 123pan 只有 XMP 能传，JPG/RAW 全失败（`Response ended prematurely`） | 123pan 网关要求 PUT 带 `Expect: 100-continue`，.NET HttpClient 默认不发；大文件被对端提前断开 | PUT 统一带 Expect；服务器返回 `417` 时回退普通 PUT |
| 坚果云大批照片失败、小 XMP 正常 | 服务端对短时间大量 PUT 限流 | 断连退避重试（3 次）+ 上传节奏节流 + WebDAV 全局串行 + 超时 600s |
| 同步会传 `._*`、`Thumbs.db`、`@eaDir` | 只按扩展名过滤 | 垃圾文件/目录名单 + App 缓存目录排除；云端同类文件比对时忽略但不主动删 |
| 按三级目录上传后 XMP 跑丢 | 只给主文件重建路径，边车仍用原相对路径 | 边车跟随主文件的云端目录（`JPG/xxx.jpg` ↔ `JPG/xxx.jpg.xmp`） |
| 「来自云同步」筛选不出现 | 来源从未被赋值 | 下载成功后记录来源（设置里 `CloudOrigins`）；扫描入库时补 `Origin`；有目标就显示分组 |
| 同步中想停 | 只有取消 | 加 `PauseTokenSource`：文件之间等待，继续从断点传；暂停中可取消 |

**经验**：WebDAV 不是"一个协议"，是一堆网关的实现差异：
**每加一家服务都要用真实账号跑一遍小/中/大文件**，并把「连接诊断」做成产品能力而不是调试手段。

### 7.5 UI / 交互

| 现象 | 根因 | 修法 |
| --- | --- | --- |
| 框选后 Ctrl 单击把之前的选中全清掉 | 框选只写 VM 选中集，ListView 自己的选中集是空的，Ctrl 单击时列表用旧集合合并 | 框选结束写回 `ListView.SelectedItems`（防递归标记），两边始终一致 |
| 框选往下滚，上面的选中消失 | 每次按"当前视口"重算 | 框选期间累计命中集，滚出视口的保留 |
| 长按拖到边缘一卡一卡 | 指针移动才滚 24px/跳；每跳全量重算 | 定时器连续滚 + 每帧重算 + 拖动暂停缩略图 |
| 浅色模式下标题栏还是黑的 | 标题栏透明让 Mica 透出，App 浅色 + 系统深色时透出深色 | 标题栏用与顶栏一致的实色 + `DWMWA_USE_IMMERSIVE_DARK_MODE` |
| 宽屏右侧大片留白 | 页面根容器取"内容想要的宽度" | 按滚动区宽度铺满，1760 DIP 封顶 |
| 「开始上传」按钮和下拉框错位 | 和带 Header 的 ComboBox 同排、底部对齐 | 按钮单独一行 |
| 筛选面板为空（主题/城市全空） | 可选值只在启动时构建一次，扫描/AI 之后的新值不会补 | 每次刷新按最新分面同步选项；空分组自动隐藏；展开状态双向绑定 |

**经验**：凡是"由数据生成的可选项"，都要**随数据刷新而重建**；
凡是"两个地方都保存选中状态"，都要有一个权威源 + 一个明确的同步时点。

### 7.6 打包 / 发布

| 现象 | 根因 | 修法 |
| --- | --- | --- |
| 便携版双击无窗口 | 启动器用 ShellExecute 在部分环境子进程启动即崩 | 先直接 `CreateProcess`，失败再退回 Shell |
| 安装到自定义目录后文件散落 | 安装脚本直接用用户选的目录 | 先创建 `<所选目录>\STJ Media Viewer` 子目录；已是自身目录则不重复 |
| 版本号不一致 | 多处硬编码 | 5 处同时改：App csproj、Installer csproj、Installer Program、build-installer.ps1、macOS Info.plist |
| GitCode 上传失败 | 服务端偶发 203 / ConnectionAborted | 上传脚本带重试（3–5 次），大文件用原始 PUT + Content-Length |

---

## 8. 可复用的工程经验（Checklist）

1. **一个 Core + 薄 UI**：业务全部无 UI 依赖，平台移植只换表现层；这是 macOS 版能存在的前提。
2. **自检即产品的一部分**：451 项检查覆盖扫描/分类/整理/水印/云同步/并发；每次改完先跑自检，再截图核对 UI。
3. **截图由程序自己出**：`STJ_CAPTURE` + `STJ_WINDOW_SIZE` + `STJ_CAPTURE_DELAY_MS`，比外部截屏稳定、可复现、体积小。
4. **日志要能定位到"哪一步"**：所有网络请求一行 `[webdav] 方法 URL → 状态码 字节 耗时`；关键流程有阶段日志；异常记录 HRESULT + 线程 + 次数。
5. **长任务三件套**：可取消（CancellationToken）、可暂停（文件之间）、有进度（阶段 + 文件数 + 百分比）。
6. **禁止静默失败**：任何失败给中文可操作提示（"稍后重试/检查密码/去设置修 ffmpeg 路径"），并写日志。
7. **写操作先计划后执行**：整理/云同步/批量写入都先出计划或预览，用户确认后才落盘。
8. **可逆性**：删除进回收站；整理有撤销；合并/合集删除只删归类。
9. **人工优先**：自动分类永远可覆盖，记录来源与置信度。
10. **平台坑要写进文档**：stowed exception、跨线程写 UI、路径规范化、WebDAV 网关差异、分页 offset、版本号五处改——这些都是"换个平台还会遇到的同类问题"。
11. **测试要用真实数据**：用户库 8000+ 素材、真实 123pan 账号、真实 RAW/视频，才暴露得出限流、并发、编码问题。
12. **版本规则**：每次对外更新 +1（1.24.1 → 1.25.0），提交 → tag → GitCode 发行版 + 附件，CHANGELOG 写清"现象/根因/修法"。

---

## 9. 验证与发布流程

1. 改代码；`dotnet build`（先杀进程，别并发构建）。
2. 跑自检：`tools/SelfCheck/bin/.../StjMediaViewer.SelfCheck.exe --library tools/SampleLibrary/out/library --data <新临时目录>`，要求 **451 / 0**。
3. 需要 UI 的改动：用 `STJ_CAPTURE` 截图（深色 + 浅色、宽窗口），截图存 `shots/`，确认没有假数据。
4. 版本号 5 处同步；CHANGELOG 加一节；README 版本与下载链接更新。
5. 构建安装包与便携版：`tools/Installer/build-installer.ps1`；确认 `FileVersion` 正确；删掉 `*.pdb`、`stj-setup.log`。
6. 提交（`-c user.email=release@stj.local -c user.name=release`）→ `git tag v<版本>` → 推送 master + tag。
7. 建 GitCode 发行版（API `POST /repos/{repo}/releases`），上传 Setup + portable 两个附件，校验 size。
8. 把截图/发行链接写进回复，说明"现象 → 根因 → 修法"。

---

## 10. 已知限制

- Windows 独占 UI（WinUI 3）；macOS 版需要原生重写表现层（见 `docs/macOS-PROMPT.md`）。
- 视频编辑能力仅限转码/压缩/预览代理，不做时间线剪辑。
- AI 只支持 OpenAI 兼容 Chat Completions，不做本地模型推理（可接 Ollama）。
- 离线城市库是 GeoNames cities15000，乡镇级精度有限；更细位置用 GPS 聚类。
- 云端整理依赖 WebDAV MOVE（部分服务不支持，会提示）。

---

## 11. 给 macOS 版的提示

1. **不要逐行翻译 XAML**：用 SwiftUI/AppKit 的原生组织方式（NavigationSplitView + Inspector + Toolbar + 菜单），交互按 macOS HIG。
2. **复用数据契约**：SQLite schema、分类语义（`is_pick` 三态、`__unset__`、多主题 `themes_json`、origin）直接照搬，才能读同一份 catalog.db。
3. **Core 可移植 ≈90%**：算法（解析/分类/LUT/整理路径/云同步比对）用 Swift 重写时照本文的规则与边界条件，别重新发明。
4. **平台坑的同类项**：跨线程写 UI（macOS 是 `@MainActor`）、异步取消、文件协调（`NSFileCoordinator`）、沙盒与安全作用域书签、Keychain 存 Key、WebDAV 同样要处理 Expect/限流/重试。
5. **自检等价物**：Swift 版也做 `stjcheck` 命令行 + XCTest，覆盖同样的业务断言；UI 用 XCUITest/截图比对。
6. 详细的 macOS 提示词见 **[docs/MACOS-PROMPT.md](MACOS-PROMPT.md)**。

---

*本文档随版本维护；新增功能或新踩的坑请补进对应章节。*