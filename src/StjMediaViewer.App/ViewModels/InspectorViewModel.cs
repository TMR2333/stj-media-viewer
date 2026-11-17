using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 右侧检视面板：元数据只读展示 + 手动分类编辑 + 保存 / AI / XMP 操作。
/// 所有手动修改都会把对应维度的 Provenance 置为 <see cref="ClassSource.Manual"/>，
/// 保证"人工结果永远优先于 AI"这条铁律在 UI 层也成立。
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    /// <summary>单例：皮肤切换会重建页面，编辑状态与选中素材需要跨重建保留。</summary>
    public static InspectorViewModel Shared { get; } = new();

    private bool _suppressEdits;
    private CancellationTokenSource? _imageCts;

    private InspectorViewModel()
    {
        ThemeOptions = new ObservableCollection<OptionItem>();
        ToneOptions = new ObservableCollection<OptionItem>();
        Groups = new ObservableCollection<MetaGroup>();
        RebuildOptionLists();
    }

    // ---------------- 当前素材 ----------------
    [ObservableProperty]
    private MediaAsset? _currentAsset;

    [ObservableProperty]
    private bool _hasAsset;

    [ObservableProperty]
    private Visibility _emptyVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _contentVisibility = Visibility.Collapsed;

    /// <summary>只有视频才显示「转码」按钮。</summary>
    [ObservableProperty]
    private Visibility _transcodeVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _headerTitle = "";

    [ObservableProperty]
    private string _headerPath = "";

    [ObservableProperty]
    private string _headerMeta = "";

    // ---------------- 预览图 ----------------
    [ObservableProperty]
    private ImageSource? _previewImage;

    [ObservableProperty]
    private Visibility _imageVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _placeholderVisibility = Visibility.Visible;

    [ObservableProperty]
    private string _placeholderGlyph = "\uE91B";

    [ObservableProperty]
    private string _placeholderText = "暂无预览";

    [ObservableProperty]
    private bool _isImageLoading;

    // ---------------- 元数据 ----------------
    public ObservableCollection<MetaGroup> Groups { get; }

    // ---------------- 手动分类编辑器 ----------------
    public ObservableCollection<OptionItem> ThemeOptions { get; }

    /// <summary>多主题选择：一张图可以同时是「自然 + 夜景」。</summary>
    public ObservableCollection<ThemeChipItem> ThemeChips { get; } = new();
    public ObservableCollection<OptionItem> ToneOptions { get; }

    [ObservableProperty]
    private OptionItem? _selectedTheme;

    [ObservableProperty]
    private OptionItem? _selectedTone;

    [ObservableProperty]
    private bool? _processedValue;

    [ObservableProperty]
    private string _cityText = "";

    [ObservableProperty]
    private int _rating;

    [ObservableProperty]
    private string _colorLabel = "none";

    [ObservableProperty]
    private bool? _pickValue;

    [ObservableProperty]
    private string _aiSubject = "";

    [ObservableProperty]
    private string _aiMood = "";

    // ---------------- 状态 ----------------
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private Visibility _busyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private Visibility _statusVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _statusOkVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _statusErrorVisibility = Visibility.Collapsed;

    // ---------------- 自定义类别 ----------------
    public ObservableCollection<CustomFieldItem> CustomFields { get; } = new();
    public ObservableCollection<string> CustomDimensions { get; } = new();

    [ObservableProperty] private string _customDimension = "";
    [ObservableProperty] private string _customValueText = "";

    /// <summary>读取当前素材的自定义类别。</summary>
    public async Task LoadCustomFieldsAsync(MediaAsset asset)
    {
        try
        {
            var fields = await AppServices.Store.GetCustomFieldsAsync(asset.Id);
            CustomFields.Clear();
            foreach (var (dimension, value) in fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                CustomFields.Add(new CustomFieldItem(dimension, value));

            var dims = await AppServices.Store.ListCustomDimensionsAsync();
            CustomDimensions.Clear();
            foreach (var d in dims) CustomDimensions.Add(d);
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("读取自定义类别失败", ex);
        }
    }

    /// <summary>加一条自定义类别（同一维度覆盖）。</summary>
    public async Task AddCustomFieldAsync()
    {
        if (CurrentAsset is null) return;
        var dimension = CustomDimension.Trim();
        var value = CustomValueText.Trim();
        if (dimension.Length == 0 || value.Length == 0) return;

        try
        {
            await AppServices.Store.SetCustomFieldAsync(CurrentAsset.Id, dimension, value);
            CustomValueText = "";
            await LoadCustomFieldsAsync(CurrentAsset);
            AppEvents.RaiseAssetUpdated(CurrentAsset);
            SetStatus($"已设置 {dimension}：{value}", false);
        }
        catch (Exception ex)
        {
            SetStatus($"保存自定义类别失败：{ex.Message}", true);
        }
    }

    /// <summary>删除某个维度的类别值。</summary>
    public async Task RemoveCustomFieldAsync(string dimension)
    {
        if (CurrentAsset is null || string.IsNullOrWhiteSpace(dimension)) return;
        try
        {
            await AppServices.Store.SetCustomFieldAsync(CurrentAsset.Id, dimension, null);
            await LoadCustomFieldsAsync(CurrentAsset);
            AppEvents.RaiseAssetUpdated(CurrentAsset);
        }
        catch (Exception ex)
        {
            SetStatus($"删除自定义类别失败：{ex.Message}", true);
        }
    }

    /// <summary>保存 / AI / XMP 写入后通知外部刷新卡片。</summary>
    public event EventHandler<MediaAsset>? AssetChanged;

    // ==================================================================================
    //  展示
    // ==================================================================================
    public void Show(MediaAsset? asset)
    {
        if (CurrentAsset is not null && IsDirty && !ReferenceEquals(CurrentAsset, asset))
            _ = SaveQuietAsync();   // 切换素材前自动保存，避免丢改动

        CurrentAsset = asset;
        HasAsset = asset is not null;
        EmptyVisibility = asset is null ? Visibility.Visible : Visibility.Collapsed;
        ContentVisibility = asset is null ? Visibility.Collapsed : Visibility.Visible;
        TranscodeVisibility = asset?.IsVideo == true ? Visibility.Visible : Visibility.Collapsed;
        if (asset is null)
        {
            Groups.Clear();
            PreviewImage = null;
            return;
        }

        _suppressEdits = true;
        try
        {
            HeaderTitle = asset.FileName;
            HeaderPath = asset.RelativePath ?? asset.FullPath;
            HeaderMeta = $"{Formatting.KindName(asset.Kind)} · {asset.FormatCode} · {Formatting.Dimensions(asset.Width, asset.Height)}";
            Rating = Math.Clamp(asset.Rating, 0, 5);
            ColorLabel = string.IsNullOrWhiteSpace(asset.ColorLabel) ? "none" : asset.ColorLabel!;
            PickValue = asset.IsPick;
            ProcessedValue = asset.PostProcess switch
            {
                PostProcessState.Processed => true,
                PostProcessState.Unprocessed => false,
                _ => null,
            };
            CityText = asset.City ?? "";
            AiSubject = asset.AiSubject ?? "";
            AiMood = asset.AiMood ?? "";
            SelectedTheme = ThemeOptions.FirstOrDefault(o => string.Equals(o.Id, asset.Theme, StringComparison.OrdinalIgnoreCase));
            SyncThemeChips(asset);
            SelectedTone = ToneOptions.FirstOrDefault(o => string.Equals(o.Id, asset.Tone, StringComparison.OrdinalIgnoreCase));
            BuildGroups(asset);
            _ = LoadCustomFieldsAsync(asset);
            _ = LoadPreviewAsync(asset);
        }
        finally
        {
            _suppressEdits = false;
        }
        IsDirty = false;
        SetStatus("", false);
    }

    private async Task LoadPreviewAsync(MediaAsset asset)
    {
        _imageCts?.Cancel();
        _imageCts?.Dispose();
        _imageCts = new CancellationTokenSource();
        var ct = _imageCts.Token;

        ImageVisibility = Visibility.Collapsed;
        PlaceholderVisibility = Visibility.Visible;
        PlaceholderGlyph = Formatting.KindGlyph(asset.Kind);
        PlaceholderText = asset.IsAudio ? "音频素材" : "正在生成预览…";
        IsImageLoading = true;

        try
        {
            ImageSource? source = asset.Kind switch
            {
                MediaKind.Video => await ImageSourceLoader.LoadVideoPosterAsync(asset, 720, ct),
                MediaKind.Audio => await ImageSourceLoader.LoadAudioCoverAsync(asset, ct),
                _ => await ImageSourceLoader.LoadAsync(asset, 900, ct),
            };
            if (ct.IsCancellationRequested || !ReferenceEquals(CurrentAsset, asset)) return;

            if (source is not null)
            {
                PreviewImage = source;
                ImageVisibility = Visibility.Visible;
                PlaceholderVisibility = Visibility.Collapsed;
            }
            else
            {
                PreviewImage = null;
                PlaceholderText = asset.IsAudio ? "音频素材（封面未内嵌）" : "预览尚未生成";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"检视面板预览失败 {asset.FileName}: {ex.Message}");
            PlaceholderText = "预览加载失败";
        }
        finally
        {
            if (ReferenceEquals(CurrentAsset, asset)) IsImageLoading = false;
        }
    }

    // ==================================================================================
    //  元数据分组
    // ==================================================================================
    private void BuildGroups(MediaAsset a)
    {
        Groups.Clear();

        var file = new MetaGroup("文件");
        file.Add("文件名", a.FileName);
        file.Add("相对路径", a.RelativePath);
        file.Add("大小", Formatting.FileSize(a.SizeBytes));
        file.Add("格式", a.FormatCode, ClassDimension.Format, a);
        file.Add("类型", Formatting.KindName(a.Kind), ClassDimension.ImageKind, a);
        file.Add("修改时间", Formatting.DateTime(a.FileModified));
        file.Add("入库时间", Formatting.DateTime(a.IndexedAt));
        if (!string.IsNullOrWhiteSpace(a.LastError)) file.Add("错误", a.LastError);
        Groups.Add(file);

        var tech = new MetaGroup("技术");
        tech.Add("分辨率", Formatting.Dimensions(a.Width, a.Height));
        tech.Add("像素", Formatting.Megapixels(a.Width, a.Height));
        tech.Add("宽高比", Formatting.AspectRatio(a.Width, a.Height));
        if (a.IsImage) tech.Add("位深", a.BitDepth is null ? null : $"{a.BitDepth} bit");
        if (!a.IsImage)
        {
            tech.Add("时长", Formatting.Duration(a.DurationSeconds));
            tech.Add("帧率", Formatting.FrameRate(a.FrameRate));
            tech.Add("码率", Formatting.Bitrate(a.BitrateBps));
            tech.Add("视频编码", StjMediaViewer.Core.Transcode.VideoCodecNames.DisplayLabel(a.VideoCodec));
            tech.Add("音频编码", a.AudioCodec);
            tech.Add("声道", a.AudioChannels is null ? null : $"{a.AudioChannels} ch");
            tech.Add("采样率", a.AudioSampleRate is null ? null : $"{a.AudioSampleRate} Hz");
        }
        tech.Add("容器", a.ContainerName);
        tech.Add("MIME", a.MimeType);
        Groups.Add(tech);

        var shot = new MetaGroup("拍摄");
        shot.Add("设备", AppServices.DeviceAlias(a.DeviceKey), ClassDimension.Device, a);
        shot.Add("厂商", a.CameraMake);
        shot.Add("机型", a.CameraModel);
        shot.Add("镜头", a.LensModel);
        shot.Add("焦距", Formatting.Focal(a.FocalLengthMm));
        shot.Add("光圈", Formatting.Aperture(a.ApertureF));
        shot.Add("快门", Formatting.Shutter(a.ShutterSpeedSeconds));
        shot.Add("ISO", Formatting.Iso(a.Iso));
        shot.Add("拍摄时间", Formatting.DateTime(a.CapturedAt), ClassDimension.CapturedTime, a);
        shot.Add("时间来源", a.CapturedAtSource);
        Groups.Add(shot);

        var place = new MetaGroup("地点");
        place.Add("国家", a.Country);
        place.Add("省份", a.Province);
        place.Add("城市", a.City, ClassDimension.City, a);
        place.Add("坐标", Formatting.Coord(a.Latitude, a.Longitude));
        place.Add("海拔", a.AltitudeMeters is null ? null : $"{a.AltitudeMeters:0} m");
        place.Add("城市来源", a.CitySource);
        Groups.Add(place);

        var color = new MetaGroup("色彩");
        color.Add("色彩配置", a.ColorProfileName);
        color.Add("色域", a.ColorPrimaries);
        color.Add("传输特性", a.TransferCharacteristics);
        color.Add("色彩矩阵", a.ColorMatrix);
        color.Add("是否 Log", a.IsLog is null ? "—" : a.IsLog == true ? "是" : "否", ClassDimension.VideoLog, a);
        color.Add("Log 配置", a.LogProfile);
        color.Add("色温", a.ColorTemperatureK is null ? null : $"{a.ColorTemperatureK:0} K");
        Groups.Add(color);

        var post = new MetaGroup("后期");
        post.Add("后期状态", Formatting.PostProcessName(a.PostProcess), ClassDimension.PostProcess, a);
        post.Add("有调色编辑", a.HasDevelopEdits ? "是" : "否");
        post.Add("编辑引擎", a.DevelopEngine);
        post.Add("XMP 边车", a.XmpPath);
        post.Add("边车文件", a.Sidecars.Count > 0 ? string.Join("、", a.Sidecars) : null);
        post.Add("内嵌 XMP", a.HasEmbeddedXmp ? "是" : "否");
        Groups.Add(post);

        var cls = new MetaGroup("分类");
        cls.Add("主题", Formatting.ThemeName(a.Theme), ClassDimension.Theme, a);
        cls.Add("影调", Formatting.ToneName(a.Tone), ClassDimension.Tone, a);
        cls.Add("评分", a.Rating > 0 ? new string('\u2605', a.Rating) : null, ClassDimension.Rating, a);
        cls.Add("颜色标签", Formatting.ColorLabelName(a.ColorLabel));
        cls.Add("选用状态", Formatting.PickName(a.IsPick));
        cls.Add("质量分", Formatting.Quality(a.QualityScore), ClassDimension.Quality, a);
        cls.Add("关键词", a.Keywords.Count > 0 ? string.Join("、", a.Keywords) : null, ClassDimension.Keywords, a);
        Groups.Add(cls);

        var ai = new MetaGroup("AI");
        ai.Add("内容描述", a.AiSubject);
        ai.Add("情绪氛围", a.AiMood);
        ai.Add("AI 主题来源", a.Prov(ClassDimension.Theme)?.Source == ClassSource.Ai
            ? $"{(a.Prov(ClassDimension.Theme)!.ModelId ?? "模型")} · {a.Prov(ClassDimension.Theme)!.Confidence * 100:0}%"
            : null);
        Groups.Add(ai);

        // 密集信息面板：常用分组展开，其余折叠，避免一屏全是 "—"。
        file.IsExpanded = true;
        shot.IsExpanded = true;
        cls.IsExpanded = true;
        post.IsExpanded = true;
        tech.IsExpanded = false;
        place.IsExpanded = false;
        color.IsExpanded = false;
        ai.IsExpanded = false;

        SyncEditorLocks(a);
    }

    /// <summary>AI / 元数据来源的维度在编辑器里加提示（人工覆盖后徽标会变成"人工"）。</summary>
    private void SyncEditorLocks(MediaAsset a)
    {
        var themeSource = a.Prov(ClassDimension.Theme)?.Source ?? ClassSource.None;
        var toneSource = a.Prov(ClassDimension.Tone)?.Source ?? ClassSource.None;
        ThemeSourceHint = SourceHint(themeSource);
        ToneSourceHint = SourceHint(toneSource);
        ThemeSourceVisibility = ThemeSourceHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToneSourceVisibility = ThemeSourceHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        static string SourceHint(ClassSource s) => s switch
        {
            ClassSource.Ai => "AI 建议值，手动选择后将标记为人工",
            ClassSource.Derived => "由元数据推断，可手动改",
            ClassSource.Sidecar => "来自 XMP 边车",
            ClassSource.Manual => "人工认定",
            _ => "",
        };
    }

    [ObservableProperty]
    private string _themeSourceHint = "";

    [ObservableProperty]
    private string _toneSourceHint = "";

    [ObservableProperty]
    private Visibility _themeSourceVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _toneSourceVisibility = Visibility.Collapsed;

    private void RebuildOptionLists()
    {
        ThemeOptions.Clear();
        ThemeOptions.Add(new OptionItem("", "未分类"));
        foreach (var t in Vocabularies.Themes) ThemeOptions.Add(new OptionItem(t.Id, t.Zh));

        ThemeChips.Clear();
        foreach (var t in Vocabularies.Themes)
        {
            var chip = new ThemeChipItem(t.Id, t.Zh);
            chip.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThemeChipItem.IsSelected)) OnThemeChipToggled(chip);
            };
            ThemeChips.Add(chip);
        }

        ToneOptions.Clear();
        ToneOptions.Add(new OptionItem("", "未分类"));
        foreach (var t in Vocabularies.Tones) ToneOptions.Add(new OptionItem(t.Id, t.Zh));
    }

    // ==================================================================================
    //  编辑：每一处都写 Provenance=Manual
    // ==================================================================================
    /// <summary>主题改为多选后，主主题仍允许用下拉快速指定（会重置为单选）。</summary>
    partial void OnSelectedThemeChanged(OptionItem? value)
    {
        if (_suppressEdits || CurrentAsset is null) return;
        var theme = string.IsNullOrEmpty(value?.Id) ? null : value!.Id;
        var themes = theme is null ? new List<string>() : new List<string> { theme };
        ApplyThemes(themes);
    }

    private void SyncThemeChips(MediaAsset asset)
    {
        _suppressChipEvents = true;
        try
        {
            var selected = asset.Themes.Count > 0
                ? asset.Themes
                : (string.IsNullOrWhiteSpace(asset.Theme) ? new List<string>() : new List<string> { asset.Theme! });
            foreach (var chip in ThemeChips)
                chip.IsSelected = selected.Contains(chip.Id, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _suppressChipEvents = false;
        }
    }

    private bool _suppressChipEvents;

    private void OnThemeChipToggled(ThemeChipItem chip)
    {
        if (_suppressEdits || _suppressChipEvents || CurrentAsset is null) return;

        // 保持主题在词表里的顺序，第一个 = 主主题
        var themes = ThemeChips.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        ApplyThemes(themes);
    }

    /// <summary>写入多主题：多选时一次性写到所有选中素材。</summary>
    private void ApplyThemes(List<string> themes)
    {
        if (CurrentAsset is null) return;

        if (ApplyToMultiSelection(new BatchEditPatch { Themes = themes },
                                  a => { a.SetThemes(themes); a.Provenance[ClassDimension.Theme] = FieldProvenance.Of(ClassSource.Manual); }))
        {
            CurrentAsset.SetThemes(themes);
            CurrentAsset.Provenance[ClassDimension.Theme] = FieldProvenance.Of(ClassSource.Manual);
            _suppressEdits = true;
            try { SelectedTheme = ThemeOptions.FirstOrDefault(o => string.Equals(o.Id, CurrentAsset.Theme, StringComparison.OrdinalIgnoreCase)); }
            finally { _suppressEdits = false; }
            SyncThemeChips(CurrentAsset);
            return;
        }

        CurrentAsset.SetThemes(themes);
        CurrentAsset.Provenance[ClassDimension.Theme] = FieldProvenance.Of(ClassSource.Manual);
        SyncThemeChips(CurrentAsset);
        MarkDirty();
    }

    partial void OnSelectedToneChanged(OptionItem? value)
    {
        if (_suppressEdits || CurrentAsset is null) return;
        var tone = string.IsNullOrEmpty(value?.Id) ? null : value!.Id;
        if (ApplyToMultiSelection(new BatchEditPatch { Tone = tone ?? "" },
                                  a => { a.Tone = tone; a.Provenance[ClassDimension.Tone] = FieldProvenance.Of(ClassSource.Manual); }))
            return;
        CurrentAsset.Tone = tone;
        CurrentAsset.Provenance[ClassDimension.Tone] = FieldProvenance.Of(ClassSource.Manual);
        MarkDirty();
    }

    partial void OnProcessedValueChanged(bool? value)
    {
        if (_suppressEdits || CurrentAsset is null) return;
        var state = value switch
        {
            true => PostProcessState.Processed,
            false => PostProcessState.Unprocessed,
            _ => PostProcessState.Unknown,
        };
        if (ApplyToMultiSelection(new BatchEditPatch { PostProcess = (int)state },
                                  a => { a.PostProcess = state; a.Provenance[ClassDimension.PostProcess] = FieldProvenance.Of(ClassSource.Manual); }))
            return;
        CurrentAsset.PostProcess = state;
        CurrentAsset.Provenance[ClassDimension.PostProcess] = FieldProvenance.Of(ClassSource.Manual);
        MarkDirty();
    }

    public void SetCity(string? city)
    {
        if (CurrentAsset is null) return;
        var value = string.IsNullOrWhiteSpace(city) ? null : city!.Trim();
        if (ApplyToMultiSelection(new BatchEditPatch { City = value ?? "" },
                                  a => { a.City = value; a.CitySource = value is null ? null : "manual"; a.Provenance[ClassDimension.City] = FieldProvenance.Of(ClassSource.Manual); }))
            return;
        if (string.Equals(CurrentAsset.City, value, StringComparison.Ordinal)) return;
        CurrentAsset.City = value;
        CurrentAsset.CitySource = value is null ? null : "manual";
        CurrentAsset.Provenance[ClassDimension.City] = FieldProvenance.Of(ClassSource.Manual);
        MarkDirty();
    }

    public void SetRating(int rating)
    {
        if (CurrentAsset is null) return;
        var value = Math.Clamp(rating, 0, 5);
        if (ApplyToMultiSelection(new BatchEditPatch { Rating = value },
                                  a => a.Rating = value))
        {
            Rating = value;
            return;
        }
        if (CurrentAsset.Rating == value) return;
        CurrentAsset.Rating = value;
        Rating = value;
        CurrentAsset.Provenance[ClassDimension.Rating] = FieldProvenance.Of(ClassSource.Manual);
        MarkDirty();
    }

    public void SetColorLabel(string? label)
    {
        if (CurrentAsset is null) return;
        var value = string.IsNullOrWhiteSpace(label) || label.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : label;
        if (ApplyToMultiSelection(new BatchEditPatch { ColorLabel = value ?? "" },
                                  a => a.ColorLabel = value))
        {
            ColorLabel = value ?? "none";
            return;
        }
        if (string.Equals(CurrentAsset.ColorLabel, value, StringComparison.OrdinalIgnoreCase)) return;
        CurrentAsset.ColorLabel = value;
        ColorLabel = value ?? "none";
        MarkDirty();
    }

    public void SetPick(bool? pick)
    {
        if (CurrentAsset is null) return;
        if (ApplyToMultiSelection(new BatchEditPatch { IsPick = pick },
                                  a => a.IsPick = pick))
        {
            PickValue = pick;
            return;
        }
        CurrentAsset.IsPick = pick;
        PickValue = pick;
        MarkDirty();
    }

    /// <summary>
    /// 多选时把分类改动一次性写到**所有选中素材**（不只是检视面板里的那一张）。
    /// 返回 true 表示已按批量方式处理，调用方不要再改单张。
    /// </summary>
    private bool ApplyToMultiSelection(BatchEditPatch patch, Action<MediaAsset> applyLocal)
    {
        var targets = MultiSelectionTargets();
        if (targets.Count <= 1) return false;

        foreach (var asset in targets) applyLocal(asset);
        _ = PersistMultiAsync(targets, patch);
        return true;
    }

    private static List<MediaAsset> MultiSelectionTargets()
    {
        try
        {
            var selected = LibraryViewModel.Shared.SelectedItems;
            if (selected.Count > 1) return selected.Select(i => i.Asset).ToList();
        }
        catch
        {
            // 忽略：退化成单张编辑
        }
        return new List<MediaAsset>();
    }

    private async Task PersistMultiAsync(List<MediaAsset> targets, BatchEditPatch patch)
    {
        try
        {
            var ids = targets.Select(a => a.Id).ToList();
            var affected = await AppServices.Store.ApplyBatchEditAsync(ids, patch);
            foreach (var asset in targets) AppServices.Thumbs.Invalidate(asset.Id);
            IsDirty = false;
            SetStatus($"已应用到 {affected} 张素材", false);
            foreach (var asset in targets) AppEvents.RaiseAssetUpdated(asset);
            if (CurrentAsset is not null) BuildGroups(CurrentAsset);
            LibraryViewModel.Shared.RequestRefresh();
            Log.Info($"[inspector] 多选分类写入 {affected} 张：{patch.Describe()}");
        }
        catch (Exception ex)
        {
            Log.Warn("多选分类写入失败", ex);
            SetStatus($"多选写入失败：{ex.Message}", true);
        }
    }

    private void MarkDirty()
    {
        IsDirty = true;
        SetStatus("有未保存的修改", false);
        // 分类维度变了：立即重建元数据（徽标要马上反映"人工"）。
        if (CurrentAsset is not null) BuildGroups(CurrentAsset);
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText = message;
        var has = !string.IsNullOrEmpty(message);
        StatusVisibility = has ? Visibility.Visible : Visibility.Collapsed;
        StatusOkVisibility = has && !isError ? Visibility.Visible : Visibility.Collapsed;
        StatusErrorVisibility = has && isError ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================================
    //  操作
    // ==================================================================================
    public async Task SaveAsync()
    {
        if (CurrentAsset is null) return;
        IsBusy = true;
        BusyVisibility = Visibility.Visible;
        try
        {
            await AppServices.Store.UpsertAsync(CurrentAsset);
            IsDirty = false;
            SetStatus("已保存", false);
            AssetChanged?.Invoke(this, CurrentAsset);
            AppEvents.RaiseAssetUpdated(CurrentAsset);
        }
        catch (Exception ex)
        {
            Log.Warn("保存素材失败", ex);
            SetStatus($"保存失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyVisibility = Visibility.Collapsed;
        }
    }

    private async Task SaveQuietAsync()
    {
        if (CurrentAsset is null || !IsDirty) return;
        try
        {
            await AppServices.Store.UpsertAsync(CurrentAsset);
            IsDirty = false;
            AppEvents.RaiseAssetUpdated(CurrentAsset);
        }
        catch (Exception ex)
        {
            Log.Warn("自动保存失败", ex);
        }
    }

    public async Task AnalyzeAsync()
    {
        if (CurrentAsset is null) return;
        IsBusy = true;
        BusyVisibility = Visibility.Visible;
        SetStatus("AI 正在分析这张素材…", false);
        try
        {
            var suggestion = await AppServices.Ai.AnalyzeAssetAsync(CurrentAsset);
            if (!suggestion.Success)
            {
                SetStatus($"AI 分类不可用：{suggestion.Error}", true);
                return;
            }

            ApplySuggestion(CurrentAsset, suggestion);
            await AppServices.Store.UpsertAsync(CurrentAsset);
            IsDirty = false;
            BuildGroups(CurrentAsset);
            _suppressEdits = true;
            try
            {
                SelectedTheme = ThemeOptions.FirstOrDefault(o => string.Equals(o.Id, CurrentAsset.Theme, StringComparison.OrdinalIgnoreCase));
                SyncThemeChips(CurrentAsset);
                SelectedTone = ToneOptions.FirstOrDefault(o => string.Equals(o.Id, CurrentAsset.Tone, StringComparison.OrdinalIgnoreCase));
                AiSubject = CurrentAsset.AiSubject ?? "";
                AiMood = CurrentAsset.AiMood ?? "";
            }
            finally
            {
                _suppressEdits = false;
            }

            SetStatus($"AI 分类完成：{Formatting.ThemeName(CurrentAsset.Theme)} · {Formatting.ToneName(CurrentAsset.Tone)}", false);
            AssetChanged?.Invoke(this, CurrentAsset);
            AppEvents.RaiseAssetUpdated(CurrentAsset);
        }
        catch (Exception ex)
        {
            Log.Warn("AI 分类失败", ex);
            SetStatus($"AI 分类失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyVisibility = Visibility.Collapsed;
        }
    }

    /// <summary>把 AI 建议写回素材；人工认定的维度保留不动。</summary>
    internal static void ApplySuggestion(MediaAsset asset, AiSuggestion s)
    {
        if (!string.IsNullOrWhiteSpace(s.Theme) && asset.Prov(ClassDimension.Theme)?.Source != ClassSource.Manual)
        {
            asset.Theme = Vocabularies.NormalizeTheme(s.Theme) ?? s.Theme;
            asset.Provenance[ClassDimension.Theme] = FieldProvenance.Of(ClassSource.Ai, s.Confidence, s.ModelId);
        }
        if (!string.IsNullOrWhiteSpace(s.Tone) && asset.Prov(ClassDimension.Tone)?.Source != ClassSource.Manual)
        {
            asset.Tone = Vocabularies.NormalizeTone(s.Tone) ?? s.Tone;
            asset.Provenance[ClassDimension.Tone] = FieldProvenance.Of(ClassSource.Ai, s.Confidence, s.ModelId);
        }
        if (s.Keywords.Count > 0)
        {
            foreach (var k in s.Keywords)
                if (!asset.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) asset.Keywords.Add(k);
            asset.Provenance[ClassDimension.Keywords] = FieldProvenance.Of(ClassSource.Ai, s.Confidence, s.ModelId);
        }
        if (!string.IsNullOrWhiteSpace(s.Subject)) asset.AiSubject = s.Subject;
        if (!string.IsNullOrWhiteSpace(s.Mood)) asset.AiMood = s.Mood;
        if (s.QualityScore is not null)
        {
            asset.QualityScore = s.QualityScore;
            asset.Provenance[ClassDimension.Quality] = FieldProvenance.Of(ClassSource.Ai, s.Confidence, s.ModelId);
        }
        if (s.IsLog is not null && asset.Prov(ClassDimension.VideoLog)?.Source != ClassSource.Manual)
        {
            asset.IsLog = s.IsLog;
            asset.Provenance[ClassDimension.VideoLog] = FieldProvenance.Of(ClassSource.Ai, s.Confidence, s.ModelId);
        }
    }

    public async Task WriteXmpAsync()
    {
        if (CurrentAsset is null) return;
        IsBusy = true;
        BusyVisibility = Visibility.Visible;
        try
        {
            var ok = await AppServices.Xmp.WriteSidecarAsync(CurrentAsset);
            SetStatus(ok ? "XMP 边车已写入" : "XMP 边车未写入（当前环境不支持写盘）", !ok);
        }
        catch (Exception ex)
        {
            Log.Warn("写入 XMP 失败", ex);
            SetStatus($"写入 XMP 失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyVisibility = Visibility.Collapsed;
        }
    }

    /// <summary>右键菜单等外部入口直接改值（评分 / 色标 / 选用），立即落库。</summary>
    public async Task ApplyQuickEditAsync(MediaAsset asset, Action<MediaAsset> mutate)
    {
        mutate(asset);
        try
        {
            await AppServices.Store.UpsertAsync(asset);
            AppEvents.RaiseAssetUpdated(asset);
            if (ReferenceEquals(CurrentAsset, asset)) Show(asset);
        }
        catch (Exception ex)
        {
            Log.Warn("快速修改失败", ex);
            AppEvents.SetError($"修改失败：{ex.Message}");
        }
    }
}

/// <summary>检视面板里的一行自定义类别。</summary>
public sealed record CustomFieldItem(string Dimension, string Value);

/// <summary>主题多选的一个小 chip（自然 / 夜景 / 风光 …）。</summary>
public sealed partial class ThemeChipItem : ObservableObject
{
    public ThemeChipItem(string id, string zh)
    {
        Id = id;
        Zh = zh;
    }

    public string Id { get; }
    public string Zh { get; }

    [ObservableProperty] private bool _isSelected;
}
