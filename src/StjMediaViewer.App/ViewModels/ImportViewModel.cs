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
/// 一张设备卡片的展示模型：把 <see cref="StorageDevice"/> 变成可绑定的文字 / 比例 / 可见性。
/// 只做展示换算，不发 IO（设备枚举都在 <see cref="ImportViewModel"/> 里异步做）。
/// </summary>
public sealed class ImportDeviceCard
{
    public ImportDeviceCard(StorageDevice device)
    {
        Device = device;

        var letter = device.RootPath.TrimEnd('\\', '/');
        TitleText = string.IsNullOrWhiteSpace(device.VolumeLabel) ? $"磁盘 {letter}" : device.VolumeLabel!;
        RootPathText = device.RootPath;

        // 类型标签：「可移动 / USB / 固定 / 网络」——总线类型比驱动器类型更能说明"这是什么卡"。
        var bus = device.BusType;
        var isExternalBus = bus is "USB" or "SD" or "MMC" or "1394";
        TypeText = isExternalBus ? bus! : device.DriveType;
        BusHintText = !string.IsNullOrWhiteSpace(bus) && bus != TypeText && bus != "内置系统盘" ? bus! : "";
        BusHintVisibility = string.IsNullOrWhiteSpace(BusHintText) ? Visibility.Collapsed : Visibility.Visible;

        ReadyVisibility = device.IsReady ? Visibility.Visible : Visibility.Collapsed;
        NotReadyVisibility = device.IsReady ? Visibility.Collapsed : Visibility.Visible;
        ScanEnabled = device.IsReady;

        CapacityText = !device.IsReady
            ? "介质未就绪"
            : device.TotalBytes > 0
                ? $"已用 {Formatting.FileSize(device.TotalBytes - device.FreeBytes)} / 共 {Formatting.FileSize(device.TotalBytes)}"
                : "容量信息不可用";
        UsedPercent = Math.Clamp(device.UsedRatio * 100.0, 0, 100);
        FreeText = device.IsReady && device.FreeBytes > 0 ? $"剩余 {Formatting.FileSize(device.FreeBytes)}" : "";

        CameraText = string.IsNullOrWhiteSpace(device.CameraHint) ? "" : $"相机卡：{device.CameraHint}";
        CameraVisibility = string.IsNullOrWhiteSpace(CameraText) ? Visibility.Collapsed : Visibility.Visible;
        SourceText = device.IsExternal ? "外部设备" : "本机磁盘";
    }

    public StorageDevice Device { get; }

    /// <summary>卷标大字（没有卷标时用"磁盘 X:"）。</summary>
    public string TitleText { get; }

    /// <summary>盘符 / 根路径。</summary>
    public string RootPathText { get; }

    /// <summary>类型标签文字。</summary>
    public string TypeText { get; }

    public string BusHintText { get; }
    public Visibility BusHintVisibility { get; }

    public string CapacityText { get; }
    public double UsedPercent { get; }
    public string FreeText { get; }
    public string SourceText { get; }

    public string CameraText { get; }
    public Visibility CameraVisibility { get; }

    public Visibility ReadyVisibility { get; }
    public Visibility NotReadyVisibility { get; }
    public bool ScanEnabled { get; }

    /// <summary>扫描时优先用相机卡特征目录（DCIM/xxx），否则用盘根。</summary>
    public string ScanRoot => string.IsNullOrWhiteSpace(Device.CameraSourcePath)
        ? Device.RootPath
        : Device.CameraSourcePath!;
}

/// <summary>
/// 导入计划里的一行：一组 JPG + RAW（+ 边车）。缩略图异步补，勾选状态双向绑定。
/// </summary>
public sealed partial class ImportGroupRow : ObservableObject
{
    private ImageSource? _thumbnail;

    public ImportGroupRow(ImportGroup group, long sizeBytes)
    {
        Group = group;
        SizeBytes = sizeBytes;
        _isSelected = group.IsSelected;

        var fileName = Path.GetFileName(group.JpegPath ?? group.RawPath ?? group.GroupKey);
        FileNameText = string.IsNullOrWhiteSpace(fileName) ? group.GroupKey : fileName;
        CapturedText = group.CapturedAt is null ? "拍摄时间未知" : Formatting.DateTime(group.CapturedAt);
        DetailText = $"{group.FileCount} 个文件 · {Formatting.FileSize(sizeBytes)}";

        PairVisibility = group.HasRawJpegPair ? Visibility.Visible : Visibility.Collapsed;
        RawOnlyVisibility = !group.HasRawJpegPair && !string.IsNullOrWhiteSpace(group.RawPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var camera = group.CameraHint ?? "";
        CameraText = string.IsNullOrWhiteSpace(camera) ? "" : camera;
        CameraVisibility = string.IsNullOrWhiteSpace(CameraText) ? Visibility.Collapsed : Visibility.Visible;

        // RAW 扩展名给一个更具体的提示（CR3 / ARW / NEF …）
        RawText = string.IsNullOrWhiteSpace(group.RawPath)
            ? ""
            : Path.GetExtension(group.RawPath).TrimStart('.').ToUpperInvariant();
        RawTextVisibility = string.IsNullOrWhiteSpace(RawText) ? Visibility.Collapsed : Visibility.Visible;
    }

    public ImportGroup Group { get; }
    public long SizeBytes { get; }
    public string FileNameText { get; }
    public string CapturedText { get; }
    public string DetailText { get; }
    public string CameraText { get; }
    public Visibility CameraVisibility { get; }
    public string RawText { get; }
    public Visibility RawTextVisibility { get; }

    /// <summary>「RAW&amp;JPEG」成对徽标。</summary>
    public Visibility PairVisibility { get; }

    /// <summary>只有 RAW 单张时的 RAW 徽标。</summary>
    public Visibility RawOnlyVisibility { get; }

    public string? JpegPath => Group.JpegPath;
    public int FileCount => Group.FileCount;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>JPG 缩略图（JPEG 可直接解码；RAW 单张没有缩略图，显示胶片字形占位）。</summary>
    public ImageSource? Thumbnail => _thumbnail;

    public Visibility ThumbnailVisibility => _thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public void SetThumbnail(ImageSource? image)
    {
        _thumbnail = image;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(ThumbnailVisibility));
        OnPropertyChanged(nameof(PlaceholderVisibility));
    }
}

/// <summary>导入报告里的一个失败 / 异常文件行。</summary>
public sealed class ImportReportItemRow
{
    public ImportReportItemRow(ImportItemResult item, string fileName, string statusText, string verifyText)
    {
        FileNameText = fileName;
        StatusText = statusText;
        VerifyText = verifyText;
        DetailText = string.IsNullOrWhiteSpace(item.Error)
            ? item.DestinationPath ?? item.SourcePath
            : $"{item.SourcePath} — {item.Error}";
    }

    public string FileNameText { get; }
    public string StatusText { get; }
    public string VerifyText { get; }
    public string DetailText { get; }
}

/// <summary>目录核验报告里的一条问题项。</summary>
public sealed class VerifyIssueRow
{
    public VerifyIssueRow(string fileName, string statusText, string detail)
    {
        FileNameText = fileName;
        StatusText = statusText;
        DetailText = detail;
    }

    public string FileNameText { get; }
    public string StatusText { get; }
    public string DetailText { get; }
}

/// <summary>
/// 导入页：外部设备列表 → 导入计划（JPG 预览 + RAW 配对）→ 执行（可选核验）→ 报告，
/// 末尾再挂一个独立的"导出核验"（任意两棵目录树逐文件哈希对账）。
///
/// 所有耗时操作都走 async + CancellationToken；进度回调用 <see cref="Progress{T}"/>
/// 创建于 UI 线程，因此自动切回 UI 线程，VM 里不做任何阻塞式 IO。
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private const int MaxReportRows = 200;

    public static ImportViewModel Shared { get; } = new();

    private readonly List<ImportGroupRow> _rows = new();
    private ImportPlan? _plan;
    private string _lastPlanSource = "";
    private bool _initialized;
    private bool _suppressRowSync;

    private CancellationTokenSource? _planCts;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _verifyCts;

    private ImportViewModel()
    {
        Devices = new ObservableCollection<ImportDeviceCard>();
        PlanGroups = new ObservableCollection<ImportGroupRow>();
        ReportFailedItems = new ObservableCollection<ImportReportItemRow>();
        VerifyIssues = new ObservableCollection<VerifyIssueRow>();
    }

    public ObservableCollection<ImportDeviceCard> Devices { get; }
    public ObservableCollection<ImportGroupRow> PlanGroups { get; }
    public ObservableCollection<ImportReportItemRow> ReportFailedItems { get; }
    public ObservableCollection<VerifyIssueRow> VerifyIssues { get; }

    // ==================================================================================
    //  ① 设备区
    // ==================================================================================
    [ObservableProperty]
    private bool _isLoadingDevices;

    [ObservableProperty]
    private Visibility _deviceRingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _deviceEmptyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _deviceListVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _deviceSummaryText = "正在读取存储设备…";

    [ObservableProperty]
    private string _deviceEmptyDescription = "插入 U 盘 / SD 卡 / 相机后点「刷新」；也可以直接用下面的「浏览任意文件夹…」从任意目录导入。";

    /// <summary>首次进入页面：默认目标目录取第一个媒体库根，然后枚举设备。</summary>
    public async Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            try
            {
                var libraries = await AppServices.Library.GetLibrariesAsync();
                if (libraries.Count > 0 && string.IsNullOrWhiteSpace(DestinationRoot))
                {
                    DestinationRoot = libraries[0].Path;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[import] 读取媒体库根失败", ex);
            }

            if (string.IsNullOrWhiteSpace(DestinationRoot))
            {
                try { DestinationRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); }
                catch { /* 极端环境下拿不到"图片"目录，留空由用户选择 */ }
            }

            if (string.IsNullOrWhiteSpace(VerifyDestinationRoot)) VerifyDestinationRoot = DestinationRoot;
        }

        await RefreshDevicesAsync();

        // 页面被 Frame 重建后（离开再回来）补一次缩略图：Shared 里的计划还在，图片是懒加载的。
        if (_rows.Count > 0 && _thumbnailCts is null) StartThumbnailLoad();
    }

    /// <summary>刷新设备：先毫秒级快枚举上屏，再后台补总线 / DCIM 线索后原地替换。</summary>
    public async Task RefreshDevicesAsync()
    {
        if (IsLoadingDevices) return;
        IsLoadingDevices = true;
        SetDeviceVisibility();
        try
        {
            var quick = await Task.Run(() => AppServices.Devices.GetDevices());
            ApplyDevices(quick);
            UpdateDeviceSummary(probed: false);

            var full = await AppServices.Devices.ProbeDevicesAsync();
            ApplyDevices(full);
            UpdateDeviceSummary(probed: true);
            AppEvents.SetStatus($"存储设备：发现 {Devices.Count} 个（其中外部设备 {Devices.Count(d => d.Device.IsExternal)} 个）");
        }
        catch (Exception ex)
        {
            Log.Warn("[import] 枚举存储设备失败", ex);
            DeviceSummaryText = $"枚举存储设备失败：{ex.Message}";
            AppEvents.SetError($"枚举存储设备失败：{ex.Message}");
        }
        finally
        {
            IsLoadingDevices = false;
            SetDeviceVisibility();
        }
    }

    private void ApplyDevices(IReadOnlyList<StorageDevice> devices)
    {
        // 系统盘（Windows 所在卷）不当作"导入源"列出来：误点会把整块系统盘当素材库扫，
        // 需要从本机目录导入时走下面的「浏览任意文件夹…」。
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "";

        Devices.Clear();
        foreach (var device in devices)
        {
            if (!string.IsNullOrEmpty(systemRoot) &&
                string.Equals(device.RootPath.TrimEnd('\\'), systemRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Devices.Add(new ImportDeviceCard(device));
        }
    }

    private void UpdateDeviceSummary(bool probed)
    {
        var external = Devices.Count(d => d.Device.IsExternal);
        DeviceSummaryText = Devices.Count == 0
            ? "没有发现可用的存储设备"
            : $"发现 {Devices.Count} 个存储设备（外部设备 {external} 个）{(probed ? " · 已探测总线与相机目录" : " · 正在探测总线…")}";
    }

    private void SetDeviceVisibility()
    {
        DeviceRingVisibility = IsLoadingDevices ? Visibility.Visible : Visibility.Collapsed;
        DeviceListVisibility = Devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceEmptyVisibility = Devices.Count == 0 && !IsLoadingDevices ? Visibility.Visible : Visibility.Collapsed;
    }

    partial void OnIsLoadingDevicesChanged(bool value) => SetDeviceVisibility();

    // ==================================================================================
    //  ② 导入计划
    // ==================================================================================
    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private bool _isScanningPlan;

    [ObservableProperty]
    private double _planPercent;

    [ObservableProperty]
    private string _planStatusText = "";

    [ObservableProperty]
    private Visibility _planProgressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _planVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _planGroupsVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _planEmptyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _planSourceText = "";

    [ObservableProperty]
    private string _planStatText = "";

    [ObservableProperty]
    private string _selectedCountText = "";

    [ObservableProperty]
    private string _duplicateHintText = "";

    [ObservableProperty]
    private Visibility _duplicateHintVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _warningsText = "";

    [ObservableProperty]
    private Visibility _warningsVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _planEmptyTitle = "源目录里没有可导入的照片";

    [ObservableProperty]
    private string _planEmptyDescription = "支持 JPG / JPEG 作为预览，RAW（CR2/CR3/NEF/ARW/DNG…）会自动按同名配对；也可以换成相机卡里的 DCIM 子目录再试。";

    /// <summary>扫描任意目录并产出导入计划。</summary>
    public async Task BuildPlanForPathAsync(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)) return;
        // 导入执行中不允许换源；扫描中允许换源（直接取消上一次扫描）。
        if (IsImporting) return;

        _planCts?.Cancel();
        _planCts?.Dispose();
        var cts = new CancellationTokenSource();
        _planCts = cts;

        ClearPlan();
        IsScanningPlan = true;
        PlanVisibility = Visibility.Visible;
        PlanProgressVisibility = Visibility.Visible;
        PlanPercent = 0;
        PlanStatusText = $"正在扫描 {sourceRoot} …";
        PlanSourceText = sourceRoot;
        WarningsVisibility = Visibility.Collapsed;
        try
        {
            var options = BuildOptions();
            var progress = new Progress<ScanProgress>(p =>
            {
                PlanPercent = Math.Clamp(p.Percent, 0, 100);
                var phase = p.Phase switch
                {
                    ScanPhase.Preparing => "准备中",
                    ScanPhase.Enumerating => "枚举文件",
                    ScanPhase.ReadingMetadata => "读取元数据",
                    ScanPhase.Hashing => "计算哈希",
                    ScanPhase.Indexing => "建立索引",
                    ScanPhase.Thumbnails => "生成缩略图",
                    ScanPhase.Completed => "完成",
                    ScanPhase.Cancelled => "已取消",
                    ScanPhase.Failed => "失败",
                    _ => "处理中",
                };
                var current = string.IsNullOrEmpty(p.CurrentPath) ? "" : Path.GetFileName(p.CurrentPath);
                PlanStatusText = p.Total > 0
                    ? $"{phase} · 已处理 {p.Processed}/{p.Total} · {current}"
                    : $"{phase} · 已发现 {p.MediaFound} 个媒体文件";
            });

            var plan = await AppServices.Import.BuildPlanAsync(sourceRoot, options, progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            await ApplyPlanAsync(plan, cts.Token);
            AppEvents.SetStatus($"导入计划就绪：{_rows.Count} 组 / {plan.TotalFiles} 个文件 / {Formatting.FileSize(plan.TotalBytes)}");
        }
        catch (OperationCanceledException)
        {
            PlanStatusText = "扫描已取消";
            if (!HasPlan)
            {
                // 还没有可用计划时直接收起计划区，避免留下一个只有一行状态文字的空卡片。
                PlanVisibility = Visibility.Collapsed;
                PlanProgressVisibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[import] 生成导入计划失败", ex);
            PlanStatusText = $"扫描失败：{ex.Message}";
            PlanEmptyTitle = "扫描失败";
            PlanEmptyDescription = ex.Message;
            PlanEmptyVisibility = Visibility.Visible;
            AppEvents.SetError($"生成导入计划失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_planCts, cts))
            {
                IsScanningPlan = false;
                PlanProgressVisibility = Visibility.Collapsed;
            }

            cts.Dispose();
            if (ReferenceEquals(_planCts, cts)) _planCts = null;
        }
    }

    /// <summary>把计划落到 UI：先在后台汇总每组字节数（不在 UI 线程做 FileInfo），再建行、补缩略图。</summary>
    private async Task ApplyPlanAsync(ImportPlan plan, CancellationToken ct)
    {
        _plan = plan;
        _lastPlanSource = plan.SourceRoot;
        PlanSourceText = string.IsNullOrWhiteSpace(plan.SourceRoot) ? _lastPlanSource : plan.SourceRoot;

        var groups = plan.Groups;
        var sizes = await Task.Run(() =>
        {
            var result = new long[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                long total = 0;
                foreach (var path in groups[i].AllFiles())
                {
                    try { total += new FileInfo(path).Length; }
                    catch { /* 读不到大小就按 0 计，不影响导入 */ }
                }
                result[i] = total;
            }
            return result;
        }, ct);
        ct.ThrowIfCancellationRequested();

        ClearPlanRows();
        for (int i = 0; i < groups.Count; i++)
        {
            var row = new ImportGroupRow(groups[i], sizes[i]);
            row.PropertyChanged += OnRowPropertyChanged;
            _rows.Add(row);
            PlanGroups.Add(row);
        }

        HasPlan = true;
        PlanVisibility = Visibility.Visible;
        PlanGroupsVisibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanEmptyVisibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count == 0)
        {
            PlanEmptyTitle = "源目录里没有可导入的照片";
            PlanEmptyDescription = plan.Warnings.Count > 0
                ? string.Join(" ", plan.Warnings.Take(2))
                : "支持 JPG / JPEG 作为预览，RAW（CR2/CR3/NEF/ARW/DNG…）会自动按同名配对；换成相机卡里的 DCIM 子目录再试。";
        }

        EstimatedDuplicatesText(plan.EstimatedDuplicates);
        SetWarnings(plan.Warnings);
        UpdatePlanSummary();
        StartThumbnailLoad();
    }

    private void EstimatedDuplicatesText(int duplicates)
    {
        DuplicateHintText = duplicates > 0 ? $"预计跳过 {duplicates} 个重复文件（目标目录已存在同名同大小文件）" : "";
        DuplicateHintVisibility = duplicates > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            WarningsText = "";
            WarningsVisibility = Visibility.Collapsed;
            return;
        }

        WarningsText = string.Join("\n", warnings.Take(4).Select(w => $"· {w}")) +
                       (warnings.Count > 4 ? $"\n· …共 {warnings.Count} 条提示" : "");
        WarningsVisibility = Visibility.Visible;
    }

    private void ClearPlanRows()
    {
        foreach (var row in _rows) row.PropertyChanged -= OnRowPropertyChanged;
        _rows.Clear();
        PlanGroups.Clear();
    }

    private void ClearPlan()
    {
        CancelThumbnails();
        _plan = null;
        ClearPlanRows();
        HasPlan = false;
        PlanVisibility = Visibility.Collapsed;
        PlanProgressVisibility = Visibility.Collapsed;
        PlanGroupsVisibility = Visibility.Collapsed;
        PlanEmptyVisibility = Visibility.Collapsed;
        DuplicateHintVisibility = Visibility.Collapsed;
        WarningsVisibility = Visibility.Collapsed;
        PlanStatText = "";
        SelectedCountText = "";
        PlanStatusText = "";
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ImportGroupRow.IsSelected)) return;
        if (_suppressRowSync) return;
        UpdatePlanSummary();
    }

    /// <summary>统计条按"当前勾选"实时重算；主按钮可用性也跟着走。</summary>
    private void UpdatePlanSummary()
    {
        var selected = _rows.Where(r => r.IsSelected).ToList();
        var files = selected.Sum(r => r.FileCount);
        var bytes = selected.Sum(r => r.SizeBytes);
        PlanStatText = $"{selected.Count} 组 · {files} 个文件 · 共 {Formatting.FileSize(bytes)}";
        SelectedCountText = _rows.Count == 0
            ? ""
            : selected.Count == 0
                ? $"未勾选任何组（共 {_rows.Count} 组）"
                : selected.Count == _rows.Count
                    ? $"已全选 {_rows.Count} 组"
                    : $"已选 {selected.Count}/{_rows.Count} 组";
        NotifyBusyState();
    }

    public void SelectAll() => SetSelection(_ => true);

    public void SelectNone() => SetSelection(_ => false);

    /// <summary>只选 RAW+JPEG 成对的组。</summary>
    public void SelectPairsOnly() => SetSelection(r => r.Group.HasRawJpegPair);

    /// <summary>只选"只有 RAW 单张"的组。</summary>
    public void SelectRawOnly() => SetSelection(r => !r.Group.HasRawJpegPair && !string.IsNullOrWhiteSpace(r.Group.RawPath));

    private void SetSelection(Func<ImportGroupRow, bool> predicate)
    {
        _suppressRowSync = true;
        try
        {
            foreach (var row in _rows) row.IsSelected = predicate(row);
        }
        finally
        {
            _suppressRowSync = false;
        }

        UpdatePlanSummary();
    }

    // ==================================================================================
    //  缩略图（JPEG 直接解码；RAW 单张显示占位）
    // ==================================================================================
    private void StartThumbnailLoad()
    {
        CancelThumbnails();
        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        _ = LoadThumbnailsAsync(_rows.ToList(), cts.Token);
    }

    public void CancelThumbnails()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<ImportGroupRow> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(row.JpegPath)) continue;
            try
            {
                var image = await ImageSourceLoader.LoadFromFileAsync(row.JpegPath!, 96, ct);
                if (ct.IsCancellationRequested) return;
                row.SetThumbnail(image);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"[import] 缩略图加载失败 {row.JpegPath}: {ex.Message}");
                row.SetThumbnail(null);
            }
        }
    }

    // ==================================================================================
    //  ③ 设置
    // ==================================================================================
    [ObservableProperty]
    private string _destinationRoot = "";

    [ObservableProperty]
    private string _subfolderTemplate = "{yyyy}/{yyyy-MM-dd}";

    [ObservableProperty]
    private bool _copyInsteadOfMove = true;

    [ObservableProperty]
    private bool _includeSidecars = true;

    [ObservableProperty]
    private bool _skipExisting = true;

    [ObservableProperty]
    private bool _verifyAfterCopy = true;

    [ObservableProperty]
    private bool _strictVerify = true;

    [ObservableProperty]
    private bool _indexAfterCopy = true;

    public void SetDestinationRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        DestinationRoot = path;
        if (string.IsNullOrWhiteSpace(VerifyDestinationRoot)) VerifyDestinationRoot = path;
    }

    private ImportOptions BuildOptions() => new()
    {
        DestinationRoot = DestinationRoot,
        SubfolderTemplate = string.IsNullOrWhiteSpace(SubfolderTemplate) ? null : SubfolderTemplate,
        CopyInsteadOfMove = CopyInsteadOfMove,
        IncludeSidecars = IncludeSidecars,
        SkipExisting = SkipExisting,
        VerifyAfterCopy = VerifyAfterCopy,
        StrictVerify = StrictVerify,
        IndexAfterCopy = IndexAfterCopy,
    };

    // ==================================================================================
    //  ④ 执行与报告
    // ==================================================================================
    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private double _importPercent;

    [ObservableProperty]
    private string _importStatusText = "";

    [ObservableProperty]
    private string _importCurrentFile = "";

    [ObservableProperty]
    private Visibility _importProgressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private bool _hasReport;

    [ObservableProperty]
    private Visibility _reportVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _reportTitleText = "";

    [ObservableProperty]
    private string _reportSummaryText = "";

    [ObservableProperty]
    private string _reportStatsText = "";

    [ObservableProperty]
    private string _reportVerifyText = "";

    [ObservableProperty]
    private string _reportElapsedText = "";

    [ObservableProperty]
    private Visibility _reportSuccessVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _reportDangerVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _reportFailedVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _lastImportDestination = "";

    /// <summary>报告卡上的「打开目标目录」是否可用。</summary>
    public bool CanOpenDestination => !string.IsNullOrWhiteSpace(LastImportDestination);

    partial void OnLastImportDestinationChanged(string value)
        => OnPropertyChanged(nameof(CanOpenDestination));

    /// <summary>执行导入：勾选状态同步回计划 → ExecuteAsync(progress) → 报告卡。</summary>
    public async Task StartImportAsync()
    {
        if (_plan is null || IsImporting || IsScanningPlan) return;

        _suppressRowSync = true;
        try
        {
            foreach (var row in _rows) row.Group.IsSelected = row.IsSelected;
        }
        finally
        {
            _suppressRowSync = false;
        }

        if (_plan.SelectedGroupCount == 0)
        {
            AppEvents.SetStatus("请至少勾选一组再开始导入");
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationRoot))
        {
            AppEvents.SetError("请先选择导入目标目录");
            return;
        }

        _importCts?.Cancel();
        _importCts?.Dispose();
        var cts = new CancellationTokenSource();
        _importCts = cts;

        _plan.Options = BuildOptions();
        LastImportDestination = _plan.Options.DestinationRoot;
        HasReport = false;
        ReportVisibility = Visibility.Collapsed;
        ReportFailedItems.Clear();
        IsImporting = true;
        ImportPercent = 0;
        ImportStatusText = "正在准备导入…";
        ImportCurrentFile = "";
        ImportProgressVisibility = Visibility.Visible;
        NotifyBusyState();
        try
        {
            var progress = new Progress<ImportProgress>(p =>
            {
                ImportPercent = Math.Clamp(p.Percent, 0, 100);
                ImportStatusText = $"文件 {p.FilesProcessed}/{p.FilesTotal} · 复制中 {Formatting.FileSize(p.BytesProcessed)}";
                ImportCurrentFile = string.IsNullOrWhiteSpace(p.CurrentFile)
                    ? (string.IsNullOrWhiteSpace(p.Message) ? "" : p.Message!)
                    : Path.GetFileName(p.CurrentFile!);
            });

            var report = await AppServices.Import.ExecuteAsync(_plan, progress, cts.Token);
            ApplyImportReport(report);
            AppEvents.RaiseLibraryContentChanged();
            AppEvents.SetStatus(report.Success
                ? $"导入完成：复制 {report.FilesCopied} 个文件（{Formatting.FileSize(report.BytesCopied)}）"
                : $"导入完成但有异常：失败 {report.FilesFailed} / 损坏 {report.FilesCorrupted} / 缺失 {report.FilesMissing}");
        }
        catch (OperationCanceledException)
        {
            ImportStatusText = "导入已取消";
            AppEvents.SetStatus("导入已取消（已完成的文件保留在目标目录）");
        }
        catch (Exception ex)
        {
            Log.Warn("[import] 执行导入失败", ex);
            ImportStatusText = $"导入失败：{ex.Message}";
            AppEvents.SetError($"导入失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_importCts, cts))
            {
                IsImporting = false;
                ImportProgressVisibility = Visibility.Collapsed;
            }

            cts.Dispose();
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
        }
    }

    public void CancelImport() => _importCts?.Cancel();

    private void ApplyImportReport(ImportReport report)
    {
        HasReport = true;
        ReportVisibility = Visibility.Visible;
        ReportTitleText = report.Success ? "导入完成" : "导入完成（有失败 / 损坏 / 缺失）";
        ReportSummaryText = report.Summary;
        ReportStatsText =
            $"组 {report.GroupsImported}/{report.GroupsTotal} · 文件 复制 {report.FilesCopied} / 跳过 {report.FilesSkipped} / 失败 {report.FilesFailed} · 共 {Formatting.FileSize(report.BytesCopied)}";
        ReportVerifyText = $"核验通过 {report.FilesVerified} · 损坏 {report.FilesCorrupted} · 缺失 {report.FilesMissing}";
        ReportElapsedText = $"耗时 {FormatElapsed(report.Elapsed)}";
        ReportSuccessVisibility = report.Success ? Visibility.Visible : Visibility.Collapsed;
        ReportDangerVisibility = report.Success ? Visibility.Collapsed : Visibility.Visible;

        ReportFailedItems.Clear();
        var failures = report.Items
            .Where(i => IsFailure(i))
            .Take(MaxReportRows)
            .ToList();
        foreach (var item in failures)
        {
            var fileName = Path.GetFileName(item.SourcePath);
            ReportFailedItems.Add(new ImportReportItemRow(
                item,
                string.IsNullOrWhiteSpace(fileName) ? item.SourcePath : fileName,
                StatusText(item.Status),
                VerifyText(item.VerifyStatus)));
        }

        ReportFailedVisibility = ReportFailedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsFailure(ImportItemResult item) =>
        item.Status is "Failed" ||
        item.VerifyStatus is "Mismatch" or "Missing" ||
        !string.IsNullOrWhiteSpace(item.Error);

    private static string StatusText(string status) => status switch
    {
        "Copied" => "已复制",
        "Skipped" => "已跳过",
        "Failed" => "失败",
        "Pending" => "待处理",
        _ => status,
    };

    private static string VerifyText(string verify) => verify switch
    {
        "Verified" => "核验通过",
        "Mismatch" => "内容不一致",
        "Corrupted" => "内容不一致",
        "Missing" => "目标缺失",
        "Skipped" => "已跳过",
        _ => "未核验",
    };

    /// <summary>「再导入一次」：清空报告，按原源目录重新出计划。</summary>
    public async Task ReimportAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastPlanSource))
        {
            AppEvents.SetStatus("没有可重复的导入源，请先扫描设备或文件夹");
            return;
        }

        HasReport = false;
        ReportVisibility = Visibility.Collapsed;
        ReportFailedItems.Clear();
        await BuildPlanForPathAsync(_lastPlanSource);
    }

    // ==================================================================================
    //  ⑤ 核验导出（任意两棵目录树）
    // ==================================================================================
    [ObservableProperty]
    private string _verifySourceRoot = "";

    [ObservableProperty]
    private string _verifyDestinationRoot = "";

    [ObservableProperty]
    private bool _verifyStrictMode = true;

    [ObservableProperty]
    private bool _isVerifying;

    [ObservableProperty]
    private double _verifyPercent;

    [ObservableProperty]
    private string _verifyStatusText = "";

    [ObservableProperty]
    private Visibility _verifyProgressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private bool _hasVerifyReport;

    [ObservableProperty]
    private Visibility _verifyReportVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _verifySummaryText = "";

    [ObservableProperty]
    private string _verifyDetailText = "";

    [ObservableProperty]
    private Visibility _verifySuccessVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _verifyDangerVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _verifyIssuesVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _verifyResultTitle = "";

    [ObservableProperty]
    private string _verifyIssueCountText = "";

    public void SetVerifySourceRoot(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) VerifySourceRoot = path;
    }

    public void SetVerifyDestinationRoot(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) VerifyDestinationRoot = path;
    }

    /// <summary>目录级核验：逐文件哈希比对 + 源目录文件数对账。</summary>
    public async Task StartVerifyAsync()
    {
        if (IsVerifying) return;
        if (string.IsNullOrWhiteSpace(VerifySourceRoot) || string.IsNullOrWhiteSpace(VerifyDestinationRoot))
        {
            AppEvents.SetError("请先选择核验的源目录与目标目录");
            return;
        }

        _verifyCts?.Cancel();
        _verifyCts?.Dispose();
        var cts = new CancellationTokenSource();
        _verifyCts = cts;

        IsVerifying = true;
        VerifyProgressVisibility = Visibility.Visible;
        VerifyReportVisibility = Visibility.Collapsed;
        VerifyPercent = 0;
        VerifyStatusText = "正在准备核验…";
        NotifyBusyState();
        try
        {
            var strict = VerifyStrictMode;
            var progress = new Progress<int>(p =>
            {
                // 核验服务的进度是"已处理文件数"（不是百分比），进度条走不确定态，文字给出计数。
                VerifyPercent = Math.Clamp(p, 0, 100);
                VerifyStatusText = $"{(strict ? "SHA-256 逐文件比对" : "大小 + 首尾指纹比对")} · 已比对 {p} 个文件…";
            });

            var report = await AppServices.Verify.VerifyFolderAsync(
                VerifySourceRoot, VerifyDestinationRoot, strict, progress, cts.Token);
            ApplyVerifyReport(report);
            AppEvents.SetStatus(report.Success
                ? $"核验通过：{report.Passed}/{report.Total} 个文件一致"
                : $"核验发现问题：缺失 {report.Missing} / 损坏 {report.Corrupted} / 错误 {report.Errors}");
        }
        catch (OperationCanceledException)
        {
            VerifyStatusText = "核验已取消";
            AppEvents.SetStatus("核验已取消");
        }
        catch (Exception ex)
        {
            Log.Warn("[import] 目录核验失败", ex);
            VerifyStatusText = $"核验失败：{ex.Message}";
            AppEvents.SetError($"核验失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_verifyCts, cts))
            {
                IsVerifying = false;
                VerifyProgressVisibility = Visibility.Collapsed;
            }

            cts.Dispose();
            if (ReferenceEquals(_verifyCts, cts)) _verifyCts = null;
        }
    }

    public void CancelVerify() => _verifyCts?.Cancel();

    private void ApplyVerifyReport(VerifyReport report)
    {
        HasVerifyReport = true;
        VerifyReportVisibility = Visibility.Visible;
        VerifyResultTitle = report.Success ? "核验通过：导出完整" : "核验未通过：导出有缺失或损坏";
        VerifySummaryText = report.Summary;
        VerifyDetailText =
            $"{(report.Strict ? "严格模式（SHA-256 全文件哈希）" : "快速模式（大小 + 首尾指纹）")} · " +
            $"比对 {report.Total} 个文件 · 通过 {report.Passed} / 缺失 {report.Missing} / 损坏 {report.Corrupted} / 错误 {report.Errors} · " +
            $"耗时 {FormatElapsed(report.Elapsed)}";
        VerifySuccessVisibility = report.Success ? Visibility.Visible : Visibility.Collapsed;
        VerifyDangerVisibility = report.Success ? Visibility.Collapsed : Visibility.Visible;

        VerifyIssues.Clear();
        var allIssues = report.Items.Where(i => i.Status is not "Passed").ToList();
        foreach (var item in allIssues.Take(MaxReportRows))
        {
            var fileName = Path.GetFileName(item.SourcePath);
            VerifyIssues.Add(new VerifyIssueRow(
                string.IsNullOrWhiteSpace(fileName) ? item.SourcePath : fileName,
                item.Status switch
                {
                    "Missing" => "缺失（漏导）",
                    "Corrupted" => "损坏（内容不一致）",
                    "Error" => "读取错误",
                    _ => item.Status,
                },
                string.IsNullOrWhiteSpace(item.Error)
                    ? $"{item.SourcePath} → {item.DestinationPath ?? "（目标缺失）"}"
                    : item.Error!));
        }

        VerifyIssuesVisibility = VerifyIssues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VerifyIssueCountText = allIssues.Count == 0
            ? "没有问题项"
            : allIssues.Count > MaxReportRows
                ? $"问题项 {allIssues.Count} 条（最多显示前 {MaxReportRows} 条）"
                : $"问题项 {allIssues.Count} 条";
    }

    // ==================================================================================
    //  忙碌状态 / 通用
    // ==================================================================================
    public bool IsBusy => IsScanningPlan || IsImporting || IsVerifying;

    public bool CanStartImport => HasPlan && !IsBusy;

    public Visibility StartImportVisibility => IsImporting ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CancelImportVisibility => IsImporting ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StartVerifyVisibility => IsVerifying ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CancelVerifyVisibility => IsVerifying ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyBusyState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartImport));
        OnPropertyChanged(nameof(StartImportVisibility));
        OnPropertyChanged(nameof(CancelImportVisibility));
        OnPropertyChanged(nameof(StartVerifyVisibility));
        OnPropertyChanged(nameof(CancelVerifyVisibility));
    }

    partial void OnIsScanningPlanChanged(bool value) => NotifyBusyState();
    partial void OnIsImportingChanged(bool value) => NotifyBusyState();
    partial void OnIsVerifyingChanged(bool value) => NotifyBusyState();
    partial void OnHasPlanChanged(bool value) => NotifyBusyState();

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{elapsed.TotalSeconds:0.0} 秒";
}