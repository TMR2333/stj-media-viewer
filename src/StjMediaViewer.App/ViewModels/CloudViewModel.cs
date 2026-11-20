using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Write;
using StjMediaViewer.Core.Settings;
using StjMediaViewer.Core.Util;

#pragma warning disable MVVMTK0045 // 未启用 AOT；字段式 [ObservableProperty] 在 WinUI 3 下等价且更易读

namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 云同步页 ViewModel：目标管理（增删改 / 测试连接）、差异预览、执行与报告、自动同步开关。
/// 所有网络与磁盘操作都走 <see cref="AppServices.Cloud"/>，本类只做状态编排。
/// </summary>
public sealed partial class CloudViewModel : ObservableObject
{
    public static CloudViewModel Shared { get; } = new();

    private CloudViewModel()
    {
        AutoText = "自动同步：未启用";
        foreach (var dim in OrganizeDimensions.All)
        {
            var label = dim.Display();
            LayoutPrimaryOptions.Add(label);
            LayoutSecondaryOptions.Add(label);
            LayoutTertiaryOptions.Add(label);
        }
        // 注意：这里不能用下方声明的静态字段 FilterableDimensions —— 静态字段初始化顺序在
        // Shared = new() 之后，构造函数里读到还是 null（早期 WatermarkViewModel 踩过同样的坑）。
        foreach (var dim in new[]
                 {
                     OrganizeDimension.Device, OrganizeDimension.City, OrganizeDimension.Year,
                     OrganizeDimension.Theme, OrganizeDimension.Tone, OrganizeDimension.Format,
                     OrganizeDimension.Kind,
                 })
        {
            FilterDimensionOptions.Add(dim.Display());
        }
    }

    // ------------------------------------------------------------------ 列表

    public ObservableCollection<CloudTargetRow> Targets { get; } = new();
    public ObservableCollection<CloudPlanRow> PlanRows { get; } = new();
    public ObservableCollection<CloudIssueRow> ReportIssues { get; } = new();

    [ObservableProperty]
    private CloudTargetRow? _selectedTarget;

    [ObservableProperty]
    private Visibility _emptyVisibility = Visibility.Collapsed;

    // ------------------------------------------------------------------ 状态条

    [ObservableProperty]
    private string _autoText = "";

    [ObservableProperty]
    private bool _isBusy;

    // ---------------- 只上传筛选出的素材（按分类 + 三级目录） ----------------

    /// <summary>可用来筛选的分类维度（和左侧筛选面板一致）。</summary>
    private static readonly OrganizeDimension[] FilterableDimensions =
    {
        OrganizeDimension.Device,
        OrganizeDimension.City,
        OrganizeDimension.Year,
        OrganizeDimension.Theme,
        OrganizeDimension.Tone,
        OrganizeDimension.Format,
        OrganizeDimension.Kind,
    };

    public ObservableCollection<string> FilterDimensionOptions { get; } =
        new() { "不限分类" };
    public ObservableCollection<string> FilterValueOptions { get; } = new() { "全部分类" };

    public ObservableCollection<string> LayoutPrimaryOptions { get; } = new() { "不分类" };
    public ObservableCollection<string> LayoutSecondaryOptions { get; } = new() { "不分类" };
    public ObservableCollection<string> LayoutTertiaryOptions { get; } = new() { "不分类" };

    [ObservableProperty] private int _filterDimensionIndex;
    [ObservableProperty] private int _filterValueIndex;
    [ObservableProperty] private int _layoutPrimaryIndex;
    [ObservableProperty] private int _layoutSecondaryIndex;
    [ObservableProperty] private int _layoutTertiaryIndex;
    [ObservableProperty] private bool _filterPickedOnly;
    [ObservableProperty] private bool _filterUploading;
    [ObservableProperty] private string _filterUploadStatus = "";

    public Microsoft.UI.Xaml.Visibility FilterProgressVisibility =>
        FilterUploading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnFilterUploadingChanged(bool value) => OnPropertyChanged(nameof(FilterProgressVisibility));

    partial void OnSelectedTargetChanged(CloudTargetRow? value) => _ = RefreshFilterValuesAsync();

    partial void OnFilterDimensionIndexChanged(int value) => _ = RefreshFilterValuesAsync();

    /// <summary>当前可选的分类值（维度 0 = 不限）。</summary>
    private List<(string Key, string Label)> _filterValues = new();

    /// <summary>并发刷新保护：只有最后一次刷新能写下拉，避免重复项。</summary>
    private int _filterRefreshVersion;

    /// <summary>按当前维度/目标库读分面，填充"分类值"下拉。</summary>
    public async Task RefreshFilterValuesAsync()
    {
        var version = Interlocked.Increment(ref _filterRefreshVersion);
        try
        {
            var values = await LoadFilterValuesCoreAsync(version);
            if (version != Volatile.Read(ref _filterRefreshVersion)) return;   // 已有更新的刷新，丢弃本次

            FilterValueOptions.Clear();
            _filterValues = values;
            FilterValueOptions.Add("全部分类");
            foreach (var (_, label) in _filterValues) FilterValueOptions.Add(label);
            FilterValueIndex = 0;
            OnPropertyChanged(nameof(FilterValueIndex));
        }
        catch (Exception ex)
        {
            Log.Warn("读取云同步筛选值失败", ex);
        }
    }

    private async Task<List<(string Key, string Label)>> LoadFilterValuesCoreAsync(int version)
    {
        if (FilterDimensionIndex <= 0 || FilterDimensionIndex > FilterableDimensions.Length)
            return new List<(string, string)>();

        var dimension = FilterableDimensions[FilterDimensionIndex - 1];
        try
        {
            var query = new AssetQuery { Limit = 0 };
            var target = SelectedTarget?.Target;
            if (target is not null && !string.IsNullOrWhiteSpace(target.LocalFolderPath))
            {
                var libraries = await AppServices.Library.GetLibrariesAsync();
                var match = libraries
                    .Where(l => !string.IsNullOrWhiteSpace(l.Path) &&
                                Path.GetFullPath(target.LocalFolderPath)
                                    .StartsWith(Path.GetFullPath(l.Path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(l => l.Path.Length)
                    .FirstOrDefault();
                query.LibraryId = match?.Id;
            }

            var facets = await AppServices.Store.GetFacetsAsync(query);
            // 分面里同一个城市可能因为空白/大小写出现两份，这里按 key 去重并去掉空值
            static List<(string Key, string Label)> Dedupe(IEnumerable<(string Key, string Label)> rows) =>
                rows.Where(r => !string.IsNullOrWhiteSpace(r.Key))
                    .GroupBy(r => r.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

            var values = Dedupe(dimension switch
            {
                OrganizeDimension.Device => facets.ByDevice
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, AppServices.DeviceAlias(kv.Key))).ToList(),
                OrganizeDimension.City => facets.ByCity
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Key)).ToList(),
                OrganizeDimension.Year => facets.ByYear
                    .OrderByDescending(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Key + " 年")).ToList(),
                OrganizeDimension.Theme => facets.ByTheme
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, Vocabularies.ThemeDisplay(kv.Key, "zh-CN"))).ToList(),
                OrganizeDimension.Tone => facets.ByTone
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, Vocabularies.ToneDisplay(kv.Key, "zh-CN"))).ToList(),
                OrganizeDimension.Format => facets.ByFormat
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Key)).ToList(),
                OrganizeDimension.Kind => facets.ByKind
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Key switch
                    {
                        "0" => "未知",
                        "1" => "图片",
                        "2" => "视频",
                        "3" => "音频",
                        _ => kv.Key,
                    })).ToList(),
                _ => new List<(string, string)>(),
            });

            return values;
        }
        catch (Exception ex)
        {
            Log.Warn("读取云同步筛选值失败", ex);
            return new List<(string, string)>();
        }
    }

    /// <summary>只把"符合分类筛选"的素材 + 边车上传到当前同步目标；可按三级目录重建云端路径。</summary>
    public async Task UploadFilteredAsync()
    {
        if (FilterUploading) return;
        var row = SelectedTarget ?? Targets.FirstOrDefault();
        if (row is null)
        {
            FilterUploadStatus = "先添加 / 选择一个同步目标";
            return;
        }

        var query = new AssetQuery { Limit = 0, Offset = 0 };

        // 分类筛选
        if (FilterDimensionIndex > 0 && FilterDimensionIndex <= FilterableDimensions.Length &&
            FilterValueIndex > 0 && FilterValueIndex <= _filterValues.Count)
        {
            var dimension = FilterableDimensions[FilterDimensionIndex - 1];
            var value = _filterValues[FilterValueIndex - 1].Key;
            switch (dimension)
            {
                case OrganizeDimension.Device: query.Devices.Add(value); break;
                case OrganizeDimension.City: query.Cities.Add(value); break;
                case OrganizeDimension.Theme: query.Themes.Add(value); break;
                case OrganizeDimension.Tone: query.Tones.Add(value); break;
                case OrganizeDimension.Format: query.Formats.Add(value); break;
                case OrganizeDimension.Kind:
                    if (int.TryParse(value, out var kind)) query.Kinds.Add((MediaKind)kind);
                    break;
                case OrganizeDimension.Year:
                    if (int.TryParse(value, out var year))
                    {
                        query.CapturedFrom = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
                        query.CapturedTo = query.CapturedFrom.Value.AddYears(1).AddTicks(-1);
                    }
                    break;
            }
        }

        if (FilterPickedOnly) query.IsPick = true;

        // 云端目录：选了维度就按分类重建
        var layout = new List<OrganizeDimension>();
        void AddLayout(int index, IReadOnlyList<string> options)
        {
            if (index <= 0 || index >= options.Count) return;
            var name = options[index];
            var dim = OrganizeDimensions.All.FirstOrDefault(d => d.Display() == name);
            if (OrganizeDimensions.All.Contains(dim)) layout.Add(dim);
        }
        AddLayout(LayoutPrimaryIndex, LayoutPrimaryOptions);
        AddLayout(LayoutSecondaryIndex, LayoutSecondaryOptions);
        AddLayout(LayoutTertiaryIndex, LayoutTertiaryOptions);

        if (AppServices.Cloud is CloudSyncService svc)
            svc.AliasLookup = AppServices.Settings.Current.DeviceAliases;

        FilterUploading = true;
        FilterUploadStatus = "正在统计素材…";
        try
        {
            var progress = new Progress<CloudSyncProgress>(p =>
            {
                FilterUploadStatus = p.FilesTotal > 0
                    ? $"{p.Phase}（{p.FilesProcessed}/{p.FilesTotal}）"
                    : p.Phase;
            });
            var report = await AppServices.Cloud.UploadFilteredAsync(
                row.Target, query, progress, layout.Count > 0 ? layout : null);
            FilterUploadStatus = report.Summary +
                                 (report.Messages.Count > 0 ? $" · {string.Join("；", report.Messages.Take(3))}" : "");
            if (report.Uploaded > 0) AppEvents.SetStatus($"已按分类上传 {report.Uploaded} 个文件");
        }
        catch (Exception ex)
        {
            FilterUploadStatus = "按分类上传失败：" + ex.Message;
            Log.Warn("按分类上传失败", ex);
        }
        finally
        {
            FilterUploading = false;
        }
    }

    /// <summary>XAML 里给按钮绑 IsEnabled 用（IsBusy 的反相）。</summary>
    public bool IsNotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _autoSyncStatus = "";

    // ------------------------------------------------------------------ 编辑器

    [ObservableProperty]
    private Visibility _editorVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _editorTitle = "添加同步目标";

    [ObservableProperty] private bool _editorGroupEnabled;

    /// <summary>群组协作里的自己的用户名（勾选群组后必填）。</summary>
    [ObservableProperty] private string _editorAuthorName = "";

    public Visibility EditorAuthorVisibility => EditorGroupEnabled ? Visibility.Visible : Visibility.Collapsed;

    partial void OnEditorGroupEnabledChanged(bool value) => OnPropertyChanged(nameof(EditorAuthorVisibility));

    [ObservableProperty]
    private string _editorName = "";

    [ObservableProperty]
    private string _editorKind = "localfolder";

    [ObservableProperty]
    private string _editorLocalFolder = "";

    [ObservableProperty]
    private string _editorRemoteFolder = "";

    [ObservableProperty]
    private string _editorEndpoint = "";

    [ObservableProperty]
    private string _editorUsername = "";

    [ObservableProperty]
    private string _editorPassword = "";

    [ObservableProperty]
    private string _editorSubPath = "";

    [ObservableProperty]
    private string _editorDirection = "bidirectional";

    [ObservableProperty]
    private string _editorConflict = "newer";

    [ObservableProperty]
    private double _editorPollMinutes = 5;

    [ObservableProperty]
    private bool _editorAutoSync;

    [ObservableProperty]
    private bool _editorVerify = true;

    [ObservableProperty]
    private string _editorError = "";

    [ObservableProperty]
    private Visibility _editorErrorVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _localFolderFieldsVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _webDavFieldsVisibility = Visibility.Collapsed;

    private string? _editingId;

    // ------------------------------------------------------------------ 计划 / 进度 / 报告

    [ObservableProperty]
    private Visibility _planVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _planTitle = "";

    [ObservableProperty]
    private string _planSummary = "";

    [ObservableProperty]
    private Visibility _progressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressPhase = "";

    [ObservableProperty]
    private string _progressFile = "";

    [ObservableProperty]
    private string _progressCounts = "";

    [ObservableProperty]
    private Visibility _reportVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _reportSummary = "";

    [ObservableProperty]
    private string _reportDetail = "";

    [ObservableProperty]
    private bool _reportSuccess = true;

    [ObservableProperty]
    private Visibility _reportIssuesVisibility = Visibility.Collapsed;

    private CancellationTokenSource? _cts;
    private CloudSyncPlan? _lastPlan;

    // ==================================================================================
    //  初始化 / 刷新
    // ==================================================================================

    public async Task InitializeAsync()
    {
        try
        {
            await AppServices.Settings.LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("云同步：读取设置失败", ex);
        }
        RefreshTargets();
        RefreshAutoStatus();
        AppServices.CloudAuto.StatusChanged += OnAutoStatusChanged;
        AppServices.CloudAuto.SyncCompleted += OnAutoSyncCompleted;
    }

    private void OnAutoStatusChanged(object? sender, EventArgs e) => RefreshAutoStatus();

    private void OnAutoSyncCompleted(object? sender, CloudSyncReport report)
    {
        RefreshTargets();
        StatusText = $"自动同步：{report.TargetName} · {report.Summary}";
    }

    public void RefreshAutoStatus()
    {
        var auto = AppServices.CloudAuto;
        AutoText = auto.IsRunning ? "自动同步：运行中" : "自动同步：已停止";
        AutoSyncStatus = auto.StatusText;
    }

    public void RefreshTargets()
    {
        var selectedId = SelectedTarget?.Target.Id;
        Targets.Clear();
        foreach (var target in AppServices.Settings.Current.CloudTargets)
            Targets.Add(new CloudTargetRow(target));

        EmptyVisibility = Targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (selectedId is not null)
            SelectedTarget = Targets.FirstOrDefault(t => t.Target.Id == selectedId);
        else
            SelectedTarget ??= Targets.FirstOrDefault();
    }

    // ==================================================================================
    //  编辑器
    // ==================================================================================

    [RelayCommand]
    private void AddTarget()
    {
        _editingId = null;
        EditorTitle = "添加同步目标";
        EditorName = "我的云盘";
        EditorGroupEnabled = false;
        EditorAuthorName = "";
        EditorKind = "localfolder";
        EditorLocalFolder = AppServices.Settings.Current.Libraries.FirstOrDefault()?.Path ?? "";
        EditorRemoteFolder = "";
        EditorEndpoint = "";
        EditorUsername = "";
        EditorPassword = "";
        EditorSubPath = "";
        EditorDirection = "bidirectional";
        EditorConflict = "newer";
        EditorPollMinutes = 5;
        EditorAutoSync = false;
        EditorVerify = true;
        EditorErrorVisibility = Visibility.Collapsed;
        EditorVisibility = Visibility.Visible;
        UpdateKindVisibility();
    }

    [RelayCommand]
    private void EditTarget(CloudTargetRow? row)
    {
        if (row is null) return;
        var t = row.Target;
        _editingId = t.Id;
        EditorTitle = "编辑同步目标";
        EditorName = t.Name;
        EditorKind = t.Kind == CloudProviderKind.WebDav ? "webdav" : "localfolder";
        EditorGroupEnabled = t.GroupEnabled;
        EditorAuthorName = t.AuthorName ?? "";
        EditorLocalFolder = t.LocalFolderPath;
        EditorRemoteFolder = t.RemoteFolderPath ?? "";
        EditorEndpoint = t.WebDavEndpoint ?? "";
        EditorUsername = t.WebDavUsername ?? "";
        EditorPassword = "";
        EditorSubPath = t.RemoteSubPath ?? "";
        EditorDirection = t.Direction switch
        {
            CloudSyncDirection.UploadOnly => "upload",
            CloudSyncDirection.DownloadOnly => "download",
            _ => "bidirectional",
        };
        EditorConflict = t.ConflictPolicy switch
        {
            CloudConflictPolicy.PreferLocal => "local",
            CloudConflictPolicy.PreferRemote => "remote",
            CloudConflictPolicy.KeepBoth => "both",
            _ => "newer",
        };
        EditorPollMinutes = Math.Max(1, t.PollSeconds / 60.0);
        EditorAutoSync = t.AutoSync;
        EditorVerify = t.VerifyAfterTransfer;
        EditorErrorVisibility = Visibility.Collapsed;
        EditorVisibility = Visibility.Visible;
        UpdateKindVisibility();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditorVisibility = Visibility.Collapsed;
        _editingId = null;
    }

    [RelayCommand]
    private void SetKind(string? kind)
    {
        EditorKind = kind == "webdav" ? "webdav" : "localfolder";
        UpdateKindVisibility();
    }

    private void UpdateKindVisibility()
    {
        var isWebDav = EditorKind == "webdav";
        WebDavFieldsVisibility = isWebDav ? Visibility.Visible : Visibility.Collapsed;
        LocalFolderFieldsVisibility = isWebDav ? Visibility.Collapsed : Visibility.Visible;
    }

    [RelayCommand]
    private void ApplyPreset(string? preset)
    {
        switch (preset)
        {
            case "nutstore":
                EditorEndpoint = "https://dav.jianguoyun.com/dav/";
                EditorName = "坚果云";
                EditorSubPath = "stj-media-viewer";
                break;
            case "nextcloud":
                EditorEndpoint = "https://example.com/remote.php/dav/files/用户名/";
                EditorName = "Nextcloud";
                break;
            case "synology":
                EditorEndpoint = "https://192.168.1.2:5006/dav";
                EditorName = "群晖 NAS";
                break;
            case "pan123":
                EditorEndpoint = "https://webdav.123pan.cn/webdav";
                EditorName = "123 云盘";
                EditorSubPath = "stj-media-viewer";
                break;
        }
        EditorKind = "webdav";
        UpdateKindVisibility();
    }

    private bool TryBuildTarget(out CloudTarget target, out string error)
    {
        target = new CloudTarget();
        error = "";

        if (string.IsNullOrWhiteSpace(EditorName)) { error = "请填写名称"; return false; }
        if (string.IsNullOrWhiteSpace(EditorLocalFolder)) { error = "请选择本地同步目录"; return false; }

        target.Id = _editingId ?? Guid.NewGuid().ToString("N")[..12];
        target.Name = EditorName.Trim();
        target.GroupEnabled = EditorGroupEnabled;
        target.AuthorName = EditorGroupEnabled ? EditorAuthorName.Trim() : null;
        target.Enabled = true;
        target.LocalFolderPath = EditorLocalFolder.Trim();
        target.Kind = EditorKind == "webdav" ? CloudProviderKind.WebDav : CloudProviderKind.LocalFolder;
        target.Direction = EditorDirection switch
        {
            "upload" => CloudSyncDirection.UploadOnly,
            "download" => CloudSyncDirection.DownloadOnly,
            _ => CloudSyncDirection.Bidirectional,
        };
        target.ConflictPolicy = EditorConflict switch
        {
            "local" => CloudConflictPolicy.PreferLocal,
            "remote" => CloudConflictPolicy.PreferRemote,
            "both" => CloudConflictPolicy.KeepBoth,
            _ => CloudConflictPolicy.PreferNewer,
        };
        target.AutoSync = EditorAutoSync;
        target.PollSeconds = Math.Max(60, (int)Math.Round(EditorPollMinutes * 60));
        target.VerifyAfterTransfer = EditorVerify;
        target.Extensions = new List<string>();

        if (target.Kind == CloudProviderKind.WebDav)
        {
            if (string.IsNullOrWhiteSpace(EditorEndpoint)) { error = "请填写 WebDAV 服务器地址"; return false; }
            if (!Uri.TryCreate(EditorEndpoint.Trim(), UriKind.Absolute, out _)) { error = "WebDAV 地址不是合法 URL"; return false; }
            target.WebDavEndpoint = EditorEndpoint.Trim();
            target.WebDavUsername = EditorUsername.Trim();
            target.RemoteSubPath = EditorSubPath.Trim();
            if (!string.IsNullOrEmpty(EditorPassword))
                target.WebDavPasswordProtected = AppServices.Secrets.Protect(EditorPassword);

            // 编辑时密码留空 = 保留原密码
            if (string.IsNullOrEmpty(EditorPassword) && _editingId is not null)
            {
                var existing = AppServices.Settings.Current.CloudTargets.FirstOrDefault(t => t.Id == _editingId);
                target.WebDavPasswordProtected = existing?.WebDavPasswordProtected;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(EditorRemoteFolder)) { error = "请选择云端挂载目录"; return false; }
            target.RemoteFolderPath = EditorRemoteFolder.Trim();
        }

        return true;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!TryBuildTarget(out var target, out var error))
        {
            SetEditorError(error);
            return;
        }
        EditorErrorVisibility = Visibility.Collapsed;
        IsBusy = true;
        StatusText = "正在测试连接…";
        try
        {
            await using var storage = AppServices.Cloud.CreateStorage(target);
            var ok = await storage.TestAsync(out var testError);
            if (ok)
            {
                StatusText = "连接成功";
                EditorError = "";
                EditorErrorVisibility = Visibility.Collapsed;
            }
            else
            {
                SetEditorError(testError ?? "连接失败");
            }
        }
        catch (Exception ex)
        {
            SetEditorError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetEditorError(string message)
    {
        EditorError = message;
        EditorErrorVisibility = Visibility.Visible;
        StatusText = message;
    }

    [RelayCommand]
    private async Task SaveTargetAsync()
    {
        if (EditorGroupEnabled && string.IsNullOrWhiteSpace(EditorAuthorName))
        {
            EditorError = "已勾选群组协作，请填写群组用户名（同步时会写入作者名）";
            return;
        }
        if (!TryBuildTarget(out var target, out var error))
        {
            SetEditorError(error);
            return;
        }

        var list = AppServices.Settings.Current.CloudTargets;
        var index = list.FindIndex(t => t.Id == target.Id);
        // 保留编辑时未改动的运行状态
        if (index >= 0)
        {
            var old = list[index];
            target.LastSyncAt = old.LastSyncAt;
            target.LastSyncStatus = old.LastSyncStatus;
            target.LastSyncMessage = old.LastSyncMessage;
            target.LastUploaded = old.LastUploaded;
            target.LastDownloaded = old.LastDownloaded;
            target.LastConflicts = old.LastConflicts;
            target.LastFailed = old.LastFailed;
            list[index] = target;
        }
        else
        {
            list.Add(target);
        }

        try
        {
            await AppServices.Settings.SaveAsync();
            AppServices.CloudAuto.RefreshTargets();
            RefreshTargets();
            SelectedTarget = Targets.FirstOrDefault(t => t.Target.Id == target.Id);
            EditorVisibility = Visibility.Collapsed;
            EditorErrorVisibility = Visibility.Collapsed;
            StatusText = $"已保存「{target.Name}」";
        }
        catch (Exception ex)
        {
            SetEditorError("保存失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteTargetAsync(CloudTargetRow? row)
    {
        if (row is null) return;
        var list = AppServices.Settings.Current.CloudTargets;
        list.RemoveAll(t => t.Id == row.Target.Id);
        try
        {
            await AppServices.Settings.SaveAsync();
            AppServices.CloudAuto.RefreshTargets();
            RefreshTargets();
            StatusText = $"已删除「{row.Target.Name}」";
        }
        catch (Exception ex)
        {
            StatusText = "删除失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleAutoAsync(CloudTargetRow? row)
    {
        if (row is null) return;
        row.Target.AutoSync = row.AutoSync;
        try
        {
            await AppServices.Settings.SaveAsync();
            AppServices.CloudAuto.RefreshTargets();
            RefreshAutoStatus();
            StatusText = $"{row.Target.Name}：自动同步{(row.AutoSync ? "已开启" : "已关闭")}";
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
        }
    }

    // ==================================================================================
    //  预览 / 执行
    // ==================================================================================

    [RelayCommand]
    private async Task PreviewAsync(CloudTargetRow? row)
    {
        row ??= SelectedTarget;
        if (row is null) return;

        SelectedTarget = row;
        CancelCurrent();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        PlanVisibility = Visibility.Collapsed;
        ReportVisibility = Visibility.Collapsed;
        ProgressVisibility = Visibility.Visible;
        ProgressPercent = 0;
        ProgressPhase = "开始比对…";
        ProgressFile = "";
        ProgressCounts = "";
        StatusText = $"正在比对「{row.Target.Name}」…";

        try
        {
            var plan = await AppServices.Cloud.BuildPlanAsync(row.Target,
                new Progress<CloudSyncProgress>(p =>
                {
                    ProgressPhase = p.Phase;
                    ProgressPercent = p.Percent;
                    ProgressFile = p.CurrentFile ?? "";
                }), _cts.Token);

            _lastPlan = plan;
            PlanTitle = row.Target.Name;
            PlanSummary = plan.Summary;
            PlanRows.Clear();
            foreach (var action in plan.Actions
                         .Where(a => a.Kind is not CloudActionKind.Skip)
                         .OrderBy(a => a.Kind)
                         .ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                PlanRows.Add(new CloudPlanRow(action));
            }
            PlanVisibility = Visibility.Visible;
            StatusText = plan.IsEmpty ? "两边已一致，无需同步" : plan.Summary;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = "比对失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressVisibility = Visibility.Collapsed;
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync(CloudTargetRow? row)
    {
        row ??= SelectedTarget;
        if (row is null) return;
        await RunAsync(row, dryRun: false);
    }

    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (SelectedTarget is null) return;
        await RunAsync(SelectedTarget, dryRun: true);
    }

    private async Task RunAsync(CloudTargetRow row, bool dryRun)
    {
        SelectedTarget = row;
        CancelCurrent();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ReportVisibility = Visibility.Collapsed;
        ProgressVisibility = Visibility.Visible;
        ProgressPercent = 0;
        ProgressPhase = dryRun ? "演练中…" : "同步中…";
        ProgressFile = "";
        ProgressCounts = "";
        StatusText = $"{(dryRun ? "演练" : "同步")}「{row.Target.Name}」…";

        try
        {
            var report = await AppServices.Cloud.SyncAsync(row.Target, dryRun,
                new Progress<CloudSyncProgress>(p =>
                {
                    ProgressPhase = p.Phase;
                    ProgressPercent = p.Percent;
                    ProgressFile = p.CurrentFile ?? "";
                    ProgressCounts = p.FilesTotal > 0 ? $"文件 {p.FilesProcessed}/{p.FilesTotal}" : "";
                }), _cts.Token);

            ReportSummary = report.Summary;
            ReportDetail = report.Messages.Count == 0
                ? $"用时 {report.Elapsed.TotalSeconds:0.0}s" + (report.DryRun ? "（演练，未改动文件）" : "")
                : string.Join("；", report.Messages.Take(3));
            ReportSuccess = report.Success;
            ReportIssues.Clear();
            foreach (var item in report.Items.Where(i => !i.Ok).Take(200))
                ReportIssues.Add(new CloudIssueRow(item.RelativePath, item.Error ?? "失败"));
            ReportIssuesVisibility = ReportIssues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ReportVisibility = Visibility.Visible;
            StatusText = report.Summary;

            RefreshTargets();
            RefreshAutoStatus();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            ReportSummary = "同步失败";
            ReportDetail = ex.Message;
            ReportSuccess = false;
            ReportIssuesVisibility = Visibility.Collapsed;
            ReportVisibility = Visibility.Visible;
            StatusText = "同步失败：" + ex.Message;
            DiagnosticTitle = $"同步失败 · {row.Name}";
            DiagnosticOk = false;
            DiagnosticVisibility = Visibility.Visible;
            DiagnosticText = $"同步失败：{ex.Message}\n\n点上面目标卡片里的「连接诊断」可以逐步检查 连通 / 鉴权 / 列目录 / 上传 / 校验 / 清理。\n日志：{AppPaths.LogFilePath}";
        }
        finally
        {
            IsBusy = false;
            ProgressVisibility = Visibility.Collapsed;
        }
    }

    [RelayCommand]
    private async Task SyncAllAsync()
    {
        foreach (var row in Targets.Where(t => t.Target.Enabled).ToList())
        {
            if (_cts?.IsCancellationRequested == true) break;
            await RunAsync(row, dryRun: false);
        }
    }

    /// <summary>云同步连接诊断：逐项验证连通 / 鉴权 / 列目录 / 上传 / 校验 / 清理，结果显示在页面并写日志。</summary>
    public async Task DiagnoseAsync(CloudTargetRow row)
    {
        DiagnosticTitle = $"连接诊断 · {row.Name}（{row.KindText}）";
        DiagnosticVisibility = Visibility.Visible;
        DiagnosticOk = true;
        DiagnosticText = "诊断中…\n";
        StatusText = $"正在诊断「{row.Name}」…";

        try
        {
            var report = await AppServices.Cloud.DiagnoseAsync(row.Target,
                new Progress<string>(m => { if (_diagRunning) DiagnosticText = $"诊断中… {m}"; }));

            var sb = new System.Text.StringBuilder();
            foreach (var step in report.Steps)
                sb.AppendLine($"{(step.Ok ? "✓" : "✗")} {step.Name}  [{step.ElapsedMs}ms]  {step.Detail}");
            sb.AppendLine();
            sb.AppendLine(report.Summary);
            sb.AppendLine($"日志：{AppPaths.LogFilePath}");

            _diagRunning = false;
            DiagnosticText = sb.ToString();
            DiagnosticOk = report.Ok;
            StatusText = report.Summary;

            if (report.Ok) AppEvents.SetStatus($"「{row.Name}」连接正常");
            else AppEvents.SetError($"「{row.Name}」连接异常：{report.Summary}");
        }
        catch (Exception ex)
        {
            DiagnosticOk = false;
            DiagnosticText = $"诊断失败：{ex.Message}\n日志：{AppPaths.LogFilePath}";
            StatusText = "诊断失败：" + ex.Message;
            Log.Warn("云同步连接诊断失败", ex);
        }
    }

    public void OpenLogFile()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogFilePath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("打开日志文件失败", ex);
            AppEvents.SetError("打不开日志文件：" + ex.Message);
        }
    }

    private bool _diagRunning;

    [ObservableProperty] private string _diagnosticText = "";
    [ObservableProperty] private string _diagnosticTitle = "";
    [ObservableProperty] private bool _diagnosticOk = true;
    [ObservableProperty] private Visibility _diagnosticVisibility = Visibility.Collapsed;

    [RelayCommand]
    private void CancelCurrent()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    public void ClearPlan()
    {
        PlanRows.Clear();
        PlanVisibility = Visibility.Collapsed;
        ReportVisibility = Visibility.Collapsed;
        _lastPlan = null;
    }
}

// =====================================================================================
//  展示行
// =====================================================================================

public sealed class CloudTargetRow
{
    public CloudTargetRow(CloudTarget target) => Target = target;
    public CloudTarget Target { get; }

    public string Name => Target.Name;
    public string KindText => Target.DisplayKind;
    public string LocalPath => Target.LocalFolderPath;
    public string RemotePath => Target.RemoteDescription;
    public string DirectionText => Target.Direction switch
    {
        CloudSyncDirection.UploadOnly => "仅上传",
        CloudSyncDirection.DownloadOnly => "仅下载",
        _ => "双向",
    };
    public string ConflictText => Target.ConflictPolicy switch
    {
        CloudConflictPolicy.PreferLocal => "冲突：本地优先",
        CloudConflictPolicy.PreferRemote => "冲突：云端优先",
        CloudConflictPolicy.KeepBoth => "冲突：两边都保留",
        _ => "冲突：较新者胜",
    };
    public bool AutoSync
    {
        get => Target.AutoSync;
        set => Target.AutoSync = value;
    }
    public string LastSyncText => Target.LastSyncAt is null
        ? "尚未同步"
        : $"{Target.LastSyncAt.Value.LocalDateTime:MM-dd HH:mm} · " +
          (Target.LastSyncStatus == "ok" ? "成功" : Target.LastSyncStatus == "partial" ? "部分失败" : "失败");
    public string CountersText => Target.LastSyncAt is null
        ? ""
        : $"↑{Target.LastUploaded}  ↓{Target.LastDownloaded}  ⚠{Target.LastConflicts}  失败{Target.LastFailed}";
}

public sealed class CloudPlanRow
{
    public CloudPlanRow(CloudSyncAction action) => Action = action;
    public CloudSyncAction Action { get; }
    public string Path => Action.RelativePath;
    public string Description => Action.Description;
    public string SizeText => Action.SizeBytes >= 1024 * 1024
        ? $"{Action.SizeBytes / 1024.0 / 1024:F1} MB"
        : $"{Action.SizeBytes / 1024.0:F0} KB";
}

public sealed class CloudIssueRow
{
    public CloudIssueRow(string path, string error)
    {
        Path = path;
        Error = error;
    }
    public string Path { get; }
    public string Error { get; }
}