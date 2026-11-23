# STJ 素材库 · Android（MD3 Expressive 原生）

> 移动伴生版：与 Windows 版共享 **SQLite 索引语义** 与 **`stj-media-viewer/exchange` v1 交换文件**，
> UI 用 **Jetpack Compose + Material 3 Expressiive**（动态取色 / 大标题 / 弹性动效 / 超圆角卡片），
> 不是把桌面 WinUI 界面缩小搬过来。

## 1. 这个版本做什么

移动端真正会用的部分（P1 范围）：

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 扫描入库 | ✅ | 三种来源：**系统相册（MediaStore）**、**SAF 目录树**、**root 任意目录** |
| 元数据 | ✅ | EXIF（时间/相机/GPS/方向）、视频时长/尺寸、RAW 内嵌预览、XMP 边车读取 |
| 离线城市反查 | ✅ | 31,701 城市（ GeoNames cities15000，CC BY 4.0），GPS → 城市名 |
| 素材库网格 | ✅ | 自适应网格、密度（小/中/大）、缩略图（含 RAW/视频首帧）、RAW/LOG/视频角标 |
| 筛选 | ✅ | 类型 / 格式 / 设备 / 地点（含未分类）/ 年份 / 主题 / 影调（含未填写）/ 评分 / 色标 / 选用三态 / 未调色 / Log |
| 搜索 + 排序 | ✅ | 文件名/城市/设备/关键词；拍摄时间/文件名/修改时间/大小/评分 |
| 详情 + 编辑 | ✅ | 评分、色标、选用/废片/未标记、调色状态、Log、城市、标题、主题（多选）、影调、关键词 |
| 写回 XMP | ✅ | 编辑后写 `<文件>.xmp`（root 模式下可写普通应用无权限的目录） |
| 批量编辑 | ✅ | 多选后统一改评分/色标/选用/调色 |
| 交换文件 | ✅ | 与 Windows/macOS 双向导入导出（v1 contract，不含密钥） |
| root 模式 | ✅ | 可选开关：`su` 扫描目录、写 XMP；non-root 用 SAF/MediaStore |
| 云同步 / AI / LUT / 水印 / 整理 | ⬜ | 移动端暂不做（见 §6） |

**刻意舍弃**（移动端用不上/系统限制大）：视频转码与 LUT 代理、地图、时间线、合集筛选 UI（但交换文件仍保留合集往返）、
重复检测、文件整理落盘（root 可后续加）、批量导出、AI 批量队列、水印工作台。

## 2. 设计：Material 3 Expressive

- 结构：`Scaffold` + `LargeTopAppBar`（滚动折叠）+ 自适应 `LazyVerticalGrid` + `ModalBottomSheet`（筛选/详情/批量）。
- 品牌与取色：Android 12+ 用 **Material You 动态取色**（`dynamicDark/LightColorScheme`），关闭时回退 STJ 暖白/炭黑 + 琥珀强调色。
- 组件：`FilterChip`、`SegmentedButton`（三态选用/调色）、`FilledTonalButton`、`ExtendedFloatingActionButton`、`ListItem`、`Switch`。
- 形状：卡片 `shapes.extraLarge`、缩略图 24dp 顶部圆角、色标圆点；密度三档会改变卡片与缩略图目标像素。
- 字体：Material 默认字阶（中文走系统字体），不做自绘字体。
- 交互：长按卡片进入多选；底部选择条（全选/取消）；详情保存后回到网格并刷新分面。

## 3. 工程结构

```
android/
├── settings.gradle.kts / build.gradle.kts / gradle.properties
├── core/                              纯 Kotlin/JVM（无 Android 依赖，可单测）
│   └── src/main/kotlin/…/core/
│       ├── Model.kt                   素材/媒体库/合集模型（字段与 Windows catalog.db 同名）
│       ├── FormatCatalog.kt           扩展名 → 类型/格式/RAW
│       ├── Query.kt                   筛选语义（__unset__、三态 is_pick、多主题、分面）
│       ├── exchange/Exchange.kt       交换文件 v1（序列化/匹配/合并/设置映射）
│       ├── meta/Xmp.kt                XMP 边车读/写（xmp:Rating / dc:subject / photoshop:City / stj:Theme…）
│       ├── media/TiffPreview.kt       RAW 内嵌 JPEG 提取（IFD 快路径 + 流式扫描）
│       └── geo/OfflineGeocoder.kt     离线城市反查（1° 网格 + Haversine）
└── app/                               Android UI + 平台实现
    └── src/main/kotlin/…/
        ├── StjApp.kt                  组合根（DB / 设置 / 缩略图 / 扫描 / 交换）
        ├── MainActivity.kt            Compose 入口 + 权限/SAF 选择器
        ├── data/CatalogDb.kt          SQLite（assets/libraries/collections，字段与桌面版同名）
        ├── data/SettingsStore.kt      SharedPreferences（外观/排序/媒体库/root）
        ├── scan/MediaScanner.kt       三种扫描来源 + 分块入库 + 失效清理
        ├── scan/MetadataReader.kt     EXIF/视频/XMP/城市
        ├── scan/ThumbnailLoader.kt    缩略图（采样/RAW/视频/LRU）
        ├── scan/RootManager.kt        su 探测、root 列目录、root 写文件
        ├── exchange/ExchangeIo.kt     SAF ↔ JSON
        ├── vm/LibraryViewModel.kt     单一状态源
        └── ui/                        Theme / Components / LibraryScreen / Sheets / SettingsScreen
```

索引库位置：`/data/data/com.stjarna.mediaviewer/files/../databases/catalog.db`（应用私有）。
字段与 Windows 版 `catalog.db` 的 assets 子集同名同义，额外一列 `content_uri` 保存 SAF/MediaStore 地址。

## 4. 构建

环境：JDK 21、Android SDK 36（platform 36 + build-tools 36.0.0）、Gradle 8.13。

```bash
cd android
echo 'sdk.dir=/path/to/android/sdk' > local.properties

# 单元测试（core，26 项）
gradle :core:test

# Debug APK
gradle :app:assembleDebug

# 发布 APK（需要 keystore.properties + stj-release.jks，见下）
gradle :app:assembleRelease
```

产物：`app/build/outputs/apk/{debug,release}/app-*.apk`

发布签名（keystore 不入库，`.gitignore` 已排除）：

```bash
keytool -genkeypair -keystore stj-release.jks -alias stj -keyalg RSA -keysize 2048 \
  -validity 10000 -storepass <pass> -keypass <pass> -dname "CN=STJ Media Viewer"
cat > keystore.properties <<'EOF'
storeFile=stj-release.jks
storePassword=<pass>
keyAlias=stj
keyPassword=<pass>
EOF
```

## 云相册（0.2.0 新增，与 Windows / macOS 同一套语义）

入口：素材库右上角 **云图标**。一个页面包含：云端浏览 / 预览 / 导入本机 / 上传所选素材。

- **账号**：WebDAV 地址（如 `https://webdav.123pan.cn/webdav/`）+ 账号 + 密码（只存本机 SharedPreferences，不进交换文件、不进日志）。
- **浏览**：PROPFIND `Depth:1` 列一层，面包屑 + 上一级；中文/空格路径逐段编码；服务器 302 到 CDN 时手动重发 `Range`。
- **缩略图**（Range 按需）：
  - ≤12MB 整文件下载 → 采样解码；
  - JPEG 优先取 `ExifInterface` 内嵌缩略图（只读前 1MB）；
  - RAW 用核心模块 `TiffPreview` + `RemoteByteSource`（Range）取内嵌预览，不下载整张；
  - 视频暂用占位（不做首帧）；
  - 磁盘缓存 `cacheDir/cloud-album`，键含路径/大小/修改时间。
- **预览**：点击图片下载到缓存并弹出大图；其他格式提示不支持。
- **导入**：勾选 → 「下载」→ 存到 `Android/data/com.stjarna.mediaviewer/files/Pictures/STJ云相册/`，自动加入媒体库并扫描（MediaScanner 通知系统相册）。
- **上传**：在素材库长按选中素材 → 进云相册 → 「上传」→ 上传原文件 + 同名/同 stem 的 `.xmp` 边车到当前云端目录（`PUT`，失败自动带 `Expect: 100-continue` 重试，123pan 必需）。
- **同步**：Windows 侧的差异计划/双向同步不在 Android 上重复实现，移动端定位是「看 + 取 + 传」；交换文件仍可迁移编辑成果。

验收见 `docs/ANDROID-ACCEPTANCE.md` §云相册。

## 5. 权限与 root

| 权限 | 用途 |
| --- | --- |
| `READ_MEDIA_IMAGES` / `READ_MEDIA_VIDEO`（33+） | 系统相册扫描、缩略图/EXIF |
| `READ_EXTERNAL_STORAGE`（≤32） | 旧系统回退 |
| `MANAGE_EXTERNAL_STORAGE`（可选） | 扫描任意素材目录；国内分发可引导授权，Play 上架需改用纯 SAF |
| INTERNET | 预留（云同步后续） |

**root 模式**（设置页开关）：

- 检测：`su -c id` 输出 `uid=0`；
- 扫描：普通权限读不到的目录用 `su -c find + stat` 枚举（toybox 兼容）；
- 写 XMP：`su -c cp/chmod` 写到素材旁边；
- 不 root 也能用：SAF 选择文件夹 + 系统相册，全部功能除"root 专有目录"外都可用。

## 6. 已知限制 / 后续

- 无 root 且未授予"所有文件访问"时，只能通过 SAF/相册访问；直接填 `/sdcard/...` 路径可能读不到。
- RAW 缩略图依赖内嵌预览（绝大多数相机 RAW 都有）；没有预览的 RAW 显示占位。
- HEIC 依赖系统解码（Android 8+ 厂商实现，个别设备不支持 10-bit HEIC）。
- 视频仅首帧缩略图，不做播放/转码/LUT。
- 交换文件导入只更新匹配素材（文件名+大小），不新建素材；合集按名字重建。
- 后续优先级：① 整理（root 移动 + 分类目录）② 云同步（WebDAV）③ AI 分类（HTTP，复用提示词）④ 水印（Compose Canvas/Skia）。

## 7. 版本策略

- Android 从 **0.1.0** 起步，与 Windows 主版本（1.27.x）**同仓库、同 CHANGELOG**、共用交换契约；
- 每次 Windows 侧功能/修复发布时，同步检查 Android 是否受影响：受影响则升 Android 修订号并重建 APK；
- 交换文件 `formatVersion` 保持 1（新增字段只做兼容扩展，升 2 时三端同时发版）。

## 8. 验证

- core 单测 **26 项全过**：格式识别、设备名归一、筛选语义（未分类/多主题/三态）、分面计数、
  交换文件导出/校验/匹配/合并/设置映射、XMP 往返、RAW 内嵌预览（IFD + 流式回退）、离线城市反查与距离。
- `assembleDebug` / `assembleRelease` 均通过；release APK 已用 `apksigner` 校验签名。
- 真机/模拟器 UI 验收清单见 `docs/ANDROID-ACCEPTANCE.md`（待设备环境）。