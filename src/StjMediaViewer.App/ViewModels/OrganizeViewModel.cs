using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using StjMediaViewer.Core.Write;
using Microsoft.UI.Xaml;

namespace StjMediaViewer.App.ViewModels;

/// <summary>计划预览里的一行。</summary>
public sealed record OrganizePreviewRow(
    string Source,
    string Target,
    string Category,
    string? Note,
    bool IsSidecar);

public sealed record OrganizeSourceOption(string Title, string Id, string Detail, bool IsCloud);

/// <summary>整理页：按设备/年份/城市等分类，把磁盘上（或云端）的文件真正移动进对应文件夹。</summary>
public sealed partial class OrganizeViewModel : ObservableObject
{
    private readonly OrganizerService _organizer = AppServices.Organizer;

    public ObservableCollection<OrganizeSourceOption> Sources { get; } = new();
    public ObservableCollection<string> DimensionChoices { get; } = new();
    public ObservableCollection<string> SecondaryChoices { get; } = new();
    public ObservableCollection<OrganizePreviewRow> PreviewRows { get; } = new();

    private OrganizePlan? _localPlan;
    private CloudOrganizePlan? _cloudPlan;

    public OrganizeViewModel()
    {
        DimensionChoices.Add("不分类");
        SecondaryChoices.Add("不分类");
        foreach (var dimension in OrganizeDimensions.All)
        {
            var label = dimension.Display();
            DimensionChoices.Add(label);
            SecondaryChoices.Add(label);
        }

        SelectedPrimaryIndex = 1;   // 默认「拍摄设备」
        SelectedSourceIndex = 0;
    }

    // ---------------- 来源 ----------------
    [ObservableProperty] private int _selectedSourceIndex;
    [ObservableProperty] private bool _isCloudSource;
    [ObservableProperty] private string _sourceDetail = "";

    public Visibility LocalOptionsVisibility => IsCloudSource ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CloudOptionsVisibility => IsCloudSource ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsCloudSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalOptionsVisibility));
        OnPropertyChanged(nameof(CloudOptionsVisibility));
        PreviewRows.Clear();
        ClearPlans();
        Status = "";
    }

    partial void OnSelectedSourceIndexChanged(int value)
    {
        if (value < 0 || value >= Sources.Count) return;
        var option = Sources[value];
        IsCloudSource = option.IsCloud;
        SourceDetail = option.Detail;
        if (!option.IsCloud) RootPath = option.Detail;
        PreviewRows.Clear();
        ClearPlans();
    }

    // ---------------- 分类规则 ----------------
    [ObservableProperty] private int _selectedPrimaryIndex = 1;
    [ObservableProperty] private int _selectedSecondaryIndex;
    [ObservableProperty] private string _customDimension = "";
    [ObservableProperty] private string _rootPath = "";
    [ObservableProperty] private int _selectedConflictIndex;

    /// <summary>「细分位置」的聚类半径（米）：范围内的算一个文件夹。</summary>
    [ObservableProperty] private double _geoRadiusMeters = 500;

    private static readonly double[] GeoRadiusChoices = { 200, 500, 1000, 2000, 5000 };

    public int GeoRadiusIndex
    {
        get
        {
            var index = Array.FindIndex(GeoRadiusChoices, r => Math.Abs(r - GeoRadiusMeters) < 1);
            return index < 0 ? 1 : index;
        }
        set
        {
            if (value < 0 || value >= GeoRadiusChoices.Length) return;
            GeoRadiusMeters = GeoRadiusChoices[value];
            OnPropertyChanged();
        }
    }

    public Visibility CustomDimensionVisibility =>
        SelectedPrimaryIndex > 0 && DimensionAt(SelectedPrimaryIndex) == OrganizeDimension.CustomCategory ||
        SelectedSecondaryIndex > 0 && DimensionAt(SelectedSecondaryIndex) == OrganizeDimension.CustomCategory
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility GeoOptionsVisibility =>
        (SelectedPrimaryIndex > 0 && DimensionAt(SelectedPrimaryIndex) == OrganizeDimension.GeoArea) ||
        (SelectedSecondaryIndex > 0 && DimensionAt(SelectedSecondaryIndex) == OrganizeDimension.GeoArea)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private static OrganizeDimension DimensionAt(int index) => OrganizeDimensions.All[index - 1];

    partial void OnSelectedPrimaryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CustomDimensionVisibility));
        OnPropertyChanged(nameof(GeoOptionsVisibility));
    }

    partial void OnSelectedSecondaryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CustomDimensionVisibility));
        OnPropertyChanged(nameof(GeoOptionsVisibility));
    }

    // ---------------- 状态 ----------------
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _progressPercent;

    public Visibility PreviewVisibility => PreviewRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningVisibility => string.IsNullOrEmpty(WarningText) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsWarningOpen => !string.IsNullOrEmpty(WarningText);
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UndoVisibility => _organizer.CanUndo ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshRowState()
    {
        OnPropertyChanged(nameof(PreviewVisibility));
        OnPropertyChanged(nameof(WarningVisibility));
        OnPropertyChanged(nameof(IsWarningOpen));
        OnPropertyChanged(nameof(BusyVisibility));
        OnPropertyChanged(nameof(UndoVisibility));
    }

    private void ClearPlans()
    {
        _localPlan = null;
        _cloudPlan = null;
        PreviewRows.Clear();
        Summary = "";
        WarningText = "";
        RefreshRowState();
    }

    // ---------------- 载入来源 ----------------
    public async Task InitializeAsync()
    {
        try
        {
            Sources.Clear();
            var libraries = await AppServices.Library.GetLibrariesAsync();
            foreach (var library in libraries)
                Sources.Add(new OrganizeSourceOption(library.Name, library.Id, library.Path, IsCloud: false));

            foreach (var target in AppServices.Settings.Current.CloudTargets)
                Sources.Add(new OrganizeSourceOption($"☁ {target.Name}", target.Id,
                    $"{target.DisplayKind} · {target.LocalFolderPath}", IsCloud: true));

            if (Sources.Count == 0)
                Sources.Add(new OrganizeSourceOption("（还没有媒体库）", "", "", IsCloud: false));

            SelectedSourceIndex = 0;
            var first = Sources[0];
            SourceDetail = first.Detail;
            if (!first.IsCloud) RootPath = first.Detail;
        }
        catch (Exception ex)
        {
            Status = "载入来源失败：" + ex.Message;
        }
    }

    // ---------------- 计划 ----------------
    private OrganizeOptions BuildOptions() => new()
    {
        Primary = SelectedPrimaryIndex > 0 ? DimensionAt(SelectedPrimaryIndex) : OrganizeDimension.Device,
        Secondary = SelectedSecondaryIndex > 0 ? DimensionAt(SelectedSecondaryIndex) : null,
        CustomDimensionName = string.IsNullOrWhiteSpace(CustomDimension) ? null : CustomDimension.Trim(),
        RootPath = IsCloudSource ? null : (string.IsNullOrWhiteSpace(RootPath) ? null : RootPath.Trim()),
        GeoRadiusMeters = GeoRadiusMeters > 0 ? GeoRadiusMeters : 500,
        DeviceAliases = AppServices.Settings.Current.DeviceAliases,
        GeoPlaceResolver = ResolveGeoPlace,
        ConflictPolicy = SelectedConflictIndex switch
        {
            1 => OrganizeConflictPolicy.Skip,
            2 => OrganizeConflictPolicy.Overwrite,
            _ => OrganizeConflictPolicy.Rename,
        },
    };

    /// <summary>GPS → 「城市-区县」（离线城市库；不可用时用坐标命名）。</summary>
    private static string? ResolveGeoPlace(double latitude, double longitude)
    {
        try
        {
            var place = AppServices.Geocoder?.Reverse(latitude, longitude);
            if (place is null) return null;
            var city = place.City ?? place.Province ?? place.Country;
            if (string.IsNullOrWhiteSpace(city)) return null;
            var district = string.IsNullOrWhiteSpace(place.District) ? null : place.District!.Trim();
            return district is null ? city!.Trim() : $"{city!.Trim()}-{district}";
        }
        catch
        {
            return null;
        }
    }

    private bool NeedsCustom(OrganizeOptions options) =>
        options.Primary == OrganizeDimension.CustomCategory || options.Secondary == OrganizeDimension.CustomCategory;

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        RefreshRowState();
        try
        {
            Status = "正在计算整理计划…";
            var options = BuildOptions();
            if (NeedsCustom(options) && string.IsNullOrWhiteSpace(options.CustomDimensionName))
            {
                Status = "选了「自定义类别」但没填维度名（例如：项目 / 客户 / 场地）";
                return;
            }

            if (IsCloudSource)
            {
                await PreviewCloudAsync(options);
            }
            else
            {
                await PreviewLocalAsync(options);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("生成整理计划失败", ex);
            Status = "生成计划失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshRowState();
        }
    }

    private async Task PreviewLocalAsync(OrganizeOptions options)
    {
        var source = Sources[SelectedSourceIndex];
        var query = new AssetQuery { Limit = 0 };
        if (!string.IsNullOrEmpty(source.Id)) query.LibraryId = source.Id;
        var assets = await AppServices.Store.QueryAsync(query);

        var roots = await AppServices.Library.GetLibrariesAsync();
        var rootMap = roots.ToDictionary(r => r.Id, r => r.Path, StringComparer.Ordinal);

        if (NeedsCustom(options) && options.CustomDimensionName is not null)
        {
            var values = await AppServices.Store.GetCustomFieldsBulkAsync(
                options.CustomDimensionName, assets.Select(a => a.Id));
            options.CustomValuesByAssetId = values;
        }

        var plan = _organizer.BuildLocalPlan(assets, rootMap, options);
        _localPlan = plan;
        FillRows(plan.Entries.Select(e => new OrganizePreviewRow(
            e.SourcePath, e.TargetPath, e.CategoryPath,
            e.Note ?? (e.IsSidecar ? "边车" : null), e.IsSidecar)));

        Summary = $"{plan.FileCount} 个文件 · {FormatSize(plan.TotalBytes)}" +
                  (plan.SidecarCount > 0 ? $" + {plan.SidecarCount} 个边车" : "") +
                  (plan.ConflictCount > 0 ? $" · {plan.ConflictCount} 个冲突" : "");

        WarningText = plan.Warnings.Count == 0
            ? ""
            : string.Join("；", plan.Warnings.Take(5)) + (plan.Warnings.Count > 5 ? $" …（共 {plan.Warnings.Count} 条）" : "");
        Status = plan.IsEmpty ? "没有需要移动的文件（可能已经整理过了）" : "计划已生成，确认后点「执行整理」";
    }

    private async Task PreviewCloudAsync(OrganizeOptions options)
    {
        var source = Sources[SelectedSourceIndex];
        var target = AppServices.Settings.Current.CloudTargets.FirstOrDefault(t => t.Id == source.Id);
        if (target is null)
        {
            Status = "找不到这个云同步目标（可能已被删除）";
            return;
        }

        var catalog = await AppServices.Store.QueryAsync(new AssetQuery { Limit = 0 });
        if (NeedsCustom(options) && options.CustomDimensionName is not null)
        {
            var values = await AppServices.Store.GetCustomFieldsBulkAsync(
                options.CustomDimensionName, catalog.Select(a => a.Id));
            options.CustomValuesByAssetId = values;
        }

        Status = "正在列出云端文件…";
        var plan = await _organizer.BuildCloudPlanAsync(target, catalog, options);
        _cloudPlan = plan;
        FillRows(plan.Entries.Select(e => new OrganizePreviewRow(
            "☁ " + e.RelativeFrom, "☁ " + e.RelativeTo, e.CategoryPath, e.Note, false)));

        Summary = $"{plan.FileCount} 个云端文件 · {FormatSize(plan.TotalBytes)}" +
                  (plan.ConflictCount > 0 ? $" · {plan.ConflictCount} 个冲突" : "");
        WarningText = plan.Warnings.Count == 0 ? "" : string.Join("；", plan.Warnings.Take(5));
        Status = plan.IsEmpty ? "云端没有需要移动的文件" : "云端计划已生成，确认后点「执行整理」";
    }

    private void FillRows(IEnumerable<OrganizePreviewRow> rows)
    {
        PreviewRows.Clear();
        foreach (var row in rows.Take(2000)) PreviewRows.Add(row);
        RefreshRowState();
    }

    // ---------------- 执行 ----------------
    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (IsBusy) return;
        if (_localPlan is null && _cloudPlan is null)
        {
            Status = "先点「预览计划」看看会移动哪些文件";
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        RefreshRowState();
        try
        {
            var options = BuildOptions();
            var progress = new Progress<int>(percent => ProgressPercent = percent);
            OrganizeResult result;

            if (IsCloudSource && _cloudPlan is not null)
            {
                var source = Sources[SelectedSourceIndex];
                var target = AppServices.Settings.Current.CloudTargets.FirstOrDefault(t => t.Id == source.Id);
                if (target is null)
                {
                    Status = "找不到这个云同步目标";
                    return;
                }
                result = await _organizer.ExecuteCloudAsync(target, _cloudPlan, options, progress);
            }
            else if (_localPlan is not null)
            {
                result = await _organizer.ExecuteLocalAsync(_localPlan, AppServices.Store, options, progress);
                AppEvents.RaiseLibraryContentChanged();
            }
            else
            {
                Status = "计划已失效，请重新预览";
                return;
            }

            Summary = result.Summary;
            Status = result.Failed == 0 ? "整理完成" : "整理完成（有失败项，见下方明细）";
            if (result.Errors.Count > 0)
                WarningText = string.Join("；", result.Errors.Take(5));
            PreviewRows.Clear();
            _localPlan = null;
            _cloudPlan = null;
            RefreshRowState();
            await AppServices.Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("执行整理失败", ex);
            Status = "执行失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshRowState();
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        RefreshRowState();
        try
        {
            var record = _organizer.ReadUndo();
            if (record is null)
            {
                Status = "没有可撤销的整理记录";
                return;
            }

            OrganizeResult result = record.Kind == "cloud"
                ? await _organizer.UndoCloudAsync(
                    AppServices.Settings.Current.CloudTargets.FirstOrDefault(t => t.Id == record.TargetId) ??
                    new CloudTarget(), new Progress<int>(p => ProgressPercent = p))
                : await _organizer.UndoLocalAsync(AppServices.Store, new Progress<int>(p => ProgressPercent = p));

            Summary = result.Summary;
            Status = result.Failed == 0 ? "已撤销上次整理" : "撤销完成（有失败项）";
            if (result.Errors.Count > 0) WarningText = string.Join("；", result.Errors.Take(5));
            AppEvents.RaiseLibraryContentChanged();
            RefreshRowState();
        }
        catch (Exception ex)
        {
            Log.Warn("撤销整理失败", ex);
            Status = "撤销失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshRowState();
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => bytes + " B",
    };
}