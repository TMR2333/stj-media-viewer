using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>AI 队列里的一条实时日志（不可变，创建后只读）。</summary>
public sealed class AiQueueLogEntry
{
    public AiQueueLogEntry(DateTimeOffset at, string fileName, string summary, bool isError)
    {
        TimeText = at.ToLocalTime().ToString("HH:mm:ss");
        FileName = string.IsNullOrEmpty(fileName) ? "—" : fileName;
        Summary = summary;
        IsError = isError;
    }

    public string TimeText { get; }
    public string FileName { get; }
    public string Summary { get; }
    public bool IsError { get; }
    public string LineText => $"{FileName}   {Summary}";

    public Visibility ErrorVisibility => IsError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OkVisibility => IsError ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>结果汇总里的主题分布柱。</summary>
public sealed class ThemeBar
{
    public ThemeBar(string label, int count, double width)
    {
        Label = label;
        Count = count;
        Width = width;
        CountText = count.ToString();
    }

    public string Label { get; }
    public int Count { get; }
    public double Width { get; }
    public string CountText { get; }
}

/// <summary>
/// AI 批量分类工作台：范围 → 批量选项 → RunBatchAsync(progress) → 汇总 + 写 XMP。
/// AI 未配置时只展示引导，不阻塞界面。
/// </summary>
public sealed partial class AiQueueViewModel : ObservableObject
{
    private const int MaxLogs = 200;
    private const int MaxScanItems = 2000;

    public static AiQueueViewModel Shared { get; } = new();

    private CancellationTokenSource? _cts;
    private int _runVersion;
    private Stopwatch? _stopwatch;
    private readonly List<MediaAsset> _updated = new();

    private AiQueueViewModel()
    {
        Logs = new ObservableCollection<AiQueueLogEntry>();
        ThemeBars = new ObservableCollection<ThemeBar>();
    }

    // ---------------- 范围 / 选项 ----------------
    [ObservableProperty]
    private string _scopeId = "unclassified";

    [ObservableProperty]
    private string _scopeName = "只处理未分类";

    [ObservableProperty]
    private int _maxItems = 50;

    [ObservableProperty]
    private bool _overwriteManual;

    [ObservableProperty]
    private bool _applyKeywords = true;

    [ObservableProperty]
    private bool _applyQuality = true;

    [ObservableProperty]
    private int _concurrency = 3;

    partial void OnScopeIdChanged(string value)
    {
        ScopeName = value switch
        {
            "all" => "全部素材",
            "filtered" => "当前筛选结果",
            "selected" => "已选中的素材",
            _ => "只处理未分类",
        };
        ClearResult();
        RefreshGuidance();
    }

    [ObservableProperty]
    private string _scopeSummary = "";

    // ---------------- 运行状态 ----------------
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private int _processed;

    [ObservableProperty]
    private int _succeeded;

    [ObservableProperty]
    private int _failed;

    [ObservableProperty]
    private int _skipped;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = "尚未开始";

    [ObservableProperty]
    private string _currentFile = "";

    [ObservableProperty]
    private Visibility _progressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _summaryVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _guidanceVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _guidanceText = "";

    [ObservableProperty]
    private Visibility _apiReadyVisibility = Visibility.Visible;

    [ObservableProperty]
    private string _apiHintText = "";

    [ObservableProperty]
    private string _successRateText = "—";

    [ObservableProperty]
    private string _averageText = "—";

    [ObservableProperty]
    private string _updatedText = "";

    [ObservableProperty]
    private string _startButtonText = "开始分类";

    [ObservableProperty]
    private bool _hasUpdated;

    [ObservableProperty]
    private string _runStatusText = "";

    public ObservableCollection<AiQueueLogEntry> Logs { get; }
    public ObservableCollection<ThemeBar> ThemeBars { get; }

    // StatTile 的 Value 是 string，这里提供格式化后的只读字段，避免 XAML 里做转换。
    public string TotalText => Total.ToString();
    public string ProcessedText => Processed.ToString();
    public string SucceededText => Succeeded.ToString();
    public string FailedText => Failed.ToString();
    public string SkippedText => Skipped.ToString();

    private void NotifyStatTexts()
    {
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(ProcessedText));
        OnPropertyChanged(nameof(SucceededText));
        OnPropertyChanged(nameof(FailedText));
        OnPropertyChanged(nameof(SkippedText));
    }

    public Visibility PauseVisibility => IsRunning && !IsPaused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeVisibility => IsPaused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => IsRunning || IsPaused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StartVisibility => IsRunning || IsPaused ? Visibility.Collapsed : Visibility.Visible;

    partial void OnIsRunningChanged(bool value) => RaiseButtonVisibilities();
    partial void OnIsPausedChanged(bool value) => RaiseButtonVisibilities();

    private void RaiseButtonVisibilities()
    {
        OnPropertyChanged(nameof(PauseVisibility));
        OnPropertyChanged(nameof(ResumeVisibility));
        OnPropertyChanged(nameof(CancelVisibility));
        OnPropertyChanged(nameof(StartVisibility));
        StartButtonText = IsPaused ? "继续" : "开始分类";
    }

    /// <summary>页面每次进入时刷新：未配置 Key 就显示引导。</summary>
    public void RefreshGuidance()
    {
        var ai = AppServices.Settings?.Current?.Ai;
        var hasKey = ai?.HasApiKey == true;
        var dryRun = ai?.DryRun == true;
        ApiReadyVisibility = hasKey || dryRun ? Visibility.Visible : Visibility.Collapsed;
        GuidanceVisibility = hasKey || dryRun ? Visibility.Collapsed : Visibility.Visible;
        ApiHintText = BuildApiHint(ai);
        GuidanceText = "还没有配置视觉模型：去「设置 → AI 接入」填 BaseUrl / API Key / 模型名，或打开 DryRun 先跑通流程。";
        Concurrency = Math.Clamp(ai?.Concurrency ?? 3, 1, 16);
    }

    private static string BuildApiHint(AiSettings? ai)
    {
        if (ai is null) return "";
        if (ai.DryRun) return "DryRun 已开启：不会发起网络请求，用于验证批处理流程。";
        if (!ai.HasApiKey) return "";
        return $"当前模型：{ai.VisionModel} · {(string.IsNullOrWhiteSpace(ai.BaseUrl) ? "BaseUrl 未填写" : ai.BaseUrl)}";
    }

    // ==================================================================================
    //  开始 / 暂停 / 取消
    // ==================================================================================
    public async Task StartOrResumeAsync()
    {
        if (IsRunning) return;
        _runVersion++;
        var version = _runVersion;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsPaused = false;
        IsRunning = true;
        IsDone = false;
        ProgressVisibility = Visibility.Visible;
        SummaryVisibility = Visibility.Collapsed;
        if (_updated.Count == 0) ClearCounters();

        try
        {
            await SaveOptionsAsync();

            var assets = await LoadTargetsAsync(ct);
            Total = assets.Count;
            if (Total == 0)
            {
                ProgressText = "没有需要处理的素材（可能全部已分类）。";
                IsRunning = false;
                return;
            }

            _stopwatch = Stopwatch.StartNew();
            ProgressText = $"准备分类 {Total} 张…";

            var options = new AiBatchOptions
            {
                MaxItems = Math.Max(1, Total),
                OverwriteManual = OverwriteManual,
                OnlyUnclassified = ScopeId == "unclassified",
                ApplyKeywords = ApplyKeywords,
                ApplyQuality = ApplyQuality,
                MinConfidenceToApply = 0.35,
            };

            var progress = new Progress<AiBatchProgress>(p => OnBatchProgress(p, version));
            var result = await AppServices.Ai.RunBatchAsync(assets, options, progress, ct);

            if (version != _runVersion) return;

            _stopwatch?.Stop();
            if (result.Updated.Count > 0)
            {
                _updated.AddRange(result.Updated);
                try { await AppServices.Store.UpsertManyAsync(result.Updated); }
                catch (Exception ex) { Log.Warn("AI 结果写库失败", ex); }

                foreach (var asset in result.Updated) AppEvents.RaiseAssetUpdated(asset);
                AppEvents.RaiseLibraryContentChanged();
            }

            FinishRun(result.Progress);
        }
        catch (OperationCanceledException)
        {
            if (version == _runVersion)
            {
                IsPaused = true;
                ProgressText = $"已暂停：已完成 {Processed}/{Total}（可点「继续」接着跑）";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("AI 批量分类失败", ex);
            ProgressText = $"批量分类失败：{ex.Message}";
            AppEvents.SetError($"AI 批量分类失败：{ex.Message}");
            _stopwatch?.Stop();
        }
        finally
        {
            if (version == _runVersion) IsRunning = false;
        }
    }

    public void Pause()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        IsPaused = true;
    }

    public void Cancel()
    {
        _runVersion++;
        _cts?.Cancel();
        IsRunning = false;
        IsPaused = false;
        IsDone = false;
        _stopwatch?.Stop();
        ClearCounters();
        ProgressText = "已取消";
        ProgressVisibility = Visibility.Collapsed;
        SummaryVisibility = Visibility.Collapsed;
        _updated.Clear();
        ThemeBars.Clear();
        HasUpdated = false;
        UpdatedText = "";
    }

    private void ClearCounters()
    {
        Total = 0;
        Processed = 0;
        Succeeded = 0;
        Failed = 0;
        Skipped = 0;
        ProgressPercent = 0;
        CurrentFile = "";
        Logs.Clear();
        NotifyStatTexts();
    }

    private void ClearResult()
    {
        ThemeBars.Clear();
        SummaryVisibility = Visibility.Collapsed;
        _updated.Clear();
        HasUpdated = false;
        UpdatedText = "";
    }

    private async Task SaveOptionsAsync()
    {
        try
        {
            var ai = AppServices.Settings.Current.Ai;
            ai.Concurrency = Math.Clamp(Concurrency, 1, 16);
            await AppServices.Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("保存批量选项失败", ex);
        }
    }

    private async Task<IReadOnlyList<MediaAsset>> LoadTargetsAsync(CancellationToken ct)
    {
        var limit = Math.Clamp(MaxItems, 1, MaxScanItems);
        switch (ScopeId)
        {
            case "selected":
            {
                var items = LibraryViewModel.Shared.SelectedItems;
                ScopeSummary = $"已选中 {items.Count} 张";
                return items.Select(i => i.Asset).Take(limit).ToList();
            }
            case "filtered":
            {
                var query = LibraryViewModel.Shared.BuildQuery(0, limit);
                var list = await AppServices.Store.QueryAsync(query, ct);
                ScopeSummary = $"当前筛选结果（取前 {list.Count} 张）";
                return list;
            }
            case "all":
            {
                var query = new AssetQuery { Limit = limit, SortBy = AssetSortKeys.CapturedAt, Descending = true };
                var list = await AppServices.Store.QueryAsync(query, ct);
                ScopeSummary = $"全部素材（取前 {list.Count} 张）";
                return list;
            }
            default:
            {
                var query = new AssetQuery { OnlyUnclassified = true, Limit = limit, SortBy = AssetSortKeys.IndexedAt, Descending = true };
                var list = await AppServices.Store.QueryAsync(query, ct);
                ScopeSummary = $"未分类素材（{list.Count} 张）";
                return list;
            }
        }
    }

    private void OnBatchProgress(AiBatchProgress p, int version)
    {
        if (version != _runVersion) return;
        if (p.Total > 0) Total = p.Total;
        Processed = p.Processed;
        Succeeded = p.Succeeded;
        Failed = p.Failed;
        Skipped = p.Skipped;
        ProgressPercent = Total > 0 ? Math.Clamp(p.Processed * 100.0 / Total, 0, 100) : 0;
        CurrentFile = p.CurrentFile ?? "";
        ProgressText = $"{(p.Done ? "已完成" : "处理中")} {p.Processed}/{Total} · 成功 {p.Succeeded} · 失败 {p.Failed}";
        NotifyStatTexts();

        var suggestion = p.LastSuggestion;
        var isError = suggestion is not null && !suggestion.Success;
        var summary = suggestion is null
            ? (string.IsNullOrWhiteSpace(p.Message) ? "—" : p.Message!)
            : suggestion.Success
                ? $"{Formatting.ThemeName(suggestion.Theme)} · {Formatting.ToneName(suggestion.Tone)} · 置信度 {suggestion.Confidence * 100:0}%"
                : $"失败：{suggestion.Error}";

        if (!string.IsNullOrWhiteSpace(p.CurrentFile) || suggestion is not null)
        {
            Logs.Insert(0, new AiQueueLogEntry(DateTimeOffset.Now, p.CurrentFile ?? p.LastAssetId ?? "", summary, isError));
            while (Logs.Count > MaxLogs) Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private void FinishRun(AiBatchProgress progress)
    {
        IsRunning = false;
        IsPaused = false;
        IsDone = true;
        ProgressPercent = 100;
        ProgressText = "批量分类完成";
        NotifyStatTexts();

        var successRate = Total > 0 ? Succeeded * 100.0 / Total : 0;
        SuccessRateText = $"{successRate:0.#}%";
        if (_stopwatch is { } sw && Succeeded > 0)
            AverageText = $"{sw.Elapsed.TotalMilliseconds / Succeeded:0} ms/张";
        else
            AverageText = "—";

        HasUpdated = _updated.Count > 0;
        UpdatedText = _updated.Count > 0
            ? $"本次更新 {_updated.Count} 张素材（写入索引库）"
            : "没有素材被修改（可能都被人工结果保护，或模型未返回可用分类）";

        BuildThemeBars();
        SummaryVisibility = Visibility.Visible;
        AppEvents.SetStatus($"AI 批量分类完成：成功 {Succeeded}，失败 {Failed}，跳过 {Skipped}");
    }

    private void BuildThemeBars()
    {
        ThemeBars.Clear();
        var counts = _updated
            .Where(a => !string.IsNullOrWhiteSpace(a.Theme))
            .GroupBy(a => a.Theme!)
            .Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(8)
            .ToList();
        if (counts.Count == 0) return;

        var max = counts.Max(x => x.Count);
        foreach (var (id, count) in counts)
        {
            var width = Math.Max(8, 200.0 * count / max);
            ThemeBars.Add(new ThemeBar(Vocabularies.ThemeDisplay(id, "zh-CN"), count, width));
        }
    }

    /// <summary>把本批次改过的素材写回 XMP 边车。</summary>
    public async Task<int> WriteXmpAsync()
    {
        if (_updated.Count == 0) return 0;
        var ok = 0;
        foreach (var asset in _updated)
        {
            try
            {
                if (await AppServices.Xmp.WriteSidecarAsync(asset)) ok++;
            }
            catch (Exception ex)
            {
                Log.Warn($"写 XMP 失败 {asset.FileName}", ex);
            }
        }
        AppEvents.SetStatus($"已写入 {ok}/{_updated.Count} 个 XMP 边车");
        return ok;
    }
}