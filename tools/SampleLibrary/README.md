# SampleLibrary — 测试素材库生成器

为 **stj-media-viewer** 提供"没有真实素材也能验收"的测试地基：一个可重复生成、内容确定、
带逐文件期望值清单（`catalog-manifest.json`）的媒体库。

- 生成器：`tools/SampleLibrary/generate.py`（Pillow + numpy；视频/音频优先 ffmpeg，缺失时纯 Python 兜底）
- 产物：`tools/SampleLibrary/out/library/`
- 当前规模：**85 个文件 / 约 9.7 MiB / 生成耗时约 6 秒**（远低于 60MB / 3 分钟上限；当前 ffmpeg 就绪，视频/音频为真媒体）
- 归属：`tools/SampleLibrary/` 由 tooling 维护；`out/` 是生成物，可随时删除重跑。

---

## 1. 快速开始

```powershell
# 任意 cwd 都可以；脚本用 __file__ 定位仓库根，不依赖绝对路径
python tools/SampleLibrary/generate.py

# 只重跑某一阶段（开发用）
python tools/SampleLibrary/generate.py --only images
python tools/SampleLibrary/generate.py --only raw
python tools/SampleLibrary/generate.py --only sidecars
python tools/SampleLibrary/generate.py --only dups
python tools/SampleLibrary/generate.py --only video
python tools/SampleLibrary/generate.py --only audio
python tools/SampleLibrary/generate.py --only junk
python tools/SampleLibrary/generate.py --only manifest   # 只重算 manifest（基于当前 MANIFEST）
```

- **幂等**：每次默认先删掉整个 `out/` 再重建。内容由「相对路径 + 固定种子」决定，重跑字节一致
  （唯一例外：`catalog-manifest.json` 里的 `generator.elapsedSeconds` 会变）。
- **失败降级**：单个文件出错只打印 `[WARN]` 并继续；最后打印统计（文件数/大小/耗时/警告）。
- 生成后自带一次 `[自检]`：比对磁盘文件与 manifest、Pillow 重开图片、校验 EXIF/GPS/内嵌 JPEG。

### Python / 依赖

```
C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe
```

需要 `Pillow`（已装 12.3）与 `numpy`；都是可选严格依赖（numpy 缺失会退化为纯 Pillow 渐变）。

### ffmpeg 探测顺序

1. `FfmpegPath` 环境变量 `FFMPEG_PATH`（可指向任意 `ffmpeg.exe`）
2. `tools/ffmpeg/bin/ffmpeg.exe`（团队约定位置）
3. `tools/ffmpeg/ff.zip`（若已是完整 zip，会自动解包 `ffmpeg.exe` / `ffprobe.exe` 到 `out/_tooling/`）
4. `PATH` 上的 `ffmpeg`

全都找不到时：视频/音频走纯 Python 兜底（见第 7 节），
`catalog-manifest.json` 的 `generator.ffmpegAvailable=false`、`generator.videoFallback=true`。

**没有 ffprobe 也能跑**：本机 `tools/ffmpeg/bin/` 只有 `ffmpeg.exe`（105MB，v9.0.2，无 ffprobe）。
生成器会自动检测：优先 `ffprobe -show_streams -of json`，缺失时退回解析
`ffmpeg -i <file>` 的 stderr（读 `Duration:` / `Stream #… Video:` / `Audio:` / `Metadata:`），
manifest 里的 `generator.probeMethod` 会记录用了哪种（`ffprobe` / `ffmpeg-stderr` / `none`）。

---

## 2. 目录结构

```
tools/SampleLibrary/
├── generate.py                 # 生成器（唯一需要维护的代码）
├── README.md                   # 本文件
└── out/                        # 生成物，每次运行清空重建
    ├── library/                # ★ 测试媒体库根（扫描器的输入）
    │   ├── 2023/上海/          # 2023 上海（Sony ARW + JPG）
    │   ├── 2023/成都/          # 2023 成都（Sony）
    │   ├── 2023/杭州/          # 2023 杭州（Canon，含 DNG/TIFF）
    │   ├── 2024/东京/          # 2024 东京（Sony/Fuji/iPhone/PnG）
    │   ├── 2024/冰岛/          # 2024 冰岛（Canon/DJI，国家=冰岛）
    │   ├── 2024/纽约/          # 2024 纽约（iPhone/Canon）
    │   ├── 2025/巴黎/          # 2025 巴黎（Canon/Nikon/iPhone/Fuji）
    │   ├── 2026/北京/          # 2026 北京（Fuji/Nikon/iPhone）
    │   ├── Audio/              # 4 个音频
    │   ├── Video/              # 6 个视频（含 HLG / S-Log3 / 竖屏）
    │   ├── _junk/              # 重复备份/近重复/杂项（会被扫描）
    │   ├── $RECYCLE.BIN/       # 必须被整目录排除
    │   ├── node_modules/       # 必须被整目录排除
    │   ├── catalog-manifest.json   # ★ 逐文件期望值清单
    │   └── 若干非媒体/坏文件/隐藏文件（见第 4 节）
    └── _tooling/               # ffmpeg 解包等临时目录（不是素材）
```

> `out/` 建议不要提交进仓库（生成物）；`generate.py` 重跑即可恢复。

---

## 3. 内容构成（实测 85 个文件）

| 类别 | 数量 | 说明 |
|---|---|---|
| JPEG | 32 | 6 种机身、8 个地点、2023–2026 跨年，EXIF 全字段 + GPS |
| PNG / TIFF | 2 / 1 | 常规图片格式分类 |
| RAW | 8 | ARW×4 / CR2×1 / NEF×1 / DNG×2，TIFF+内嵌 JPEG |
| 视频 | 6 | 2×H.264 MP4、2×HLG/BT.2020 MOV、1×S-Log3 MOV、1×竖屏 9:16 |
| 音频 | 4 | WAV / MP3(ID3v2) / FLAC / M4A |
| 边车 | 10 | 8×`.xmp`（5 JPEG + 3 RAW）+ 1×`.dop` + 1×`.cos` |
| 重复/近重复 | 5 | 3 组完全相同 + 2 组近重复 |
| 反例/非媒体 | 16 | 见第 4 节 |
| manifest | 1 | 自身也登记为 entry（`sizeBytes=null`） |

设备分布（`expectDevice` 目标值，`expectCameraMake/expectCameraModel` 是 EXIF 原值）：
`Sony A7 IV`(SONY/ILCE-7M4)、`Canon EOS R5`(Canon/Canon EOS R5)、`Nikon Z 8`(NIKON CORPORATION/NIKON Z 8)、
`Fujifilm X-T5`(FUJIFILM/X-T5)、`Apple iPhone 15 Pro`(Apple/iPhone 15 Pro)、`DJI FC3582`(DJI/FC3582)。

GPS 地点（真实坐标，误差 ≤0.001°）：上海 31.2304,121.4737 / 北京 39.9042,116.4074 /
成都 30.5728,104.0668 / 杭州 30.2741,120.1551 / 东京 35.6762,139.6503 / 巴黎 48.8566,2.3522 /
纽约 40.7128,-74.0060 / 冰岛 64.1466,-21.9426。EXIF 里写 `OffsetTimeOriginal`（如 `+09:00`）。

---

## 4. 反例文件：每个都在测什么

| 文件 | 期望 | 测什么 |
|---|---|---|
| `notes.txt` `settings.json` `cache.tmp` `import_log.csv` | 排除（`ignored-extension` / `unsupported-extension`） | 扩展名黑名单 + "扩展名不认识但不是媒体"不能入库、不能崩溃 |
| `Thumbs.db` `.DS_Store` `desktop.ini` | 排除（`ignored-filename`） | 系统缩略图/DS 垃圾文件名过滤（大小写不敏感） |
| `._DSC02001.JPG` | 排除（`appledouble`） | macOS AppleDouble：`._` 前缀，扩展名虽是 .JPG 但内容不是媒体，按文件名规则排除 |
| `.hidden_photo.jpg` | 排除（`hidden-file`） | 隐藏文件（`.` 开头）过滤，即使内容是真 JPEG |
| `broken.jpg` | **入库但解析失败**（`expectParseError=true`） | 扩展名是媒体但内容是文本：要留住 asset 并记 `LastError`，不能丢、不能崩 |
| `empty.jpg` | 排除（`empty-file`） | 0 字节文件：扫描器按 `ScanOptions.MinFileSize=1` 直接跳过、不产生 asset（0 字节不可能是有效媒体，入库只会污染列表） |
| `$RECYCLE.BIN/deleted_but_real.jpg` | 排除（`ignored-directory`） | 回收站目录整目录排除，即使里面有真图片 |
| `node_modules/package.json` `node_modules/preview.png` | 排除（`ignored-directory`） | 前端工程目录整目录排除，不进入遍历 |
| `_junk/random.tmp` `_junk/readme.txt` | 排除 | `_junk` 目录本身会被扫描，但其中非媒体文件仍按扩展名规则排除 |

> 隐藏/系统文件的判定请统一走 `MediaTypeCatalog.IsIgnoredFile/IsIgnoredDirectory`，不要各写一套。

---

## 5. 重复 / 近重复 / JPG-RAW 配对

| 组 | 基准 | 副本 | 用途 |
|---|---|---|---|
| `exact-1` | `2024/东京/DSC02001.JPG` | `_junk/dup_a/DSC02001(1).JPG` | 同内容不同文件名（含内嵌 XMP） |
| `exact-2` | `2025/巴黎/R5A0002.CR2` | `_junk/dup_b/巴黎RAW备份.CR2` | RAW 精确重复 |
| `exact-3` | `2023/上海/DSC01234.JPG` | `_junk/dup_c/DSC01234_copy.JPG` | 跨目录精确重复 |
| `near-1` | `2024/冰岛/R5A0100.JPG` | `_junk/near_dup/R5A0100_q55.JPG` | 同画面 quality 55 重编码（aHash 应近似） |
| `near-2` | `2024/纽约/IMG_5001.JPG` | `_junk/near_dup/IMG_5001_压缩版.JPG` | 同画面 quality 52 重编码 |

- 精确重复：`sha256` 完全相同，`expectDuplicateOf` + `expectExactDuplicate=true`。
- 近重复：`expectNearDuplicateOf` 指向基准；**不要**期望它们 FullHash 相同。
- **副本只复制主文件、不复制边车**：若基准的后期信息来自 `.xmp/.dop/.cos`（例如
  `_junk/dup_b/巴黎RAW备份.CR2` 的基准 `R5A0002.CR2` 有 `.xmp`），副本上这些信息物理不存在，
  因此该条目的 `expectRating` / `expectColorLabel` / `expectPostProcess` 置 `null`、`expectKeywords=[]`、
  `expectHasDevelopEdits=false`（内嵌 XMP 的副本不受影响，见 `exact-1`）。
- **JPG-RAW 配对**（5 对，`expectRawPairWith` 互相指向）：
  `DSC01235`(2023 上海)、`DSC02001`(2024 东京)、`R5A0002`(2025 巴黎)、`_DSC3000`(2026 北京)、`DJI_0001`(2024 冰岛)。
  同名 JPG/RAW 应被识别为「同一素材的两种格式」，用于 JPG+RAW 分类与去重策略。

---

## 6. manifest (`catalog-manifest.json`) 字段说明

顶层：

```jsonc
{
  "schemaVersion": 1,
  "generatedBy": "tools/SampleLibrary/generate.py",
  "generator": { "ffmpegAvailable": true, "ffmpegSource": "tools\\ffmpeg\\bin\\ffmpeg.exe",
                 "ffprobeAvailable": false, "probeMethod": "ffmpeg-stderr",
                 "videoFallback": false, "audioFallbackFiles": [], "warnings": 0 },
  "tolerances": { "gpsDegrees": 0.001, "durationSeconds": 0.35, "fNumber": 0.05,
                  "exposureTime": 0.0005, "widthHeight": 2 },
  "counts": { "files": 85, "expectedIncluded": 60, "expectedExcluded": 25,
              "byKind": {...}, "byFormat": {...}, "byDevice": {...}, "byThemeHint": {...} },
  "notes": [ ... ],
  "entries": [ { "path": "2024/东京/DSC02001.JPG", ... }, ... ]
}
```

每个 entry 的 `path` 相对 `out/library/`（POSIX 分隔符，便于跨平台比较）。
**`expect*` 是给 SelfCheck 的断言目标；`null` / 缺省 = 该项不作断言**，整体原则「宁少勿错」。

| 字段 | 类型 | 含义 / 断言建议 |
|---|---|---|
| `kind` | `"Image"/"Video"/"Audio"/"Sidecar"/"NonMedia"` | 对应 `MediaKind` 名称；后两种是过滤/挂接对象，不是独立 asset |
| `format` | `"JPEG"/"CR2"/"MP4"/...` | 对应 `MediaTypeCatalog` 的 FormatCode |
| `sizeBytes` / `sha256` | int / hex（manifest 自身为 null） | 精确重复组 hash 相同；可用于校验生成完整 |
| `expectIncluded` | bool | 扫描器是否应产出 `MediaAsset`。`false` 的配 `expectExcludedReason` |
| `expectExcludedReason` | string? | `ignored-directory` / `ignored-filename` / `ignored-extension` / `unsupported-extension` / `hidden-file` / `appledouble` / `empty-file` / `sidecar` / `broken-media` |
| `expectParseError` | bool | 入库但元数据解析应失败（写入 `LastError`），扫描不能中断 |
| `expectDevice` | string? | **期望归一化设备键（商用名）**；`DeviceKey` 未实现时改用下一行断言 |
| `expectCameraMake` / `expectCameraModel` | string? | EXIF 原值（一定可解析） |
| `expectLensModel` / `expectFNumber` / `expectExposureTime` / `expectIso` / `expectFocalLengthMm` | | EXIF 摄影参数 |
| `expectWidth` / `expectHeight` | int? | 显示尺寸。RAW 取内嵌预览/TIFF 主图（本样本一致）；视频竖屏取**显示方向** 180×320。SelfCheck 容差 **±2px** |
| `expectCapturedAt` | ISO8601 带偏移? | 由 `DateTimeOriginal + OffsetTimeOriginal` 拼出；解析器若忽略偏移，至少应匹配日期时间部分 |
| `expectGps` | `{lat, lon}`? | 容差见 `tolerances.gpsDegrees`（0.001°≈110m） |
| `expectCity` / `expectCountry` / `expectProvince` | string? | 离线反查期望。**冰岛只到国家级**（`expectCity="冰岛"`），避免地理库粒度差异 |
| `expectIsRaw` / `expectRawFormat` | bool / string? | RAW 判定与格式（ARW/CR2/NEF/DNG） |
| `expectIsLog` / `expectLogProfile` | bool? / string? | 仅 `Video/2024_巴黎_SLog3_Sony.mov` 为 `true` + `"S-Log3"`（从容器 `©cmt` 注释判定）；HLG 样本的 Log 口径待定，留 `null` |
| `expectColorPrimaries` / `expectTransferCharacteristics` | string? | HLG 视频为 `"bt2020"` / `"arib-std-b67"`；其余 `"bt709"` |
| `expectVideoCodec` / `expectHasAudio` / `expectDurationSeconds` / `expectFrameRate` | | 视频技术参数（ffmpeg 可用时由 `ffmpeg -i` 实测回填） |
| `expectAudioCodec` / `expectAudioChannels` / `expectAudioSampleRate` / `expectAudioTags` | | 音频参数/标签 |
| `expectPostProcess` | bool? | `true` = 有后期编辑痕迹（内嵌 XMP `crs:HasSettings` 或 `.xmp/.dop/.cos` 边车） |
| `expectHasDevelopEdits` / `expectDevelopEngine` | bool / `"Lightroom"/"CaptureOne"`? | 边车/内嵌 XMP 的 `crs:` 段 |
| `expectHasEmbeddedXmp` / `expectXmpPath` / `expectSidecars` | | 内嵌 XMP、主 XMP 边车路径、全部挂接边车 |
| `expectRating` / `expectColorLabel` / `expectTitle` / `expectCaption` / `expectKeywords` | | 来自 XMP（`xmp:Rating` / `xmp:Label` / `dc:title` / `dc:description` / `dc:subject`） |
| `expectProcessVersion` | string? | `crs:ProcessVersion`（Lightroom 15.4 / Capture One 15.0） |
| `expectHasEmbeddedPreview` / `expectEmbeddedPreviewWidth/Height` | | RAW 内嵌预览 |
| `expectRawPairWith` | path? | 同 stem 的 JPG/RAW 另一半 |
| `expectDuplicateGroup` / `expectDuplicateOf` / `expectExactDuplicate` / `expectNearDuplicateOf` | | 去重测试 |
| `expectThemeHint` / `expectToneHint` | string? | **advisory**：按画面/关键词给的词表 ID 提示（city/night/landscape…；lowkey/cinematic…），用于主题/影调规则测试，不作硬断言 |
| `probe` | object? | ffmpeg/兜底探测到的真实容器信息（调试用） |

边车条目额外有 `expectAttachedTo`（主素材 path）、`expectExcludedReason="sidecar"`。
`catalog-manifest.json` 自身也有一条 entry（`kind=NonMedia`，`sizeBytes/sha256=null`）。

### 推荐断言方式（伪代码）

```python
for e in manifest["entries"]:
    asset = scanned_by_path.get(e["path"])
    if not e["expectIncluded"]:
        assert asset is None, f"{e['path']} 应被排除（{e['expectExcludedReason']}）"
        continue
    assert asset is not None
    if e["expectParseError"]: assert asset.LastError
    assert asset.Kind.value == e["kind"] or asset.Kind.name == e["kind"]
    if e["expectCameraModel"]: assert asset.CameraModel == e["expectCameraModel"]
    if e["expectWidth"]: assert asset.Width == e["expectWidth"]
    if e["expectGps"]: assert abs(asset.Latitude - e["expectGps"]["lat"]) <= tol["gpsDegrees"]
    if e["expectPostProcess"] is not None: assert (asset.PostProcess == Processed) == e["expectPostProcess"]
    ...
```

---

## 7. 媒体文件内部结构（为什么"看起来真"）

### JPEG
- Pillow 按场景合成 800–1600px 画面，JPEG quality 70（单张 30–130KB）。
- EXIF 走 Pillow `Image.Exif()`：IFD0（Make/Model/Software/DateTime/Artist/Copyright）+
  Exif 子 IFD（`DateTimeOriginal`、`OffsetTimeOriginal`、`FNumber`、`ExposureTime`、`ISOSpeedRatings`、
  `FocalLength`、`LensModel`、`PixelXDimension`…）+ GPS IFD（`GPSLatitude/Ref`、`GPSLongitude/Ref`、
  `GPSAltitude`、`GPSTimeStamp`、`GPSDateStamp`）。
- 内嵌 XMP：生成后把 XMP packet 作为 **APP1** 插到 SOI 之后（`http://ns.adobe.com/xap/1.0/\x00` 前缀），
  含 `xmp:Rating`、`xmp:Label`、`dc:title/description/creator/subject`、
  `crs:HasSettings=True`、`crs:ProcessVersion`、`crs:CameraProfile`。

### RAW（8 个）
一个**合法 TIFF（little-endian）+ 完整内嵌 JPEG**，可直接被 Pillow / MetadataExtractor 打开：

- IFD0：`ImageWidth/ImageLength`、`BitsPerSample=8,8,8`、`Compression=8`（Adobe deflate）、
  `Photometric=RGB`、`StripOffsets/ByteCounts`（主图 RGB 数据 zlib 压缩）、Make/Model/Software/DateTime、
  `ExifIFD`(34665) / `GPS IFD`(34853) 指针。**主图尺寸 = 内嵌预览尺寸**（1200×800 / 1600×1067 等）。
- Exif 子 IFD：`DateTimeOriginal`、`OffsetTimeOriginal`、`ExposureTime`、`FNumber`、`ISOSpeedRatings`、
  `LensModel`、`PixelXDimension/PixelYDimension`、`FocalLength`。
- GPS IFD：纬度/经度/海拔/UTC 时间戳。
- **IFD1**：`Compression=6`（JPEG）、`JPEGInterchangeFormat=513`、`JPEGInterchangeFormatLength=514`
  正式登记文件尾部一段完整 `SOI…EOI` 的 JPEG 预览（提取器也可直接全文件扫描最大 JPEG，两条路径都通）。
- `.cr2` 额外写 CR2 签名（offset 8 起 `CR\x02\x00`）；`.dng` 额外写 `DNGVersion`(50706)、
  `DNGBackwardVersion`(50707)、`UniqueCameraModel`(50708)。

### 视频/音频

#### ffmpeg 可用时（当前环境，真媒体）
生成命令要点：

- 画面：`-f lavfi -i testsrc2=size=WxH:rate=FPS:duration=D`（运动测试图，便于抽帧验证）；
  `libx264 -preset veryfast -crf 30 -pix_fmt yuv420p`。MP4×2 / MOV×4，320×180，1–3s。
- 音频：`sine` 正弦波 1–2s；WAV(PCM) / MP3(libmp3lame + ID3v2.3 title/artist/album) / FLAC / M4A(AAC)。
- 元数据**同时写两份**，覆盖解析器两种读取路径：
  - 经典 QuickTime 文本原子：`©mak`（Sony/Canon/DJI/FUJIFILM/Apple）、`©mod`、`©xyz`（ISO6709 GPS）、`©cmt`（S-Log3）、`©day`；
  - QuickTime metadata keys：`com.apple.quicktime.make/model/location.ISO6709/creationdate/comment`
    （用 `-movflags +use_metadata_tags` 写入 `moov/meta/keys+ilst`）。
- 色彩：`-movflags +write_colr` + `-x264-params colorprim/transfer/colormatrix`；
  生成后**精确回写容器 `colr` 盒子**（等长字节补丁，`nclc`/`nclx` 都支持）：
  普通/S-Log3 = `(primaries=1, transfer=1, matrix=1)` BT.709；HLG 两个 MOV = **`(9, 18, 9)`**（BT.2020/HLG）。
  这样自研 ISOBMFF 解析器无论读 `colr` 还是读 H.264 VUI 都能拿到正确 transfer characteristics。
- Log：`Video/2024_巴黎_SLog3_Sony.mov` 写 `comment=S-Log3`（经典 `©cmt` + keys），
  manifest `expectIsLog=true` / `expectLogProfile="S-Log3"`；HLG 样本的 `expectIsLog` 仍为 null（口径待定）。
- 竖屏：直接编码 180×320（UTF-8 文件名为 `2026_北京_竖屏_iPhone.mov`），manifest 期望 180×320。
- 生成后用 `ffmpeg -i`（本机无 ffprobe）实测：`Duration:` → `expectDurationSeconds`、
  `Video: ... 320x180 ... 25 fps` → `expectWidth/Height/FrameRate`、`Audio:` → `expectHasAudio`、
  `Metadata:` → `make/model/location/comment` 回填;`probe` 字段保留原始探测信息。
- 生成器还会实际执行一次 `ffmpeg -ss 0.2 -i file -frames:v 1 -f null -` 抽帧自检，确保视频可解码
  （即 SelfCheck 的"视频抽帧 → 缩略图"链路可用）。

#### ffmpeg 缺失时（纯 Python 兜底，仍可用）
**仍保证容器结构可被 ISOBMFF 解析器读出**：

- `ftyp` + `moov` + `mdat`；`mvhd`/`tkhd`/`mdhd` 统一 timescale=1000、duration 一致（秒级可算）。
- `trak → mdia → minf → stbl → stsd` 里有一个 **`avc1`** 样本描述，附带 **`colr`（`nclx`）** 盒子：
  普通/S-Log3 = primaries 1 / transfer 1（BT.709）；HLG 两个 = primaries 9 / transfer 18（HLG/BT.2020）。
- `tkhd` 宽高为 16.16 定点数（`320<<16`、`180<<16`）。
- 竖屏 iPhone：coded 尺寸仍是 320×180，`tkhd` 矩阵为 **90° 旋转**（`[0,1,-1,0]`），显示方向应为 180×320。
- `udta` 写 `©mak`/`©mod`/`©xyz`/`©cmt`/`©day`，`mdhd` 可直接算时长。
- 音频兜底：WAV 真 PCM；MP3 = ID3v2.3 + 合法 MPEG1 Layer3 静音帧；FLAC = STREAMINFO + VORBIS_COMMENT；
  M4A = ftyp+moov(音轨)+mdat。
- 兜底视频**不能真正解码画面**（缩略图链路会失败）；`manifest.generator.videoFallback=true`，且视频的
  `expectDevice/expectCameraMake/expectCameraModel/expectCity/expectGps/expectCapturedAt` 置 `null`
  （宁少勿错）；`expectWidth/Height/Duration/colr` 仍给出。**`tools/ffmpeg/bin/ffmpeg.exe` 到位后重跑即切换为真视频**。

---

## 8. 已知限制与注意事项

1. **设备归一化**：`expectDevice` 是商用名目标值（如 `Sony A7 IV`）。若 `DeviceNormalizer` 尚未实现，
   SelfCheck 请用 `expectCameraMake/expectCameraModel` 断言。
2. **冰岛**只给国家级期望（`expectCity="冰岛"`），因为离线地理库粒度可能到国家/地区而非城市。
3. **GPS 容差**：EXIF 用度分秒存储，回读误差 < 0.001°；SelfCheck 请用 `tolerances.gpsDegrees`。
4. **偏移时间**：`expectCapturedAt` 带时区偏移；若解析器暂时忽略 `OffsetTimeOriginal`，至少断言日期时间本地值。
5. **近重复**重编码会丢失内嵌 XMP（与真实"导出/压缩后副本"一致），因此这两条的
   `expectPostProcess/expectHasEmbeddedXmp/expectKeywords` 为 false/空。
6. **重复组副本不复制边车**：`_junk` 里的副本没有 `.xmp`，从边车推导的
   `expectRating/expectColorLabel/expectPostProcess/expectKeywords/expectHasDevelopEdits/expectDevelopEngine`
   都会被清成 `null`/`false`/`[]`（符合直觉：边车只挂原文件；`expectExactDuplicate` 仍为 true）。
7. `out/` 是生成物，不要提交；需要时重跑 `generate.py`。生成耗时 < 10s，可放进 CI 的 setup。
8. 修改样本清单时**必须同步维护 manifest 期望**：所有 `expect*` 由生成逻辑实测/推导，不要手写猜测值。
9. **本机没有 ffprobe**：video/audio 的期望由 `ffmpeg -i` stderr 回填（`generator.probeMethod="ffmpeg-stderr"`），
   若以后装了 ffprobe 会自动优先使用；两种方式都拿不到时才退回规格值。
10. **幂等性已实测**：连续两次运行（第二次在 `C:\` 等不同 cwd 下），85 个文件里除
    `catalog-manifest.json` 的 `generator.elapsedSeconds` 外**全部字节一致**，文件数恒为 85。
11. 视频设备/城市期望基于容器里真实写入的 `©mak/©mod/©xyz` + `com.apple.quicktime.*` 实测回填；
    如果未来 ffmpeg 版本改了 muxer 行为，`[自检]` 会把不一致打出来（生成器不会静默造错期望）。

---

## 9. SelfCheck 如何使用 manifest（约定）

### 9.1 读取与文件数

- 路径：**`tools/SampleLibrary/out/library/catalog-manifest.json`**（顶层 `libraryRoot` 字段也指向这里）。
- 顶层 `entries` 是**库目录下全部 85 个文件的一一对应清单**（含反例/边车/坏文件/manifest 自身），
  不是"媒体 asset 清单"。`path` 为 POSIX 分隔符、相对 `out/library/`，断言时统一 `relative_to(lib)` 归一。
- 文件数校验：`len(entries) == counts.manifestEntries == counts.files == 磁盘实际文件数`。
  注意 `generate.py` 会先 `rmtree(out/)` 再重建（约 5s），SelfCheck 请在生成进程结束后再读；
  读不到/读到 0 条时先重试一次，不要当成空库。
- `counts.byKind` / `byFormat` / `byDevice` / `byThemeHint` 均已按 entries 汇总，可直接做分布断言。

### 9.2 哪些 `expect*` 会被断言

1. **排除规则**：`expectIncluded=false` → 扫描结果里不得出现该 path；`expectExcludedReason` 用于诊断
   （`appledouble` / `empty-file` / `ignored-directory` / `ignored-filename` / `ignored-extension` /
   `unsupported-extension` / `hidden-file` / `sidecar` / `broken-media`）。`expectIncluded=true` 且
   `expectParseError=true` → 必须有 asset 且 `LastError` 非空（如 `broken.jpg`：扩展名是媒体但内容是文本）。
   注意 **0 字节文件不算 broken**：`ScanOptions.MinFileSize=1` 会直接跳过、不产生 asset，对应
   `expectIncluded=false, expectExcludedReason="empty-file"`。
2. **身份**：`kind` / `format` / `sizeBytes` / `sha256`（精确重复组 sha256 必须相同）。
3. **图片元数据**：`expectCameraMake/Model`、`expectLensModel`、`expectFNumber`、`expectExposureTime`、
   `expectIso`、`expectFocalLengthMm`、`expectCapturedAt`、`expectGps`、`expectWidth/Height`、
   `expectCity/Country/Province`、`expectIsRaw`、`expectRawFormat`。
4. **后期/边车**：`expectPostProcess`、`expectHasDevelopEdits`、`expectDevelopEngine`、
   `expectHasEmbeddedXmp`、`expectXmpPath`、`expectSidecars`、`expectRating`、`expectColorLabel`、
   `expectTitle`、`expectCaption`、`expectKeywords`、`expectProcessVersion`。
5. **RAW**：`expectHasEmbeddedPreview` + `expectEmbeddedPreviewWidth/Height`。
6. **视频**：`expectWidth/Height`、`expectDurationSeconds`、`expectFrameRate`、`expectVideoCodec`、
   `expectHasAudio`、`expectColorPrimaries`、`expectTransferCharacteristics`、`expectIsLog`、`expectLogProfile`。
7. **音频**：`expectAudioCodec`、`expectAudioChannels`、`expectAudioSampleRate`、`expectDurationSeconds`、`expectAudioTags`。
8. **关系**：`expectRawPairWith`（同名 JPG/RAW 配对）、
   `expectDuplicateGroup` / `expectDuplicateOf` / `expectExactDuplicate` / `expectNearDuplicateOf`。
9. **advisory（建议只做软断言/告警，不硬失败）**：`expectThemeHint`、`expectToneHint`、
   `expectDevice`（`DeviceKey` 归一化完成前请改用 `expectCameraMake/expectCameraModel`）。

### 9.3 `null` 与 `false` 的区别（宁少勿错）

- `null` = **本项不作断言**：不适用、解析器能力尚未覆盖、或物理上不存在
  （例如重复副本没有边车 → `expectRating=null`、`expectPostProcess=null`）。
- `false` / `[]` = **明确断言"没有"**：例如无后期编辑（`expectHasDevelopEdits=false`）、无关键词（`expectKeywords=[]`）。
- 不要把 `null` 当 `false` 去断言；也不要对 `null` 字段做等值比较。

### 9.4 容差表（与 manifest `tolerances` 一致）

| 项 | 容差 | 原因 |
|---|---|---|
| `width` / `height` | **±2 px**（`tolerances.widthHeight`） | RAW 预览取整、视频 SAR/显示方向换算 |
| `durationSeconds` | **±0.35 s**（`tolerances.durationSeconds`） | 容器时长 vs 帧数取整（例：1.5s 实测 1.52s） |
| `gpsDegrees`（lat/lon） | **±0.001°**（≈110m） | EXIF 度分秒存储回读误差 |
| `expectFNumber` | ±0.05 | 有理数近似 |
| `expectExposureTime` | ±0.0005 s | 有理数近似 |
| `expectCapturedAt` | 先按时区偏移转 `DateTimeOffset` 再比；偏移被忽略时至少比本地日期时间 | EXIF `OffsetTimeOriginal` 解析差异 |

### 9.5 为什么 `catalog-manifest.json` 不计入媒体

- `.json` 命中 `MediaTypeCatalog.IgnoredExtensions`，扫描器按扩展名排除 → **不产生 `MediaAsset`**。
- 但它确实是 `out/library/` 里的 1 个文件。为了"文件数与 manifest 一一对应"，它也被登记成一条
  `{kind:"NonMedia", expectIncluded:false, expectExcludedReason:"ignored-extension", sizeBytes:null, sha256:null}`。
- 因此：`counts.files` = `counts.manifestEntries` = 磁盘文件数（**含** manifest 自身）；
  `counts.expectedIncluded` 与 `byKind` 里的媒体数**不含**它；`counts.totalBytesExcludingManifest` 也**不含**它
  （自引用无法给出自身大小/哈希）。SelfCheck 校验"文件齐全"用前者，校验"媒体 asset 数"用后者。

### 9.6 建议的最小断言循环

```python
tol = manifest["tolerances"]
disk = {p.relative_to(lib).as_posix() for p in lib.rglob("*") if p.is_file()}
assert len(disk) == manifest["counts"]["files"] == len(manifest["entries"])
for e in manifest["entries"]:
    asset = assets_by_rel.get(e["path"])
    if not e["expectIncluded"]:
        assert asset is None, (e["path"], e["expectExcludedReason"])
        continue
    assert asset is not None, e["path"]
    if e["expectParseError"]:
        assert asset.LastError
    assert asset.Kind.name == e["kind"]
    if e["expectWidth"] is not None:
        assert abs(asset.Width - e["expectWidth"]) <= tol["widthHeight"]
    if e["expectHeight"] is not None:
        assert abs(asset.Height - e["expectHeight"]) <= tol["widthHeight"]
    if e["expectDurationSeconds"] is not None:
        assert abs(asset.DurationSeconds - e["expectDurationSeconds"]) <= tol["durationSeconds"]
    if e["expectGps"]:
        assert abs(asset.Latitude - e["expectGps"]["lat"]) <= tol["gpsDegrees"]
        assert abs(asset.Longitude - e["expectGps"]["lon"]) <= tol["gpsDegrees"]
    if e["expectPostProcess"] is not None:
        assert (asset.PostProcess == PostProcessState.Processed) == e["expectPostProcess"]
    # 其余 expectCameraModel/expectCity/expectIsRaw/expectIsLog/关键字…按"非 null 才断言"处理
```