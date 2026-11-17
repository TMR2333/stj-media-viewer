using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 网格 / 列表里一张卡片的数据视图。
/// 只暴露"已经算好、可直接 x:Bind"的展示属性；颜色全部留在 XAML 的 ThemeResource 里，
/// 因此换皮肤 / 换明暗不需要重建这些对象。
/// </summary>
public sealed partial class AssetCardItem : ObservableObject
{
    private bool _selected;

    public AssetCardItem(MediaAsset asset)
    {
        _asset = asset;
        Rebuild();
    }

    private MediaAsset _asset;

    /// <summary>可观察：预览里做了旋转/翻转后要触发缩略图重新加载。</summary>
    /// <summary>如果这张卡片代表一个"合并组"（多重曝光 / 星轨 / 演示视频合并成的大文件夹），这里存组信息。</summary>
    public MergeGroup? MergeGroup { get; private set; }

    public bool IsMergeGroupCard => MergeGroup is not null;

    public string MergeBadgeText => MergeGroup is null ? "" : $"合并 · {MergeGroup.AssetCount} 张";
    public Visibility MergeBadgeVisibility => MergeGroup is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>把卡片标记成合并组卡片（显示封面素材 + 组徽标）。</summary>
    public void ApplyMergeGroup(MergeGroup? group)
    {
        MergeGroup = group;
        OnPropertyChanged(nameof(IsMergeGroupCard));
        OnPropertyChanged(nameof(MergeBadgeText));
        OnPropertyChanged(nameof(MergeBadgeVisibility));
    }

    public MediaAsset Asset
    {
        get => _asset;
        private set
        {
            if (ReferenceEquals(_asset, value)) return;
            _asset = value;
            OnPropertyChanged();
        }
    }

    // ---------------- 文本 ----------------
    public string Id => Asset.Id;
    public string FileName => Asset.FileName;
    public string DeviceText => Formatting.Device(Asset);
    public string DateText => Formatting.Date(Asset.CapturedAt ?? Asset.FileModified);
    public string DeviceDateText => $"{Formatting.Device(Asset)} · {DateText}";
    public string SizeText => Formatting.FileSize(Asset.SizeBytes);
    public string DurationText => Formatting.Duration(Asset.DurationSeconds);
    public string FormatText => string.IsNullOrWhiteSpace(Asset.FormatCode) ? "—" : Asset.FormatCode.ToUpperInvariant();
    public string ThemeText => string.IsNullOrWhiteSpace(Asset.Theme) ? "未分类" : Vocabularies.ThemeDisplay(Asset.Theme, "zh-CN");
    public string ToneText => string.IsNullOrWhiteSpace(Asset.Tone) ? "未分类" : Vocabularies.ToneDisplay(Asset.Tone, "zh-CN");
    public string ColorLabelName => Formatting.ColorLabelName(Asset.ColorLabel);
    public string KindName => Formatting.KindName(Asset.Kind);
    public string DimensionsText => Formatting.Dimensions(Asset.Width, Asset.Height);
    public string TooltipText =>
        $"{Asset.FileName}\n{Formatting.Device(Asset)} · {DateText}\n{DimensionsText} · {SizeText}\n" +
        $"主题 {ThemeText} · 影调 {ToneText}";

    // ---------------- 评分 ----------------
    public int Rating => Math.Clamp(Asset.Rating, 0, 5);
    public string StarsFilled => Formatting.StarsFilled(Rating);
    public string StarsEmpty => Formatting.StarsEmpty(Rating);
    public Visibility StarsVisibility => Rating > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- 徽标 ----------------
    // 配对组用 RAW&amp;JPEG 徽标统一表达，避免同时出现"RAW"和"RAW&amp;JPEG"两个角标
    public Visibility RawBadgeVisibility => Asset.IsRaw && !Asset.IsRawJpegPair ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>RAW&amp;JPEG 配对标签：这一项同时拥有 RAW 原件与 JPEG 预览。</summary>
    public Visibility RawJpegBadgeVisibility => Asset.IsRawJpegPair ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>组内副本数提示（如 "+1 RAW"），折叠时让用户知道还有别的文件。</summary>
    public string CompanionText => Asset.CompanionPaths.Count == 0 ? "" : "+" + Asset.CompanionPaths.Count + " 个同组文件";
    public Visibility CompanionVisibility => Asset.CompanionPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LogBadgeVisibility => Asset.IsLog == true ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>视频编码徽标（H.264 / H.265 等），只有视频显示。</summary>
    public string CodecBadgeText => Asset.IsVideo
        ? StjMediaViewer.Core.Transcode.VideoCodecNames.ShortBadge(Asset.VideoCodec)
        : "";
    public Visibility CodecBadgeVisibility => Asset.IsVideo && !string.IsNullOrEmpty(CodecBadgeText)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility AiBadgeVisibility => HasAi ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VideoBadgeVisibility => Asset.IsVideo ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AudioBadgeVisibility => Asset.IsAudio ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageBadgeVisibility => Asset.IsImage && !Asset.IsRaw ? Visibility.Visible : Visibility.Collapsed;
    public bool HasAi =>
        Asset.Prov(ClassDimension.Theme)?.Source == ClassSource.Ai ||
        Asset.Prov(ClassDimension.Tone)?.Source == ClassSource.Ai ||
        !string.IsNullOrWhiteSpace(Asset.AiSubject);

    public Visibility PickBadgeVisibility => Asset.IsPick == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RejectBadgeVisibility => Asset.IsPick == false ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- 颜色标签（用 Visibility 切换，配色留在 XAML token） ----------------
    public Visibility LabelRedVisibility => LabelIs("red") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelYellowVisibility => LabelIs("yellow") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelGreenVisibility => LabelIs("green") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelBlueVisibility => LabelIs("blue") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelPurpleVisibility => LabelIs("purple") ? Visibility.Visible : Visibility.Collapsed;
    private bool LabelIs(string id) => string.Equals(Asset.ColorLabel, id, StringComparison.OrdinalIgnoreCase);

    // ---------------- 选中态 ----------------
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
                OnPropertyChanged(nameof(SelectionVisibility));
        }
    }

    public Visibility SelectionVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>素材被就地修改后重算全部展示属性（评分、色标、分类、AI 结果）。</summary>
    public void Rebuild()
    {
        OnPropertyChanged(string.Empty);
    }

    /// <summary>基类素材对象被替换时（例如 Upsert 之后拿回新实例）重新绑定。</summary>
    public void UpdateAsset(MediaAsset asset)
    {
        // 对象实例可能没变（预览编辑只改了字段），所以先清空再设回去，强制 ThumbnailImage 重载
        var current = _asset;
        _asset = null!;
        OnPropertyChanged(nameof(Asset));
        _asset = ReferenceEquals(current, asset) ? asset : asset;
        OnPropertyChanged(nameof(Asset));
        Rebuild();
    }
}