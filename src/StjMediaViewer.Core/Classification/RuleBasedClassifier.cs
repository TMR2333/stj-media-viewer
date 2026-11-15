using System.Text.RegularExpressions;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Classification;

/// <summary>
/// 纯元数据的确定性规则分类器：补全格式/设备/Log/后期状态，写 <see cref="ClassSource.Derived"/> Provenance；
/// 并结合 <see cref="ImageStats"/> 给出影调建议。
/// <para>不覆盖人工（Manual）/边车（Sidecar）已经标注过的维度；拿不准就留空。</para>
/// </summary>
public sealed class RuleBasedClassifier : IRuleClassifier
{
    private static readonly Regex EditHintRegex = new(
        @"(^|[_\-\s.])(final|finals|edit|edited|export|exported|retouch|retouched|processed|web|social|print)([_\-\s.]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] ChineseEditHints = { "修图", "精修", "调色", "成品", "成片", "导出" };

    private static readonly string[] JpegExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };

    /// <summary>Log 传输/色彩配置特征 → 标准 Log 名（长 token 在前，避免 "LOG" 抢先匹配）。</summary>
    private static readonly (string Token, string Profile)[] LogTokens =
    {
        ("APPLE LOG", "Apple Log"),
        ("APPLELOG", "Apple Log"),
        ("S-LOG3", "S-Log3"),
        ("SLOG3", "S-Log3"),
        ("S-LOG2", "S-Log2"),
        ("SLOG2", "S-Log2"),
        ("C-LOG3", "C-Log3"),
        ("CLOG3", "C-Log3"),
        ("C-LOG2", "C-Log2"),
        ("V-LOGL", "V-Log L"),
        ("V-LOG", "V-Log"),
        ("D-LOG", "D-Log"),
        ("N-LOG", "N-Log"),
        ("LOG3G10", "Log3G10"),
        ("LOGC", "LogC"),
        ("LOG", "Log"),
    };

    private static readonly string[] StandardTransferTokens =
    {
        "BT.709", "BT709", "REC.709", "REC709", "SDR", "SRGB", "GAMMA 2.2", "GAMMA2.2", "BT.2020", "BT2020",
    };

    // ============================ ApplyDerived ============================

    public void ApplyDerived(MediaAsset asset)
    {
        if (asset is null) return;
        try
        {
            EnsureFormat(asset);
            ApplyImageKind(asset);
            ApplyDevice(asset);
            ApplyVideoLog(asset);
            ApplyPostProcess(asset);
        }
        catch (Exception ex)
        {
            Log.Warn($"规则分类失败（{asset.FileName}）", ex);
        }
    }

    public string? SuggestDeviceKey(string? make, string? model, string? fileName) =>
        DeviceNormalizer.Guess(make, model, fileName)?.Key;

    // ============================ 格式 / ImageKind ============================

    private static void EnsureFormat(MediaAsset asset)
    {
        var path = !string.IsNullOrWhiteSpace(asset.FullPath) ? asset.FullPath : asset.FileName;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (MediaTypeCatalog.TryGet(path, out var kind, out var format, out var flavor, out var rawFormat, out var mime))
        {
            if (asset.Kind == MediaKind.Unknown) asset.Kind = kind;
            if (string.IsNullOrWhiteSpace(asset.FormatCode)) asset.FormatCode = format;
            if (asset.ImageFlavor == ImageFlavor.None) asset.ImageFlavor = flavor;
            if (asset.RawFormat == RawFormat.None) asset.RawFormat = rawFormat;
            asset.MimeType ??= mime;
        }
        else if (string.IsNullOrWhiteSpace(asset.FormatCode))
        {
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext)) asset.FormatCode = ext.TrimStart('.').ToUpperInvariant();
        }
    }

    private static void ApplyImageKind(MediaAsset asset)
    {
        var existing = asset.Prov(ClassDimension.ImageKind);
        if (existing?.Source is ClassSource.Manual or ClassSource.Sidecar) return;

        // ImageKind 只区分 JPG / RAW（用于 JPG+RAW 成对管理）；其他图片/视频不写该维度。
        string? imageKind = asset.Kind switch
        {
            MediaKind.Image when asset.IsRaw => "raw",
            MediaKind.Image when asset.ImageFlavor == ImageFlavor.Jpeg => "jpg",
            _ => null,
        };
        if (imageKind is null) return;

        var alreadyCorrect = existing?.Source == ClassSource.Derived &&
                             existing.Note == imageKind;
        if (alreadyCorrect) return;

        asset.SetProv(ClassDimension.ImageKind, DerivedProv(1.0, imageKind));
    }

    // ============================ 设备 ============================

    private static void ApplyDevice(MediaAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.DeviceKey)) return;
        var existing = asset.Prov(ClassDimension.Device);
        if (existing?.Source is ClassSource.Manual or ClassSource.Sidecar) return;

        var guess = DeviceNormalizer.Guess(asset.CameraMake, asset.CameraModel, asset.FileName);
        if (guess is null) return;

        asset.DeviceKey = guess.Key;
        asset.SetProv(ClassDimension.Device, DerivedProv(guess.Confidence, guess.Note));
    }

    // ============================ Log ============================

    private static void ApplyVideoLog(MediaAsset asset)
    {
        var existing = asset.Prov(ClassDimension.VideoLog);
        if (existing?.Source is ClassSource.Manual or ClassSource.Ai or ClassSource.Sidecar) return;

        var haystack = string.Join(" ",
            asset.TransferCharacteristics, asset.ColorPrimaries, asset.ColorMatrix,
            asset.ColorProfileName, asset.LogProfile, asset.VideoCodec).ToUpperInvariant();

        var profile = DetectLogProfile(haystack);
        bool? isLog = asset.IsLog;
        double confidence = 0.85;
        string? note = null;

        if (isLog is null)
        {
            if (profile is not null)
            {
                isLog = true;
                confidence = 0.85;
                note = "按传输特性/色彩配置识别为 Log";
            }
            else
            {
                var fileName = (asset.FileName ?? "").ToUpperInvariant();
                if (ContainsAny(fileName, "_LOG", "-LOG", "SLOG", "VLOG", "CLOG", "LOG3", "LOG2"))
                {
                    isLog = true;
                    confidence = 0.6;
                    note = "按文件名推断为 Log";
                }
                else if (ContainsAny(haystack, StandardTransferTokens))
                {
                    isLog = false;
                    confidence = 0.8;
                    note = "传输特性为 BT.709/SDR";
                }
                else if (asset.IsVideo &&
                         (!string.IsNullOrWhiteSpace(asset.CameraMake) || !string.IsNullOrWhiteSpace(asset.CameraModel)))
                {
                    isLog = false;
                    confidence = 0.5;
                    note = "相机素材未发现 Log 特征";
                }
            }
        }
        else if (isLog == true)
        {
            confidence = 0.9;
            note = "元数据已标记 Log";
        }

        if (isLog is null) return;

        asset.IsLog = isLog;
        if (isLog == true && !string.IsNullOrWhiteSpace(profile)) asset.LogProfile ??= profile;
        asset.SetProv(ClassDimension.VideoLog, DerivedProv(confidence, note));
    }

    private static string? DetectLogProfile(string upperHaystack)
    {
        foreach (var (token, profile) in LogTokens)
        {
            if (upperHaystack.Contains(token, StringComparison.Ordinal)) return profile;
        }
        return null;
    }

    private static bool ContainsAny(string text, params string[] tokens) =>
        tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));

    // ============================ 后期状态 ============================

    private static void ApplyPostProcess(MediaAsset asset)
    {
        var existing = asset.Prov(ClassDimension.PostProcess);
        if (existing?.Source is ClassSource.Manual or ClassSource.Sidecar) return;

        // 元数据引擎/边车已给出明确状态时不重判，只补 Provenance。
        if (asset.PostProcess != PostProcessState.Unknown)
        {
            if (existing is null)
            {
                asset.SetProv(ClassDimension.PostProcess,
                    DerivedProv(0.8,
                        asset.PostProcess == PostProcessState.Processed ? "元数据已标记已调色" : "元数据已标记未调色"));
            }
            return;
        }

        bool? processed = null;
        double confidence = 0.5;
        string? note = null;
        var engine = asset.DevelopEngine;

        var sidecars = AllSidecars(asset);
        if (string.IsNullOrWhiteSpace(engine))
        {
            foreach (var sidecar in sidecars)
            {
                var guessed = GuessEngineFromSidecar(sidecar);
                if (guessed is null) continue;
                engine = guessed;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(engine) && !string.IsNullOrWhiteSpace(asset.XmpPath))
            engine = GuessEngineFromSidecar(asset.XmpPath) ?? "Lightroom";

        if (sidecars.Count > 0)
        {
            processed = true;
            confidence = 0.9;
            note = "存在后期边车文件";
        }
        else if (!string.IsNullOrWhiteSpace(asset.XmpPath))
        {
            processed = true;
            confidence = 0.85;
            note = "存在 XMP 边车";
        }
        else if (asset.HasDevelopEdits)
        {
            processed = true;
            confidence = 0.85;
            note = "元数据标记有后期编辑";
        }
        else if (asset.HasEmbeddedXmp)
        {
            processed = true;
            confidence = 0.6;
            note = "内嵌 XMP";
        }
        else if (asset.RawFormat == RawFormat.Dng && !string.IsNullOrWhiteSpace(engine))
        {
            processed = true;
            confidence = 0.7;
            note = "DNG 内含后期软件标记";
        }
        else if (asset.IsRaw)
        {
            var sibling = FindSameNameJpeg(asset);
            if (sibling is not null)
            {
                processed = true;
                confidence = 0.45;
                note = "RAW 存在同名 JPG（可能是导出，也可能是机内双存）";
            }
        }

        if (processed is null && FileNameHasEditHint(asset.FileName))
        {
            processed = true;
            confidence = 0.5;
            note = "文件名含后期/导出标记";
        }
        if (processed is null && !string.IsNullOrWhiteSpace(engine))
        {
            processed = true;
            confidence = 0.7;
            note = "识别到后期软件标记";
        }
        if (processed is null) return;

        asset.PostProcess = processed.Value ? PostProcessState.Processed : PostProcessState.Unprocessed;
        if (processed.Value && confidence >= 0.6) asset.HasDevelopEdits = true;
        if (!string.IsNullOrWhiteSpace(engine)) asset.DevelopEngine = engine;
        asset.SetProv(ClassDimension.PostProcess, DerivedProv(confidence, note));
    }

    private static List<string> AllSidecars(MediaAsset asset)
    {
        var result = new List<string>();
        foreach (var sidecar in asset.Sidecars)
        {
            if (!string.IsNullOrWhiteSpace(sidecar) &&
                !result.Contains(sidecar, StringComparer.OrdinalIgnoreCase))
                result.Add(sidecar);
        }
        if (!string.IsNullOrWhiteSpace(asset.XmpPath) &&
            !result.Contains(asset.XmpPath, StringComparer.OrdinalIgnoreCase))
            result.Add(asset.XmpPath);
        return result;
    }

    private static string? GuessEngineFromSidecar(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".dop" => "DxO PhotoLab",
            ".cos" => "Capture One",
            ".on1" => "ON1 Photo RAW",
            ".pp3" => "RawTherapee",
            ".arp" => "ART",
            ".lrtemplate" => "Lightroom",
            ".xmp" => "Lightroom",
            _ => null,
        };
    }

    private static string? FindSameNameJpeg(MediaAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.FullPath)) return null;
        try
        {
            var directory = Path.GetDirectoryName(asset.FullPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

            var stem = Path.GetFileNameWithoutExtension(asset.FullPath);
            foreach (var extension in JpegExtensions)
            {
                var candidate = Path.Combine(directory, stem + extension);
                if (!File.Exists(candidate)) continue;
                if (string.Equals(candidate, asset.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
                return candidate;
            }
        }
        catch
        {
            // 目录不可访问就放弃这条弱推断
        }
        return null;
    }

    private static bool FileNameHasEditHint(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (EditHintRegex.IsMatch(fileName)) return true;
        return ChineseEditHints.Any(hint => fileName.Contains(hint, StringComparison.Ordinal));
    }

    // ============================ 影调建议 ============================

    public string? SuggestTone(MediaAsset asset, ImageStats? stats)
    {
        if (asset is null) return null;
        try
        {
            var provenance = asset.Prov(ClassDimension.Tone);
            // AI / 人工结果优先，不覆盖。
            if (!string.IsNullOrWhiteSpace(asset.Tone) &&
                provenance?.Source is ClassSource.Manual or ClassSource.Ai)
                return asset.Tone;

            var candidates = new List<(string Id, double Confidence, string Why)>();

            var statTone = Vocabularies.NormalizeTone(stats?.SuggestTone());
            if (statTone is not null) candidates.Add((statTone, stats!.IsMonochrome ? 0.9 : 0.6, "直方图"));
            if (stats is { IsMonochrome: true } && statTone != "mono") candidates.Add(("mono", 0.9, "直方图"));
            if (stats is not null && stats.MeanSaturation < 0.12 && statTone != "lowsat")
                candidates.Add(("lowsat", 0.5, "低饱和"));

            if (asset.ColorTemperatureK is { } kelvin)
            {
                if (kelvin < 3800) candidates.Add(("cool", 0.55, "色温偏低"));
                else if (kelvin > 6200) candidates.Add(("warm", 0.55, "色温偏高"));
            }

            if (asset.IsLog == true)
                candidates.Add(("natural", 0.35, "Log 素材（未调色）"));

            if (!string.IsNullOrWhiteSpace(asset.Tone) && provenance?.Source is ClassSource.Derived or null)
                candidates.Add((asset.Tone, 0.45, "已有推断"));

            if (candidates.Count == 0) return null;

            var best = candidates
                .OrderByDescending(c => c.Confidence)
                .Select(c => c.Id)
                .FirstOrDefault();
            return Vocabularies.NormalizeTone(best);
        }
        catch (Exception ex)
        {
            Log.Warn($"影调建议失败（{asset.FileName}）", ex);
            return null;
        }
    }

    private static FieldProvenance DerivedProv(double confidence, string? note)
    {
        var provenance = FieldProvenance.Of(ClassSource.Derived, confidence);
        provenance.Note = note;
        return provenance;
    }
}