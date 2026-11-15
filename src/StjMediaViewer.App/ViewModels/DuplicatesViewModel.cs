using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>重复组里的一张素材。</summary>
public sealed partial class DuplicateItemViewModel : ObservableObject
{
    public DuplicateItemViewModel(MediaAsset asset, bool isKept)
    {
        Asset = asset;
        _isKept = isKept;
    }

    public MediaAsset Asset { get; }
    public string Id => Asset.Id;
    public string FileName => Asset.FileName;
    public string PathText => Asset.RelativePath ?? Asset.FullPath;
    public string SizeText => Formatting.FileSize(Asset.SizeBytes);
    public string DimensionText => Formatting.Dimensions(Asset.Width, Asset.Height);
    public string DateText => Formatting.DateTime(Asset.CapturedAt ?? Asset.FileModified);
    public string DetailText => $"{DimensionText} · {SizeText} · {DateText}";

    [ObservableProperty]
    private bool _isKept;

    /// <summary>是否勾选删除（保留项不允许勾选）。</summary>
    [ObservableProperty]
    private bool _isSelectedForDelete;

    public Visibility KeptVisibility => IsKept ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotKeptVisibility => IsKept ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DeleteCheckVisibility => IsKept ? Visibility.Collapsed : Visibility.Visible;
    public string KeepButtonText => IsKept ? "将保留" : "设为保留";

    public event EventHandler? KeepStateChanged;

    partial void OnIsKeptChanged(bool value)
    {
        OnPropertyChanged(nameof(KeptVisibility));
        OnPropertyChanged(nameof(NotKeptVisibility));
        OnPropertyChanged(nameof(DeleteCheckVisibility));
        OnPropertyChanged(nameof(KeepButtonText));
        if (value) IsSelectedForDelete = false;
        KeepStateChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>一组重复素材。</summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateGroup group, IReadOnlyList<DuplicateItemViewModel> items)
    {
        Group = group;
        Items = new ObservableCollection<DuplicateItemViewModel>(items);
        foreach (var item in Items) item.KeepStateChanged += (_, _) => RefreshState();
        RefreshState();
    }

    public DuplicateGroup Group { get; }
    public ObservableCollection<DuplicateItemViewModel> Items { get; }

    public string KindText => Group.Kind switch
    {
        DuplicateKind.Exact => "完全相同",
        DuplicateKind.SameContent => "同内容（哈希相同）",
        _ => "近重复",
    };

    public Visibility ExactVisibility => Group.Kind == DuplicateKind.Exact ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SameContentVisibility => Group.Kind == DuplicateKind.SameContent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NearVisibility => Group.Kind == DuplicateKind.NearDuplicate ? Visibility.Visible : Visibility.Collapsed;

    public string CountText => $"{Items.Count} 个文件";
    public long WastedBytes => Group.WastedBytes;
    public string WastedText => $"可释放 {Formatting.FileSize(Group.WastedBytes)}";
    public string ReasonText => string.IsNullOrWhiteSpace(Group.Reason) ? "" : Group.Reason!;
    public Visibility ReasonVisibility => string.IsNullOrWhiteSpace(Group.Reason) ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    private string _deleteSelectionText = "勾选要删除的副本";

    public int DeleteSelectionCount => Items.Count(i => i.IsSelectedForDelete);

    public void RefreshState() => DeleteSelectionText = DeleteSelectionCount == 0
        ? "勾选要删除的副本"
        : $"已勾选 {DeleteSelectionCount} 个副本";
}

/// <summary>
/// 重复检测页：调用 IDuplicateFinder 找重复 / 近重复，默认保留"最优"（分辨率 > 文件大小 > 更早），
/// 删除走回收站 + 索引库同步删除。
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    public static DuplicatesViewModel Shared { get; } = new();

    private DuplicatesViewModel()
    {
        Groups = new ObservableCollection<DuplicateGroupViewModel>();
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; }

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private double _scanPercent;

    [ObservableProperty]
    private string _scanText = "";

    [ObservableProperty]
    private Visibility _progressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private bool _includeNearDuplicates = true;

    [ObservableProperty]
    private int _groupCount;

    [ObservableProperty]
    private int _duplicateFileCount;

    [ObservableProperty]
    private long _wastedBytes;

    [ObservableProperty]
    private string _summaryText = "还没有查重结果";

    [ObservableProperty]
    private string _wastedText = "—";

    [ObservableProperty]
    private string _groupCountText = "0";

    [ObservableProperty]
    private string _fileCountText = "0";

    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private Visibility _selectionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _resultsVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _emptyVisibility = Visibility.Visible;

    [ObservableProperty]
    private bool _isDeleting;

    [ObservableProperty]
    private bool _hasScanned;

    // ==================================================================================
    //  查重
    // ==================================================================================
    public async Task FindAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ProgressVisibility = Visibility.Visible;
        ScanPercent = 0;
        ScanText = "正在读取素材清单…";
        try
        {
            var assets = await AppServices.Store.QueryAsync(new AssetQuery { Limit = 10000 });
            if (assets.Count == 0)
            {
                ClearResults();
                ScanText = "媒体库里还没有素材，先扫描一个媒体库。";
                AppEvents.SetStatus("查重：媒体库为空");
                return;
            }

            ScanText = $"正在比对 {assets.Count} 个素材…";
            var progress = new Progress<int>(p =>
            {
                ScanPercent = Math.Clamp(p, 0, 100);
                ScanText = $"正在比对 {assets.Count} 个素材 · {ScanPercent:0}%";
            });

            var groups = await AppServices.Duplicates.FindDuplicatesAsync(assets, IncludeNearDuplicates, progress);

            Groups.Clear();
            int fileCount = 0;
            long wasted = 0;
            foreach (var group in groups
                         .OrderBy(g => g.Kind)
                         .ThenByDescending(g => g.WastedBytes))
            {
                var items = BuildItems(group);
                var vm = new DuplicateGroupViewModel(group, items);
                Groups.Add(vm);
                fileCount += items.Count;
                wasted += group.WastedBytes;
            }

            GroupCount = Groups.Count;
            DuplicateFileCount = fileCount;
            WastedBytes = wasted;
            GroupCountText = Groups.Count.ToString();
            FileCountText = fileCount.ToString();
            WastedText = Formatting.FileSize(wasted);
            SummaryText = Groups.Count == 0
                ? "没有发现重复素材，素材库很干净。"
                : $"发现 {Groups.Count} 组重复，涉及 {fileCount} 个文件，最多可释放 {Formatting.FileSize(wasted)}。";
            ResultsVisibility = Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyVisibility = Groups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            HasScanned = true;
            UpdateSelectionSummary();
            AppEvents.SetStatus($"查重完成：{Groups.Count} 组 / 可释放 {Formatting.FileSize(wasted)}");
        }
        catch (Exception ex)
        {
            Log.Warn("查重失败", ex);
            ScanText = $"查重失败：{ex.Message}";
            AppEvents.SetError($"查重失败：{ex.Message}");
        }
        finally
        {
            IsScanning = false;
            ProgressVisibility = Visibility.Collapsed;
        }
    }

    /// <summary>默认保留"最优"：分辨率最高 → 文件最大 → 创建/拍摄时间更早。</summary>
    private static IReadOnlyList<DuplicateItemViewModel> BuildItems(DuplicateGroup group)
    {
        var ordered = group.Assets
            .OrderByDescending(a => (long)(a.Width ?? 0) * (a.Height ?? 0))
            .ThenByDescending(a => a.SizeBytes)
            .ThenBy(a => a.CapturedAt ?? a.FileModified)
            .ToList();
        var list = new List<DuplicateItemViewModel>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
            list.Add(new DuplicateItemViewModel(ordered[i], isKept: i == 0));
        return list;
    }

    private void ClearResults()
    {
        Groups.Clear();
        GroupCount = 0;
        DuplicateFileCount = 0;
        WastedBytes = 0;
        GroupCountText = "0";
        FileCountText = "0";
        WastedText = "—";
        SummaryText = "还没有查重结果";
        ResultsVisibility = Visibility.Collapsed;
        EmptyVisibility = Visibility.Visible;
        UpdateSelectionSummary();
    }

    // ==================================================================================
    //  保留 / 删除
    // ==================================================================================
    public void SetKeep(DuplicateGroupViewModel group, DuplicateItemViewModel target)
    {
        foreach (var item in group.Items) item.IsKept = ReferenceEquals(item, target);
        UpdateSelectionSummary();
    }

    public void UpdateSelectionSummary()
    {
        var count = Groups.Sum(g => g.DeleteSelectionCount);
        SelectionText = count > 0 ? $"已勾选 {count} 个待删除副本" : "";
        SelectionVisibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in Groups) g.RefreshState();
    }

    /// <summary>删除所有"已勾选且非保留"的副本：先回收站，再删索引。</summary>
    public async Task DeleteSelectedAsync()
    {
        if (IsDeleting) return;

        var victims = Groups
            .SelectMany(g => g.Items)
            .Where(i => i.IsSelectedForDelete && !i.IsKept)
            .ToList();
        if (victims.Count == 0)
        {
            AppEvents.SetStatus("没有勾选任何要删除的副本");
            return;
        }

        var totalBytes = victims.Sum(v => v.Asset.SizeBytes);
        var ok = await AppServices.Dialogs.ConfirmAsync(
            "删除选中的副本",
            $"将把 {victims.Count} 个文件移入回收站（约 {Formatting.FileSize(totalBytes)}），并从索引库移除。\n\n保留的文件不会被删除，此操作可在回收站撤销。",
            "移入回收站", "取消");
        if (!ok) return;

        IsDeleting = true;
        try
        {
            var assets = victims.Select(v => v.Asset).ToList();
            var deletedCount = await AppServices.Operations.DeleteToRecycleBinAsync(assets);
            // 即使文件删除失败也把索引里已不存在的记录清掉，避免"幽灵条目"。
            try { await AppServices.Store.DeleteAsync(assets.Select(a => a.Id)); }
            catch (Exception ex) { Log.Warn("删除索引记录失败", ex); }

            foreach (var group in Groups.ToList())
            {
                foreach (var victim in group.Items.Where(i => i.IsSelectedForDelete && !i.IsKept).ToList())
                    group.Items.Remove(victim);

                if (group.Items.Count <= 1)
                {
                    Groups.Remove(group);
                }
                else if (!group.Items.Any(i => i.IsKept))
                {
                    group.Items[0].IsKept = true;
                }
            }

            GroupCount = Groups.Count;
            DuplicateFileCount = Groups.Sum(g => g.Items.Count);
            WastedBytes = Groups.Sum(g => g.WastedBytes);
            GroupCountText = GroupCount.ToString();
            FileCountText = DuplicateFileCount.ToString();
            WastedText = Formatting.FileSize(WastedBytes);
            SummaryText = Groups.Count == 0
                ? "重复素材已清理完成。"
                : $"已完成清理：回收站删除 {deletedCount} 个文件，剩余 {Groups.Count} 组重复。";
            ResultsVisibility = Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyVisibility = Groups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateSelectionSummary();
            AppEvents.RaiseLibraryContentChanged();
            AppEvents.SetStatus($"重复项清理完成：移入回收站 {deletedCount} 个文件");
        }
        catch (Exception ex)
        {
            Log.Warn("删除重复文件失败", ex);
            AppEvents.SetError($"删除失败：{ex.Message}");
        }
        finally
        {
            IsDeleting = false;
        }
    }
}