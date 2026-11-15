using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Ai;

/// <summary>
/// AI 视觉分类编排：取图（RAW 用内嵌预览 / 视频抽帧）→ 压缩 JPEG → 提示词 → 调用
/// <see cref="IAiClient"/> → 容错 JSON 解析 → 词表归一 → <see cref="AiSuggestion"/>。
/// <para>批量 <see cref="RunBatchAsync"/> 用 SemaphoreSlim 控并发、回调进度、按 Provenance 决定写回：
/// 人工（Manual）/边车（Sidecar）结果默认不覆盖。</para>
/// </summary>
public sealed class AiClassificationService : IAiClassificationService
{
    private const int MaxEmbeddedImageBytes = 8 * 1024 * 1024;
    private const int VisionMaxTokens = 800;

    private readonly IAiClient _aiClient;
    private readonly IMediaDecoder _decoder;
    private readonly IImageEncoder _encoder;
    private readonly ISettingsService _settings;

    public AiClassificationService(
        IAiClient aiClient,
        IMediaDecoder decoder,
        IImageEncoder encoder,
        ISettingsService settingsService)
    {
        _aiClient = aiClient ?? throw new ArgumentNullException(nameof(aiClient));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _settings = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    // ============================ 单条分析 ============================

    public async Task<AiSuggestion> AnalyzeAssetAsync(MediaAsset asset, CancellationToken ct = default)
    {
        if (asset is null) return new AiSuggestion { Error = "素材为空" };

        var ai = CurrentAiSettings();
        var requestModel = FirstNonEmpty(ai.VisionModel, ai.TextModel);

        try
        {
            ct.ThrowIfCancellationRequested();

            byte[]? image = null;
            if (ai.VisionEnabled) image = await TryGetImageAsync(asset, ai, ct).ConfigureAwait(false);

            var needsVisual = ai.VisionEnabled &&
                              asset.Kind is MediaKind.Image or MediaKind.Video or MediaKind.Unknown;
            if (image is null && needsVisual && !ai.DryRun)
            {
                return new AiSuggestion
                {
                    Error = $"无法取得可分析的图像（文件缺失或解码失败）：{asset.FileName}",
                    ModelId = requestModel,
                };
            }

            var userMessage = new AiChatMessage
            {
                Role = "user",
                Text = PromptLibrary.BuildUserText(asset, image is not null),
            };
            if (image is not null)
            {
                userMessage.ImageBytes = image;
                userMessage.ImageMime = GuessImageMime(image);
                requestModel = FirstNonEmpty(ai.VisionModel, ai.TextModel);
            }
            else
            {
                requestModel = FirstNonEmpty(ai.TextModel, ai.VisionModel);
            }

            var request = new AiRequest
            {
                Model = requestModel,
                RequireJson = true,
                JsonSchema = PromptLibrary.JsonSchema,
                JsonSchemaName = PromptLibrary.SchemaName,
                Temperature = ai.Temperature,
                MaxTokens = VisionMaxTokens,
                CorrelationId = asset.Id,
                Messages =
                {
                    new AiChatMessage { Role = "system", Text = PromptLibrary.BuildSystemPrompt(ai) },
                    userMessage,
                },
            };

            AiRawResponse response;
            try
            {
                response = await _aiClient.CompleteAsync(request, ai, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"AI 调用异常（{asset.FileName}）", ex);
                return new AiSuggestion { Error = "AI 调用异常: " + ex.Message, ModelId = requestModel };
            }

            if (!response.Success)
            {
                return new AiSuggestion
                {
                    Error = response.Error ?? "AI 调用失败",
                    ModelId = response.Model ?? requestModel,
                    RawJson = response.RawJson,
                };
            }

            var suggestion = AiJsonParser.ParseSuggestion(response.Text, response.Model ?? requestModel);
            suggestion.RawJson = response.Text;
            suggestion.At = DateTimeOffset.Now;

            if (suggestion.Success)
            {
                // 模型可能返回中文/英文/自造值，统一映射回稳定 ID。
                suggestion.Theme = Vocabularies.NormalizeTheme(suggestion.Theme);
                suggestion.Tone = Vocabularies.NormalizeTone(suggestion.Tone);
                if (!string.IsNullOrWhiteSpace(suggestion.Theme) || !string.IsNullOrWhiteSpace(suggestion.Tone) ||
                    suggestion.Keywords.Count > 0 || suggestion.Subject is not null)
                {
                    suggestion.Error = null;
                }
            }

            return suggestion;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"AI 分类异常（{asset.FileName}）", ex);
            return new AiSuggestion { Error = "AI 分类异常: " + ex.Message, ModelId = requestModel };
        }
    }

    /// <summary>把 AI 建议写回素材（按 Provenance 保护人工/边车结果）。返回是否发生实际修改。</summary>
    public bool ApplySuggestion(MediaAsset asset, AiSuggestion suggestion, AiBatchOptions? options = null)
    {
        options ??= new AiBatchOptions();
        return ApplySuggestionCore(asset, suggestion, options);
    }

    // ============================ 批量 ============================

    public async Task<AiBatchResult> RunBatchAsync(
        IReadOnlyList<MediaAsset> assets,
        AiBatchOptions options,
        IProgress<AiBatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new AiBatchOptions();
        var ai = CurrentAiSettings();
        var state = new BatchState();

        var candidates = BuildCandidates(assets, options, ai, out var preSkipped);
        state.Total = candidates.Count;
        state.Processed = preSkipped;
        state.Skipped = preSkipped;
        Report(progress, state, $"准备处理 {candidates.Count} 条素材（跳过 {preSkipped} 条）", done: false);

        var result = new AiBatchResult();

        if (candidates.Count == 0)
        {
            Report(progress, state, "没有需要 AI 分类的素材", done: true);
            result.Progress = state.Snapshot("没有需要 AI 分类的素材", done: true);
            return result;
        }

        var concurrency = Math.Clamp(ai.Concurrency <= 0 ? 3 : ai.Concurrency, 1, 16);
        using var concurrencyGate = new SemaphoreSlim(concurrency, concurrency);
        var sync = new object();
        var updated = new bool[candidates.Count];
        var tasks = new List<Task>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var index = i;
            var asset = candidates[i];

            tasks.Add(Task.Run(async () =>
            {
                await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    AiSuggestion suggestion;
                    try
                    {
                        suggestion = await AnalyzeAssetAsync(asset, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        suggestion = new AiSuggestion { Error = "分析异常: " + ex.Message, ModelId = ai.VisionModel };
                    }

                    var applied = false;
                    var belowThreshold = false;
                    if (suggestion.Success)
                    {
                        if (suggestion.Confidence >= options.MinConfidenceToApply)
                            applied = ApplySuggestionCore(asset, suggestion, options);
                        else
                            belowThreshold = true;
                    }

                    lock (sync)
                    {
                        state.Processed++;
                        if (!suggestion.Success) state.Failed++;
                        else if (belowThreshold) state.Skipped++;
                        else if (applied) { state.Succeeded++; updated[index] = true; }
                        else state.Skipped++;   // 解析成功但无可写字段（例如全部被人工作品质保护）

                        state.CurrentFile = asset.FileName;
                        state.LastAssetId = asset.Id;
                        state.LastSuggestion = suggestion;
                        Report(progress, state, null, done: false);
                    }
                }
                finally
                {
                    concurrencyGate.Release();
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (sync) Report(progress, state, "已取消", done: true);
            throw;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (updated[i]) result.Updated.Add(candidates[i]);
        }

        var finalMessage = $"完成：成功 {state.Succeeded}，跳过 {state.Skipped}，失败 {state.Failed}";
        lock (sync) Report(progress, state, finalMessage, done: true);
        result.Progress = state.Snapshot(finalMessage, done: true);
        return result;
    }

    // ============================ 建议写回 ============================

    private bool ApplySuggestionCore(MediaAsset asset, AiSuggestion suggestion, AiBatchOptions options)
    {
        if (asset is null || suggestion is null || !suggestion.Success) return false;
        if (suggestion.Confidence < options.MinConfidenceToApply) return false;

        var changed = false;
        var confidence = Math.Clamp(suggestion.Confidence, 0, 1);
        var provenance = FieldProvenance.Of(ClassSource.Ai, confidence, suggestion.ModelId);
        provenance.At = suggestion.At;

        // 主题
        if (!string.IsNullOrWhiteSpace(suggestion.Theme) &&
            CanWrite(asset, ClassDimension.Theme, options.OverwriteManual))
        {
            var valueChanged = !string.Equals(asset.Theme, suggestion.Theme, StringComparison.OrdinalIgnoreCase);
            var provChanged = asset.Prov(ClassDimension.Theme)?.Source != ClassSource.Ai;
            asset.Theme = suggestion.Theme;
            asset.SetProv(ClassDimension.Theme, provenance.Clone());
            changed |= valueChanged || provChanged;
        }

        // 影调
        if (!string.IsNullOrWhiteSpace(suggestion.Tone) &&
            CanWrite(asset, ClassDimension.Tone, options.OverwriteManual))
        {
            var valueChanged = !string.Equals(asset.Tone, suggestion.Tone, StringComparison.OrdinalIgnoreCase);
            var provChanged = asset.Prov(ClassDimension.Tone)?.Source != ClassSource.Ai;
            asset.Tone = suggestion.Tone;
            asset.SetProv(ClassDimension.Tone, provenance.Clone());
            changed |= valueChanged || provChanged;
        }

        // 关键词：合并去重（不覆盖已有，特别是人工标签）
        if (options.ApplyKeywords && suggestion.Keywords.Count > 0 &&
            CanWrite(asset, ClassDimension.Keywords, options.OverwriteManual))
        {
            var before = asset.Keywords.Count;
            foreach (var rawKeyword in suggestion.Keywords)
            {
                var keyword = rawKeyword?.Trim();
                if (string.IsNullOrWhiteSpace(keyword)) continue;
                if (keyword.Length > 40) keyword = keyword[..40];
                if (!asset.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                    asset.Keywords.Add(keyword);
            }
            asset.SetProv(ClassDimension.Keywords, provenance.Clone());
            changed |= asset.Keywords.Count != before;
        }

        // 一句话内容 / 情绪：AiSubject / AiMood 是 AI 专属字段
        if (!string.IsNullOrWhiteSpace(suggestion.Subject))
        {
            asset.AiSubject = suggestion.Subject.Trim();
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(suggestion.Mood))
        {
            asset.AiMood = suggestion.Mood.Trim();
            changed = true;
        }

        // Log 判断
        // 注意：容器/相机给出的 CICP/comment/XMP 证据比"模型看图猜"更权威，
        // 元数据已经明确 IsLog 时，AI 只作为补充而不是覆盖（否则会抹掉真实的 S-Log3 判定）。
        if (suggestion.IsLog is { } isLog && asset.IsLog is null &&
            CanWrite(asset, ClassDimension.VideoLog, options.OverwriteManual))
        {
            var valueChanged = asset.IsLog != isLog;
            var provChanged = asset.Prov(ClassDimension.VideoLog)?.Source != ClassSource.Ai;
            asset.IsLog = isLog;
            asset.SetProv(ClassDimension.VideoLog, provenance.Clone());
            changed |= valueChanged || provChanged;
        }

        // 技术质量
        if (options.ApplyQuality && suggestion.QualityScore is { } quality &&
            CanWrite(asset, ClassDimension.Quality, options.OverwriteManual))
        {
            var value = Math.Round(Math.Clamp(quality, 0, 1), 3);
            var valueChanged = asset.QualityScore is null || Math.Abs(asset.QualityScore.Value - value) > 0.0005;
            asset.QualityScore = value;
            asset.SetProv(ClassDimension.Quality, provenance.Clone());
            changed |= valueChanged;
        }

        return changed;
    }

    private static bool CanWrite(MediaAsset asset, ClassDimension dimension, bool overwriteManual)
    {
        var provenance = asset.Prov(dimension);
        if (provenance is null) return true;
        return provenance.Source switch
        {
            // 人工与后期边车（Lightroom/Capture One 等写回的 XMP）都视为人工结果。
            ClassSource.Manual or ClassSource.Sidecar => overwriteManual,
            _ => true,
        };
    }

    // ============================ 候选筛选 ============================

    private static List<MediaAsset> BuildCandidates(
        IReadOnlyList<MediaAsset>? assets,
        AiBatchOptions options,
        AiSettings ai,
        out int skipped)
    {
        var candidates = new List<MediaAsset>();
        skipped = 0;
        if (assets is null || assets.Count == 0) return candidates;

        var limit = options.MaxItems > 0 ? options.MaxItems : int.MaxValue;
        if (ai.DailyCallLimit > 0) limit = Math.Min(limit, ai.DailyCallLimit);

        foreach (var asset in assets)
        {
            if (asset is null) continue;
            if (candidates.Count >= limit) break;

            if (string.IsNullOrWhiteSpace(asset.FullPath))
            {
                skipped++;
                continue;
            }
            if (options.OnlyUnclassified && HasClassification(asset))
            {
                skipped++;
                continue;
            }
            if (!options.OverwriteManual && IsHumanLocked(asset))
            {
                skipped++;
                continue;
            }
            candidates.Add(asset);
        }
        return candidates;
    }

    private static bool HasClassification(MediaAsset asset) =>
        !string.IsNullOrWhiteSpace(asset.Theme) && !string.IsNullOrWhiteSpace(asset.Tone);

    private static bool IsHumanLocked(MediaAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Theme) || string.IsNullOrWhiteSpace(asset.Tone)) return false;
        return IsHumanSource(asset.Prov(ClassDimension.Theme)) && IsHumanSource(asset.Prov(ClassDimension.Tone));
    }

    private static bool IsHumanSource(FieldProvenance? provenance) =>
        provenance?.Source is ClassSource.Manual or ClassSource.Sidecar;

    // ============================ 取图 ============================

    private async Task<byte[]?> TryGetImageAsync(MediaAsset asset, AiSettings ai, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset.FullPath)) return null;

        var maxDimension = Math.Clamp(ai.MaxImageDimension <= 0 ? 1024 : ai.MaxImageDimension, 256, 4096);
        var quality = Math.Clamp(ai.JpegQuality <= 0 ? 82 : ai.JpegQuality, 40, 100);

        try
        {
            if (asset.Kind == MediaKind.Video)
            {
                var position = asset.DurationSeconds is > 0
                    ? Math.Min(2.0, Math.Max(0.1, asset.DurationSeconds.Value * 0.25))
                    : 1.0;
                var frame = await _decoder.ExtractVideoFrameAsync(asset.FullPath, position, maxDimension, ct)
                    .ConfigureAwait(false);
                if (frame is { Length: > 0 } && frame.Length <= MaxEmbeddedImageBytes) return frame;
            }
            else if (asset.Kind == MediaKind.Audio)
            {
                var cover = await _decoder.ExtractAudioCoverAsync(asset.FullPath, ct).ConfigureAwait(false);
                if (cover is { Length: > 0 } && cover.Length <= MaxEmbeddedImageBytes) return cover;
                return null; // 音频没有封面就退化为纯文本分析
            }

            // RAW：优先内嵌预览（WIC 常常不支持 RAW 解码，而预览本身就是 JPEG）。
            if (asset.IsRaw)
            {
                var preview = await _decoder.ExtractEmbeddedPreviewAsync(asset.FullPath, ct).ConfigureAwait(false);
                if (preview is { Length: > 0 } && preview.Length <= MaxEmbeddedImageBytes) return preview;
            }

            var decoded = await _decoder.DecodeAsync(asset.FullPath, maxDimension, ct).ConfigureAwait(false);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;

            var jpeg = await _encoder.ResizeToJpegAsync(decoded, maxDimension, quality, ct).ConfigureAwait(false);
            if (jpeg is { Length: > 0 }) return jpeg;

            return await _encoder.EncodeJpegAsync(decoded, quality, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"AI 取图失败（{asset.FileName}）", ex);
            return null;
        }
    }

    /// <summary>按魔数猜图片 MIME（解码器可能返回 JPEG/PNG/BMP 等不同格式的字节）。</summary>
    public static string GuessImageMime(byte[] data)
    {
        if (data is null || data.Length < 3) return "image/jpeg";
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
            data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
            return "image/webp";
        if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return "image/gif";
        if (data[0] == 'B' && data[1] == 'M') return "image/bmp";
        return "image/jpeg";
    }

    // ============================ 杂项 ============================

    private AiSettings CurrentAiSettings()
    {
        try
        {
            return _settings.Current?.Ai ?? new AiSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("读取 AI 设置失败，使用默认设置", ex);
            return new AiSettings();
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static void Report(IProgress<AiBatchProgress>? progress, BatchState state, string? message, bool done)
    {
        if (progress is null) return;
        try { progress.Report(state.Snapshot(message, done)); }
        catch (Exception ex) { Log.Warn("AI 批次进度回调异常（已忽略）", ex); }
    }

    private sealed class BatchState
    {
        public int Total;
        public int Processed;
        public int Succeeded;
        public int Failed;
        public int Skipped;
        public string? CurrentFile;
        public string? LastAssetId;
        public AiSuggestion? LastSuggestion;

        public AiBatchProgress Snapshot(string? message, bool done) => new()
        {
            Total = Total,
            Processed = Processed,
            Succeeded = Succeeded,
            Failed = Failed,
            Skipped = Skipped,
            CurrentFile = CurrentFile,
            LastSuggestion = LastSuggestion,
            LastAssetId = LastAssetId,
            Message = message,
            Done = done,
        };
    }
}