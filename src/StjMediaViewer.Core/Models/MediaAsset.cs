namespace StjMediaViewer.Core.Models;

/// <summary>
/// 一条素材记录。这是整个系统的中心模型：扫描器产出它、元数据引擎填充它、
/// 分类引擎改写它、索引库存取它、UI 展示它。
/// 所有字段都可为空/默认，表示"尚未解析"。
/// </summary>
public sealed class MediaAsset
{
    // ---- 身份 / 文件 ----
    public string Id { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? RelativePath { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset FileModified { get; set; }
    public DateTimeOffset? FileCreated { get; set; }

    // ---- 大类 / 格式 ----
    public MediaKind Kind { get; set; } = MediaKind.Unknown;
    public string FormatCode { get; set; } = "";      // JPEG / HEIC / CR3 / MP4 / MOV / WAV / MP3 ...
    public string? MimeType { get; set; }
    public string? ContainerName { get; set; }        // ISOBMFF / Matroska / RIFF / TIFF ...
    public ImageFlavor ImageFlavor { get; set; } = ImageFlavor.None;
    public RawFormat RawFormat { get; set; } = RawFormat.None;
    public bool IsRaw => RawFormat != RawFormat.None;

    // ---- 技术参数 ----
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? FrameRate { get; set; }
    public long? BitrateBps { get; set; }
    public int? BitDepth { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public bool HasAudio { get; set; }

    // ---- 色彩 / Log ----
    public string? ColorPrimaries { get; set; }             // BT.709 / BT.2020 / P3-D65 ...
    public string? TransferCharacteristics { get; set; }    // BT.709 / PQ / HLG / S-Log3 / LogC ...
    public string? ColorMatrix { get; set; }
    public string? ColorProfileName { get; set; }           // sRGB / AdobeRGB / S-Gamut3.Cine ...
    public bool? IsLog { get; set; }                        // 是否 Log 素材
    public string? LogProfile { get; set; }                 // S-Log3 / V-Log / C-Log3 / Apple Log / D-Log ...
    public double? ColorTemperatureK { get; set; }

    // ---- 拍摄设备 ----
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? DeviceKey { get; set; }
    /// <summary>该素材选用的 LUT id（空 = 按相机/Log 自动匹配内置基准 LUT）。</summary>
    public string? LutId { get; set; }                  // 归一化设备键，如 "Sony ILCE-7M4"
    public string? LensModel { get; set; }
    public double? FocalLengthMm { get; set; }
    public double? ApertureF { get; set; }
    public double? ShutterSpeedSeconds { get; set; }
    public int? Iso { get; set; }

    // ---- 时间 / 地点 ----
    public DateTimeOffset? CapturedAt { get; set; }
    public string? CapturedAtSource { get; set; }           // exif:DateTimeOriginal / quicktime / file
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? AltitudeMeters { get; set; }
    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? CitySource { get; set; }                 // offline / online / manual

    // ---- 文本元数据 ----
    public string? Title { get; set; }
    public string? Caption { get; set; }
    public string? Creator { get; set; }
    public string? Copyright { get; set; }
    public List<string> Keywords { get; set; } = new();

    // ---- 后期状态 / 边车 / 分组（同一张照片的 JPG 与 RAW 归为一组）----
    public PostProcessState PostProcess { get; set; } = PostProcessState.Unknown;
    public bool HasDevelopEdits { get; set; }
    public string? DevelopEngine { get; set; }              // Lightroom / CaptureOne / ON1 / Darktable
    public string? XmpPath { get; set; }
    public List<string> Sidecars { get; set; } = new();
    public bool HasEmbeddedXmp { get; set; }

    /// <summary>同组共享的分组键（同一张照片的 JPG / RAW / 边车同属一组）。</summary>
    public string? GroupId { get; set; }
    /// <summary>
    /// 是否是这一组在网格里的代表（优先 JPG，没有 JPG 时用 RAW）。
    /// 默认 true：不属于任何 JPG/RAW 组的素材（视频 / 音频 / 单张图）本身就是一格，
    /// 折叠（CollapseGroups）查询按 `is_group_cover = 1` 过滤，默认 false 会让它们整类消失。
    /// </summary>
    public bool IsGroupCover { get; set; } = true;
    /// <summary>组内是否含 RAW。</summary>
    public bool GroupHasRaw { get; set; }
    /// <summary>组内是否含 JPEG。</summary>
    public bool GroupHasJpeg { get; set; }
    /// <summary>组内其它文件（RAW ↔ JPG）。</summary>
    public List<string> CompanionPaths { get; set; } = new();

    /// <summary>是否是一组 RAW+JPEG 配对（界面上显示 RAW&amp;JPEG 标签）。</summary>
    public bool IsRawJpegPair => GroupHasRaw && GroupHasJpeg;

    // ---- 分类结果 ----
    public string? Theme { get; set; }
    public string? Tone { get; set; }
    public int Rating { get; set; }
    public string? ColorLabel { get; set; }                 // None / Red / Yellow / Green / Blue / Purple
    public bool? IsPick { get; set; }                       // true=选用 false=废片 null=未定
    public string? AiSubject { get; set; }                  // 一句话内容描述
    public string? AiMood { get; set; }
    public double? QualityScore { get; set; }               // 0..1
    public Dictionary<ClassDimension, FieldProvenance> Provenance { get; set; } = new();

    // ---- 指纹 / 缓存 ----
    public string? QuickHash { get; set; }                  // 快速重复检测指纹
    public string? FullHash { get; set; }
    public string? ThumbKey { get; set; }                   // 缩略图缓存键
    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? MetadataParsedAt { get; set; }
    public string? LastError { get; set; }

    public bool IsVideo => Kind == MediaKind.Video;
    public bool IsImage => Kind == MediaKind.Image;
    public bool IsAudio => Kind == MediaKind.Audio;

    public double? AspectRatio =>
        Width is > 0 && Height is > 0 ? (double)Width.Value / Height.Value : null;

    public string DisplayDevice =>
        !string.IsNullOrWhiteSpace(DeviceKey) ? DeviceKey! :
        (!string.IsNullOrWhiteSpace(CameraModel) ? CameraModel! : "未知设备");

    public FieldProvenance? Prov(ClassDimension d) => Provenance.TryGetValue(d, out var p) ? p : null;

    public void SetProv(ClassDimension d, FieldProvenance p) => Provenance[d] = p;

    public MediaAsset Clone() => new()
    {
        Id = Id,
        LibraryId = LibraryId,
        FullPath = FullPath,
        FileName = FileName,
        RelativePath = RelativePath,
        SizeBytes = SizeBytes,
        FileModified = FileModified,
        FileCreated = FileCreated,
        Kind = Kind,
        FormatCode = FormatCode,
        MimeType = MimeType,
        ContainerName = ContainerName,
        ImageFlavor = ImageFlavor,
        RawFormat = RawFormat,
        Width = Width,
        Height = Height,
        DurationSeconds = DurationSeconds,
        FrameRate = FrameRate,
        BitrateBps = BitrateBps,
        BitDepth = BitDepth,
        VideoCodec = VideoCodec,
        AudioCodec = AudioCodec,
        AudioChannels = AudioChannels,
        AudioSampleRate = AudioSampleRate,
        HasAudio = HasAudio,
        ColorPrimaries = ColorPrimaries,
        TransferCharacteristics = TransferCharacteristics,
        ColorMatrix = ColorMatrix,
        ColorProfileName = ColorProfileName,
        IsLog = IsLog,
        LogProfile = LogProfile,
        ColorTemperatureK = ColorTemperatureK,
        CameraMake = CameraMake,
        CameraModel = CameraModel,
        DeviceKey = DeviceKey,
        LutId = LutId,
        LensModel = LensModel,
        FocalLengthMm = FocalLengthMm,
        ApertureF = ApertureF,
        ShutterSpeedSeconds = ShutterSpeedSeconds,
        Iso = Iso,
        CapturedAt = CapturedAt,
        CapturedAtSource = CapturedAtSource,
        Latitude = Latitude,
        Longitude = Longitude,
        AltitudeMeters = AltitudeMeters,
        Country = Country,
        Province = Province,
        City = City,
        CitySource = CitySource,
        Title = Title,
        Caption = Caption,
        Creator = Creator,
        Copyright = Copyright,
        Keywords = new List<string>(Keywords),
        PostProcess = PostProcess,
        HasDevelopEdits = HasDevelopEdits,
        DevelopEngine = DevelopEngine,
        XmpPath = XmpPath,
        Sidecars = new List<string>(Sidecars),
        HasEmbeddedXmp = HasEmbeddedXmp,
        GroupId = GroupId,
        IsGroupCover = IsGroupCover,
        GroupHasRaw = GroupHasRaw,
        GroupHasJpeg = GroupHasJpeg,
        CompanionPaths = new List<string>(CompanionPaths),
        Theme = Theme,
        Tone = Tone,
        Rating = Rating,
        ColorLabel = ColorLabel,
        IsPick = IsPick,
        AiSubject = AiSubject,
        AiMood = AiMood,
        QualityScore = QualityScore,
        Provenance = Provenance.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        QuickHash = QuickHash,
        FullHash = FullHash,
        ThumbKey = ThumbKey,
        IndexedAt = IndexedAt,
        MetadataParsedAt = MetadataParsedAt,
        LastError = LastError,
    };
}