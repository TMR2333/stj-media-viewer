using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Metadata;

/// <summary>
/// 元数据引擎总调度：识别类型（扩展名 + 魔数纠正）→ 分派给图片/视频/音频解析器 →
/// RAW 预览探测 → 边车/配对发现与 XMP 合并 → 设备/镜头归一化 → Log/色彩判定。
///
/// 线程安全：类本身无任何实例状态，所有解析器都是纯静态实现；同一个实例可被
/// <c>Parallel.ForEach</c> 并发调用。构造无参：<c>new MetadataService()</c>。
///
/// 永不抛异常：任何失败都降级为 <see cref="MediaMetadata.Warnings"/> / <see cref="MediaMetadata.Error"/>，
/// 并记 <see cref="Log.Warn"/>，调用方拿到的是尽可能完整的部分结果。
/// </summary>
public sealed class MetadataService : IMetadataService
{
    // ---------------------------------------------------------------- 公开 API

    public MediaMetadata Read(string fullPath, CancellationToken ct = default)
    {
        var metadata = new MediaMetadata
        {
            SourcePath = fullPath ?? "",
            ParsedAt = DateTimeOffset.Now,
        };

        try
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                metadata.Error = "路径为空";
                return metadata;
            }

            if (!File.Exists(fullPath))
            {
                metadata.Error = "文件不存在";
                metadata.Warnings.Add("文件不存在: " + fullPath);
                return metadata;
            }

            FileInfo fileInfo;
            try { fileInfo = new FileInfo(fullPath); }
            catch (Exception ex)
            {
                metadata.Error = "无法读取文件信息: " + ex.Message;
                return metadata;
            }

            if (fileInfo.Length == 0)
            {
                // 空文件：仍然按扩展名给出类型，方便扫描器/UI 归类，同时标记解析错误
                MediaTypeCatalog.TryGet(fullPath, out var emptyKind, out var emptyFormat, out _, out _, out var emptyMime);
                metadata.Kind = emptyKind;
                metadata.FormatCode = emptyFormat;
                metadata.MimeType = emptyMime;
                metadata.Error = "文件大小为 0";
                metadata.Warnings.Add("空文件，无可解析内容");
                return metadata;
            }

            var head = ReadHead(fullPath, 64, out var headLength);
            var headSpan = head.AsSpan(0, headLength);

            // ---------- 1) 类型识别（扩展名 + 魔数纠正） ----------
            IdentifyKind(fullPath, headSpan, metadata);

            if (metadata.Kind == MediaKind.Unknown)
            {
                metadata.Warnings.Add("无法识别的媒体类型，已跳过深度解析");
                return metadata;
            }

            // ---------- 2) 按类型分派 ----------
            IsoBmffInfo? iso = null;
            MatroskaInfo? matroska = null;
            var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            switch (metadata.Kind)
            {
                case MediaKind.Image:
                    iso = ReadImage(fullPath, metadata, headSpan, hints, ct);
                    break;
                case MediaKind.Video:
                    (iso, matroska) = ReadVideo(fullPath, metadata, headSpan, hints, ct);
                    break;
                case MediaKind.Audio:
                    iso = ReadAudio(fullPath, metadata, headSpan, ct);
                    break;
            }

            // ---------- 3) 边车 / 配对 ----------
            var sidecars = SidecarScanner.Discover(fullPath, metadata.RawFormat, metadata.ImageFlavor, metadata.Kind);
            foreach (var warning in sidecars.Warnings) metadata.Warnings.Add(warning);
            foreach (var sidecar in sidecars.Sidecars)
                if (!metadata.Sidecars.Contains(sidecar, StringComparer.OrdinalIgnoreCase))
                    metadata.Sidecars.Add(sidecar);
            metadata.XmpPath ??= sidecars.PrimarySidecarPath;
            XmpApplier.ApplySidecar(sidecars.SidecarXmp, metadata);
            if (sidecars.HasDevelopEdits) metadata.HasDevelopEdits = true;
            if (sidecars.DevelopEngine is not null) metadata.DevelopEngine = sidecars.DevelopEngine;

            // ---------- 4) 设备 / 镜头归一化 ----------
            NormalizeDevice(metadata, fullPath);

            // ---------- 5) 色彩 / Log 判定 ----------
            ApplyColorDetection(fullPath, metadata, iso, matroska, sidecars.SidecarXmp, hints);

            // ---------- 6) 后期状态 ----------
            if (metadata.HasDevelopEdits || metadata.DevelopEngine is not null)
                metadata.PostProcess = PostProcessState.Processed;
            else if (sidecars.ExplicitlyUnprocessed)
                metadata.PostProcess = PostProcessState.Unprocessed;
            else if (metadata.Kind == MediaKind.Image)
                metadata.PostProcess = PostProcessState.Unprocessed;   // 图片无任何后期痕迹 → 未处理

            // ---------- 7) 拍摄时间兜底：文件修改时间 ----------
            if (metadata.CapturedAt is null)
                ApplyFileTime(fullPath, metadata);

            // 解析完成后的统一错误标记（例如异常已被内部吞掉）
            if (metadata.HasError) Log.Warn($"[MetadataService] {Path.GetFileName(fullPath)}: {metadata.Error}");
            return metadata;
        }
        catch (OperationCanceledException)
        {
            metadata.Warnings.Add("解析被取消，返回部分结果");
            return metadata;
        }
        catch (Exception ex)
        {
            metadata.Error = "元数据解析失败: " + ex.Message;
            metadata.Warnings.Add(metadata.Error);
            Log.Warn($"[MetadataService] 解析失败: {fullPath}", ex);
            return metadata;
        }
    }

    public MediaKind Identify(string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return MediaKind.Unknown;
            var found = MediaTypeCatalog.TryGet(fullPath, out var kind, out _, out _, out _, out _);
            var head = ReadHead(fullPath, 64, out var length);
            if (length >= 4 && InferFromMagic(head.AsSpan(0, length), out var magicKind, out _, out _, out _, out _, out _))
            {
                return magicKind;
            }
            return found ? kind : MediaKind.Unknown;
        }
        catch
        {
            return MediaKind.Unknown;
        }
    }

    // ============================================================ 类型识别

    private static void IdentifyKind(string fullPath, ReadOnlySpan<byte> head, MediaMetadata metadata)
    {
        var knownExtension = MediaTypeCatalog.TryGet(fullPath, out var catalogKind, out var catalogFormat,
            out var catalogFlavor, out var catalogRaw, out var catalogMime);

        var magicKnown = InferFromMagic(head, out var magicKind, out var magicFormat, out var magicFlavor,
            out var magicRaw, out var magicMime, out var magicContainer);

        metadata.FormatCode = knownExtension ? catalogFormat : Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();
        metadata.Kind = knownExtension ? catalogKind : MediaKind.Unknown;
        metadata.ImageFlavor = knownExtension ? catalogFlavor : ImageFlavor.None;
        metadata.RawFormat = knownExtension ? catalogRaw : RawFormat.None;
        metadata.MimeType = knownExtension ? catalogMime : null;
        metadata.ContainerName = magicContainer;

        if (!magicKnown)
        {
            if (knownExtension)
            {
                // 扩展名认识、魔数不认识：可能是少见 RAW/PSD/JXL/MXF，也可能文件损坏；
                // 对"有严格文件头"的格式给出明确降级提示并标记错误，其余只提示。
                if (HasStrictMagic(metadata.FormatCode))
                {
                    metadata.Error = "文件内容与扩展名不符（魔数校验失败），可能已损坏";
                    metadata.Warnings.Add(metadata.Error);
                }
                else
                {
                    metadata.Warnings.Add("魔数未识别，将按扩展名尝试解析");
                }
            }
            return;
        }

        if (!knownExtension)
        {
            metadata.Kind = magicKind;
            metadata.FormatCode = magicFormat;
            metadata.ImageFlavor = magicFlavor;
            metadata.RawFormat = magicRaw;
            metadata.MimeType = magicMime;
            metadata.Warnings.Add($"未知扩展名，按魔数识别为 {magicFormat}");
            return;
        }

        if (magicKind == catalogKind)
        {
            // 类型一致：保留扩展名给出的格式（用户/相机命名更符合直觉），只补空的 MIME
            metadata.MimeType ??= magicMime;
            return;
        }

        // M4A/AAC 常以 "isom" 作为 major brand（魔数会判成 MP4 视频），实际 track 只有音频。
        // 扩展名已明确是音频时保留 Audio，交给 ISOBMFF 解析器按 track 定夺。
        if (catalogKind == MediaKind.Audio && magicKind == MediaKind.Video && magicContainer == "ISOBMFF")
        {
            metadata.Kind = MediaKind.Audio;
            metadata.FormatCode = catalogFormat;
            metadata.ImageFlavor = ImageFlavor.None;
            metadata.RawFormat = RawFormat.None;
            metadata.MimeType = catalogMime;
            return;
        }

        // 类型冲突（例如 .jpg 里其实是 MP4）：以魔数为准
        metadata.Warnings.Add($"扩展名与文件内容不一致：扩展名判定为 {catalogKind}/{catalogFormat}，" +
                              $"魔数判定为 {magicKind}/{magicFormat}，已按魔数纠正");
        metadata.Kind = magicKind;
        metadata.FormatCode = magicFormat;
        metadata.ImageFlavor = magicFlavor;
        metadata.RawFormat = magicRaw;
        metadata.MimeType = magicMime;
    }

    private static bool HasStrictMagic(string format) => format switch
    {
        "JPEG" or "PNG" or "TIFF" or "GIF" or "BMP" or "WAV" or "FLAC" or "MP3" or "OGG" or "AIFF"
            or "MP4" or "M4V" or "MOV" or "M4A" or "HEIC" or "HEIF" or "AVIF" or "CR3" or "WEBP" or "PSD" => true,
        _ => false,
    };

    /// <summary>按魔数推断类型/格式（不依赖扩展名）。</summary>
    private static bool InferFromMagic(ReadOnlySpan<byte> h, out MediaKind kind, out string format,
        out ImageFlavor flavor, out RawFormat raw, out string mime, out string? container)
    {
        kind = MediaKind.Unknown;
        format = "";
        flavor = ImageFlavor.None;
        raw = RawFormat.None;
        mime = "application/octet-stream";
        container = null;
        if (h.Length < 4) return false;

        // ---- 图片 ----
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        {
            kind = MediaKind.Image; format = "JPEG"; flavor = ImageFlavor.Jpeg; mime = "image/jpeg"; container = "JPEG";
            return true;
        }
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == (byte)'P' && h[2] == (byte)'N' && h[3] == (byte)'G')
        {
            kind = MediaKind.Image; format = "PNG"; flavor = ImageFlavor.Png; mime = "image/png"; container = "PNG";
            return true;
        }
        if (h.Length >= 4 &&
            ((h[0] == (byte)'I' && h[1] == (byte)'I' && h[2] == 42 && h[3] == 0) ||
             (h[0] == (byte)'M' && h[1] == (byte)'M' && h[2] == 0 && h[3] == 42)))
        {
            kind = MediaKind.Image; format = "TIFF"; flavor = ImageFlavor.Tiff; mime = "image/tiff"; container = "TIFF";
            if (h.Length >= 10 && h[8] == (byte)'C' && h[9] == (byte)'R')
            {
                format = "CR2"; raw = RawFormat.Cr2; mime = "image/x-canon-cr2";
            }
            return true;
        }
        if (h.Length >= 2 && h[0] == (byte)'B' && h[1] == (byte)'M')
        {
            kind = MediaKind.Image; format = "BMP"; flavor = ImageFlavor.Bmp; mime = "image/bmp"; container = "BMP";
            return true;
        }
        if (h.Length >= 4 && h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'8')
        {
            kind = MediaKind.Image; format = "GIF"; flavor = ImageFlavor.Gif; mime = "image/gif"; container = "GIF";
            return true;
        }
        if (h.Length >= 4 && h[0] == (byte)'8' && h[1] == (byte)'B' && h[2] == (byte)'P' && h[3] == (byte)'S')
        {
            kind = MediaKind.Image; format = "PSD"; flavor = ImageFlavor.Psd; mime = "image/vnd.adobe.photoshop"; container = "Photoshop";
            return true;
        }

        // ---- RIFF 家族 ----
        if (h.Length >= 12 && h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F')
        {
            var form = MetaUtil.FourCC(h, 8);
            if (form == "WAVE") { kind = MediaKind.Audio; format = "WAV"; mime = "audio/wav"; container = "RIFF"; return true; }
            if (form == "AVI ") { kind = MediaKind.Video; format = "AVI"; mime = "video/x-msvideo"; container = "RIFF"; return true; }
            if (form == "WEBP") { kind = MediaKind.Image; format = "WEBP"; flavor = ImageFlavor.WebP; mime = "image/webp"; container = "RIFF"; return true; }
        }

        // ---- ISOBMFF ----
        if (h.Length >= 12 && MetaUtil.FourCCEquals(h, 4, "ftyp"))
        {
            kind = MediaKind.Video;
            format = "MP4";
            mime = "video/mp4";
            container = "ISOBMFF";
            var brand = MetaUtil.FourCC(h, 8);
            switch (brand)
            {
                case "crx ":
                    kind = MediaKind.Image; format = "CR3"; raw = RawFormat.Cr3; mime = "image/x-canon-cr3";
                    break;
                case "avif":
                case "avis":
                    kind = MediaKind.Image; format = "AVIF"; flavor = ImageFlavor.Heif; mime = "image/avif";
                    break;
                case "heic":
                case "heix":
                case "hevc":
                case "hevx":
                case "heim":
                case "heis":
                    kind = MediaKind.Image; format = "HEIC"; flavor = ImageFlavor.Heif; mime = "image/heic";
                    break;
                case "mif1":
                case "msf1":
                    kind = MediaKind.Image; format = "HEIF"; flavor = ImageFlavor.Heif; mime = "image/heif";
                    break;
                case "M4A ":
                case "M4B ":
                    kind = MediaKind.Audio; format = "M4A"; mime = "audio/mp4";
                    break;
                case "M4V ":
                case "M4VH":
                case "M4VP":
                    kind = MediaKind.Video; format = "M4V"; mime = "video/x-m4v";
                    break;
                case "qt  ":
                    kind = MediaKind.Video; format = "MOV"; mime = "video/quicktime";
                    break;
                default:
                    kind = MediaKind.Video; format = "MP4"; mime = "video/mp4";
                    break;
            }
            return true;
        }

        // ---- EBML（Matroska / WebM）----
        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3)
        {
            kind = MediaKind.Video;
            format = ContainsAscii(h, "webm") ? "WEBM" : "MKV";
            mime = format == "WEBM" ? "video/webm" : "video/x-matroska";
            container = "Matroska";
            return true;
        }

        // ---- 音频 ----
        if (h.Length >= 4 && h[0] == (byte)'f' && h[1] == (byte)'L' && h[2] == (byte)'a' && h[3] == (byte)'C')
        {
            kind = MediaKind.Audio; format = "FLAC"; mime = "audio/flac"; container = "FLAC";
            return true;
        }
        if (h.Length >= 4 && h[0] == (byte)'O' && h[1] == (byte)'g' && h[2] == (byte)'g' && h[3] == (byte)'S')
        {
            kind = MediaKind.Audio; format = "OGG"; mime = "audio/ogg"; container = "Ogg";
            return true;
        }
        if (h.Length >= 3 && h[0] == (byte)'I' && h[1] == (byte)'D' && h[2] == (byte)'3')
        {
            kind = MediaKind.Audio; format = "MP3"; mime = "audio/mpeg"; container = "MPEG-Audio";
            return true;
        }
        if (h.Length >= 4 && h[0] == (byte)'F' && h[1] == (byte)'O' && h[2] == (byte)'R' && h[3] == (byte)'M')
        {
            var form = h.Length >= 12 ? MetaUtil.FourCC(h, 8) : "";
            kind = MediaKind.Audio; format = form == "AIFC" ? "AIFF" : "AIFF";
            mime = "audio/aiff"; container = "AIFF";
            return true;
        }
        return false;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> h, string token)
    {
        Span<byte> needle = stackalloc byte[8];
        if (token.Length > needle.Length) return false;
        for (var i = 0; i < token.Length; i++) needle[i] = (byte)token[i];
        return h.IndexOf(needle[..token.Length]) >= 0;
    }

    // ============================================================ 图片

    private static IsoBmffInfo? ReadImage(string fullPath, MediaMetadata m, ReadOnlySpan<byte> head,
        HashSet<string> hints, CancellationToken ct)
    {
        IsoBmffInfo? iso = null;
        var isFtyp = head.Length >= 12 && MetaUtil.FourCCEquals(head, 4, "ftyp");
        var brand = isFtyp ? MetaUtil.FourCC(head, 8) : "";

        try
        {
            if (m.RawFormat == RawFormat.Cr3 || brand == "crx ")
            {
                iso = IsoBmffParser.Parse(fullPath, ct);
                ApplyIsoBmff(iso, m);
                m.ContainerName = "ISOBMFF";
                CollectIsoHints(iso, m, hints);
                if (iso.XmpBytes is { Length: > 0 })
                {
                    var xmp = XmpReader.Parse(iso.XmpBytes);
                    XmpApplier.ApplyEmbedded(xmp, m);
                }
                // CR3 的 EXIF 已在 CMT TIFF 里；MetadataExtractor 作为补充（失败会内部降级）
                ImageMetadataReader.Read(fullPath, m, ct);
            }
            else if (m.ImageFlavor == ImageFlavor.Heif || brand is "heic" or "heix" or "hevc" or "hevx" or "avif" or "avis" or "mif1" or "msf1")
            {
                iso = IsoBmffParser.Parse(fullPath, ct);
                ApplyIsoBmff(iso, m);
                m.ContainerName = "ISOBMFF";
                CollectIsoHints(iso, m, hints);
                if (iso.XmpBytes is { Length: > 0 })
                {
                    var xmp = XmpReader.Parse(iso.XmpBytes);
                    XmpApplier.ApplyEmbedded(xmp, m);
                }
                ImageMetadataReader.Read(fullPath, m, ct);
            }
            else
            {
                m.ContainerName ??= m.ImageFlavor switch
                {
                    ImageFlavor.Jpeg => "JPEG",
                    ImageFlavor.Png => "PNG",
                    ImageFlavor.Tiff => "TIFF",
                    ImageFlavor.WebP => "RIFF",
                    ImageFlavor.Bmp => "BMP",
                    ImageFlavor.Gif => "GIF",
                    ImageFlavor.Psd => "Photoshop",
                    _ => m.ContainerName,
                };
                ImageMetadataReader.Read(fullPath, m, ct);
            }

            // RAW 内嵌预览（DNG/CR2 走 IFD；其它走流式扫描）
            if (m.RawFormat != RawFormat.None)
            {
                var preview = RawPreviewExtractor.Find(fullPath, m.RawFormat, ct);
                if (preview.Found)
                {
                    m.HasEmbeddedPreview = true;
                    m.EmbeddedPreviewWidth ??= preview.Width;
                    m.EmbeddedPreviewHeight ??= preview.Height;
                    // 主图尺寸缺失时用预览尺寸兜底（宽/高分别补，避免半残尺寸）
                    if ((m.Width is null or <= 0) && preview.Width > 0) m.Width = preview.Width;
                    if ((m.Height is null or <= 0) && preview.Height > 0) m.Height = preview.Height;
                }
                else
                {
                    m.Warnings.Add("未找到 RAW 内嵌 JPEG 预览（可能为纯 RAW 或预览被裁剪）");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            m.Warnings.Add("图片解析失败: " + ex.Message);
            Log.Warn($"[MetadataService] 图片解析失败: {fullPath}", ex);
        }
        return iso;
    }

    // ============================================================ 视频

    private static (IsoBmffInfo? Iso, MatroskaInfo? Matroska) ReadVideo(string fullPath, MediaMetadata m,
        ReadOnlySpan<byte> head, HashSet<string> hints, CancellationToken ct)
    {
        IsoBmffInfo? iso = null;
        MatroskaInfo? matroska = null;
        try
        {
            var isFtyp = head.Length >= 12 && MetaUtil.FourCCEquals(head, 4, "ftyp");
            var isEbml = head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
            var isRiff = head.Length >= 12 && MetaUtil.FourCCEquals(head, 0, "RIFF");

            if (isFtyp)
            {
                iso = IsoBmffParser.Parse(fullPath, ct);
                ApplyIsoBmff(iso, m);
                m.ContainerName = "ISOBMFF";
                CollectIsoHints(iso, m, hints);
                if (iso.XmpBytes is { Length: > 0 })
                {
                    var xmp = XmpReader.Parse(iso.XmpBytes);
                    XmpApplier.ApplyEmbedded(xmp, m);
                }
            }
            else if (isEbml)
            {
                matroska = MatroskaParser.Parse(fullPath, ct);
                ApplyMatroska(matroska, m);
                m.ContainerName = string.Equals(matroska.DocType, "webm", StringComparison.OrdinalIgnoreCase)
                    ? "WebM" : "Matroska";
                if (!string.IsNullOrWhiteSpace(matroska.Title)) hints.Add(matroska.Title!);
                if (!string.IsNullOrWhiteSpace(matroska.MuxingApp)) hints.Add(matroska.MuxingApp!);
                if (!string.IsNullOrWhiteSpace(matroska.WritingApp)) hints.Add(matroska.WritingApp!);
            }
            else if (isRiff)
            {
                m.ContainerName = "RIFF";
                m.Warnings.Add("AVI/RIFF 视频的深度解析暂未实现（仅保留扩展名信息）");
            }
            else
            {
                m.ContainerName ??= m.FormatCode;
                m.Warnings.Add($"视频封装 {m.FormatCode} 暂不支持深度解析（建议依赖 ffprobe 补充）");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            m.Warnings.Add("视频解析失败: " + ex.Message);
            Log.Warn($"[MetadataService] 视频解析失败: {fullPath}", ex);
        }
        return (iso, matroska);
    }

    // ============================================================ 音频

    private static IsoBmffInfo? ReadAudio(string fullPath, MediaMetadata m, ReadOnlySpan<byte> head, CancellationToken ct)
    {
        try
        {
            var isFtyp = head.Length >= 12 && MetaUtil.FourCCEquals(head, 4, "ftyp");
            if (isFtyp)
            {
                var iso = IsoBmffParser.Parse(fullPath, ct);
                ApplyIsoBmff(iso, m);
                m.ContainerName = "ISOBMFF";
                if (iso.XmpBytes is { Length: > 0 })
                {
                    var xmp = XmpReader.Parse(iso.XmpBytes);
                    XmpApplier.ApplyEmbedded(xmp, m);
                }
                return iso;
            }

            AudioMetadataReader.Read(fullPath, m, ct);   // 具体解析器成功时自己置 HasAudio
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            m.Warnings.Add("音频解析失败: " + ex.Message);
            Log.Warn($"[MetadataService] 音频解析失败: {fullPath}", ex);
            return null;
        }
    }

    // ============================================================ 结果映射

    private static void ApplyIsoBmff(IsoBmffInfo iso, MediaMetadata m)
    {
        if (iso is null) return;
        foreach (var warning in iso.Warnings) m.Warnings.Add(warning);

        m.ContainerName ??= "ISOBMFF";
        m.Width ??= iso.Width;
        m.Height ??= iso.Height;
        m.DurationSeconds ??= iso.DurationSeconds;
        m.FrameRate ??= iso.FrameRate;
        m.BitrateBps ??= iso.BitrateBps;
        m.VideoCodec ??= iso.VideoCodec;
        m.AudioCodec ??= iso.AudioCodec;
        m.AudioChannels ??= iso.AudioChannels;
        m.AudioSampleRate ??= iso.AudioSampleRate;
        if (iso.HasAudio) m.HasAudio = true;
        if (m.Kind == MediaKind.Audio) m.BitDepth ??= iso.AudioBitDepth;

        if (!string.IsNullOrEmpty(iso.FormatCode))
        {
            if (string.IsNullOrEmpty(m.FormatCode) || m.FormatCode == "UNKNOWN")
                m.FormatCode = iso.FormatCode!;
        }

        m.CameraMake ??= MetaUtil.CleanText(iso.Make, 128);
        m.CameraModel ??= MetaUtil.CleanText(iso.Model, 128);
        m.Title ??= MetaUtil.CleanText(iso.Title, 512);
        m.Creator ??= MetaUtil.CleanText(iso.Artist, 256);
        m.Caption ??= MetaUtil.CleanText(iso.Comment, 4096);
        m.Copyright ??= MetaUtil.CleanText(iso.Copyright, 512);
        if (!string.IsNullOrWhiteSpace(iso.Genre) && !m.Keywords.Contains(iso.Genre, StringComparer.OrdinalIgnoreCase))
            m.Keywords.Add(iso.Genre!);

        if (m.CapturedAt is null && iso.CreationDate is { } created)
        {
            m.CapturedAt = created;
            m.CapturedAtSource = "quicktime";
        }
        if (m.Latitude is null && iso.Latitude is { } lat && iso.Longitude is { } lon)
        {
            m.Latitude = lat;
            m.Longitude = lon;
            m.AltitudeMeters = iso.Altitude;
        }
    }

    private static void ApplyMatroska(MatroskaInfo k, MediaMetadata m)
    {
        if (k is null) return;
        foreach (var warning in k.Warnings) m.Warnings.Add(warning);
        m.Width ??= k.Width;
        m.Height ??= k.Height;
        m.DurationSeconds ??= k.DurationSeconds;
        m.FrameRate ??= k.FrameRate;
        m.BitrateBps ??= k.BitrateBps;
        m.VideoCodec ??= k.VideoCodec;
        m.AudioCodec ??= k.AudioCodec;
        m.AudioChannels ??= k.AudioChannels;
        m.AudioSampleRate ??= k.AudioSampleRate;
        if (m.Kind == MediaKind.Audio) m.BitDepth ??= k.AudioBitDepth;
        if (k.HasAudio) m.HasAudio = true;
        m.Title ??= k.Title;
        if (m.CapturedAt is null && k.CreationDate is { } created)
        {
            m.CapturedAt = created;
            m.CapturedAtSource = "matroska";
        }
    }

    // ============================================================ 设备归一化

    private static void NormalizeDevice(MediaMetadata m, string fullPath)
    {
        // CameraMake / CameraModel 保留 EXIF 原始写法（SONY / NIKON CORPORATION / ILCE-7M4），
        // 归一化只体现在 DeviceKey（人读商用名），避免破坏用户熟悉的厂商字段。
        var lens = DeviceNormalizer.NormalizeLens(m.LensModel, m.CameraMake);
        if (!string.IsNullOrWhiteSpace(lens)) m.LensModel = lens;

        m.DeviceKey = DeviceNormalizer.NormalizeDevice(m.CameraMake, m.CameraModel, fullPath);
    }

    // ============================================================ 色彩 / Log

    private static void ApplyColorDetection(string fullPath, MediaMetadata m, IsoBmffInfo? iso,
        MatroskaInfo? matroska, XmpData? sidecarXmp, HashSet<string> hints)
    {
        try
        {
            if (iso is not null)
            {
                if (!string.IsNullOrWhiteSpace(iso.IccProfileName)) m.ColorProfileName ??= iso.IccProfileName;
                if (!string.IsNullOrWhiteSpace(iso.Make)) hints.Add(iso.Make!);
                if (!string.IsNullOrWhiteSpace(iso.Model)) hints.Add(iso.Model!);
                if (!string.IsNullOrWhiteSpace(iso.Software)) hints.Add(iso.Software!);
            }
            if (matroska is not null)
            {
                if (!string.IsNullOrWhiteSpace(matroska.Title)) hints.Add(matroska.Title!);
            }

            var fileName = Path.GetFileName(fullPath);
            foreach (var token in ColorLogDetector.FileNameTokens)
                if (fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    hints.Add(token);

            var s = new ColorSignals
            {
                Kind = m.Kind,
                Make = m.CameraMake,
                Model = m.CameraModel,
                FormatCode = m.FormatCode,
                FileName = fileName,
                CicpPrimaries = iso?.CicpPrimaries ?? matroska?.CicpPrimaries,
                CicpTransfer = iso?.CicpTransfer ?? matroska?.CicpTransfer,
                CicpMatrix = iso?.CicpMatrix ?? matroska?.CicpMatrix,
                IccProfileName = iso?.IccProfileName ?? m.ColorProfileName,
                TextHints = hints.Count > 0 ? hints : null,
                XmpCameraProfile = sidecarXmp?.CameraProfile,
                XmpLookName = sidecarXmp?.LookName,
                XmpColorMode = sidecarXmp?.ColorSpace,
                XmpWhiteBalance = sidecarXmp?.WhiteBalance,
                XmpTemperatureK = sidecarXmp?.TemperatureK,
            };

            // 视频必然做判定；图片只在有显式文本/ICC 线索时才可能得出 Log/HDR 结论
            var info = ColorLogDetector.Detect(s);

            if (m.Kind == MediaKind.Image)
            {
                // 图片不臆测 Log：仅保留显式检出的 Log/HDR 与色域信息
                if (info.IsLog == true || info.TransferCharacteristics is "HLG" or "PQ (ST 2084)")
                {
                    m.IsLog ??= info.IsLog;
                    m.LogProfile ??= info.LogProfile;
                    m.ColorPrimaries ??= info.ColorPrimaries;
                    m.TransferCharacteristics ??= info.TransferCharacteristics;
                    m.ColorProfileName ??= info.ColorProfileName;
                }
                else
                {
                    m.ColorPrimaries ??= info.ColorPrimaries;
                    m.ColorMatrix ??= info.ColorMatrix;
                }
            }
            else
            {
                ColorLogDetector.ApplyTo(info, m);
            }

            if (m.ColorTemperatureK is null && sidecarXmp?.TemperatureK is { } temp) m.ColorTemperatureK = temp;
            ColorLogDetector.LogResult(fullPath, info);
        }
        catch (Exception ex)
        {
            m.Warnings.Add("色彩/Log 判定失败: " + ex.Message);
            Log.Warn($"[MetadataService] 色彩判定失败: {fullPath}", ex);
        }
    }

    private static void CollectIsoHints(IsoBmffInfo iso, MediaMetadata m, HashSet<string> hints)
    {
        foreach (var hint in iso.TextHints) hints.Add(hint);
        foreach (var text in iso.MetadataStrings)
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 512) hints.Add(text);
        if (!string.IsNullOrWhiteSpace(iso.Make)) hints.Add(iso.Make!);
        if (!string.IsNullOrWhiteSpace(iso.Model)) hints.Add(iso.Model!);
    }

    // ============================================================ 兜底

    private static void ApplyFileTime(string fullPath, MediaMetadata m)
    {
        try
        {
            var modified = File.GetLastWriteTime(fullPath);
            if (modified.Year is >= 1970 and <= 2200)
            {
                m.CapturedAt = new DateTimeOffset(modified);
                m.CapturedAtSource = "file";
            }
        }
        catch { /* 忽略 */ }
    }

    private static byte[] ReadHead(string fullPath, int maxBytes, out int read)
    {
        read = 0;
        var buffer = new byte[Math.Max(4, maxBytes)];
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(fullPath);
            read = RandomAccess.Read(fs.SafeFileHandle, buffer, 0);
            if (read < 0) read = 0;
        }
        catch
        {
            read = 0;
        }
        return buffer;
    }
}