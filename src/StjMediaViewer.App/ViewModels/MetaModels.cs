using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.App.ViewModels;

/// <summary>元数据表里的一行：label + value + 来源徽标。</summary>
public sealed class MetaRow
{
    public MetaRow(string label, string? value, ClassDimension? dimension = null, FieldProvenance? provenance = null)
    {
        Label = label;
        Value = string.IsNullOrWhiteSpace(value) ? "—" : value!;
        var source = provenance?.Source ?? ClassSource.None;
        HasProvenance = dimension is not null && source != ClassSource.None;
        Source = source;
        ProvenanceText = Formatting.ProvenanceName(source);
        ConfidenceText = provenance is null || source is not ClassSource.Ai and not ClassSource.Derived
            ? ""
            : $"{provenance.Confidence * 100:0}%";
    }

    public string Label { get; }
    public string Value { get; }
    public bool HasProvenance { get; }
    public ClassSource Source { get; }
    public string ProvenanceText { get; }
    public string ConfidenceText { get; private set; }

    public Visibility ProvenanceVisibility => HasProvenance ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConfidenceVisibility => HasProvenance && !string.IsNullOrEmpty(ConfidenceText)
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ManualVisibility => Source == ClassSource.Manual ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AiVisibility => Source == ClassSource.Ai ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DerivedVisibility => Source == ClassSource.Derived ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SidecarVisibility => Source == ClassSource.Sidecar ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>来源未知但存在（例如 AI 分类后再手工改过）时用中性徽标。</summary>
    public Visibility NeutralVisibility => HasProvenance &&
        Source is not (ClassSource.Manual or ClassSource.Ai or ClassSource.Derived or ClassSource.Sidecar)
        ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>元数据分组（文件 / 技术 / 拍摄 / 地点 / 色彩 / 后期 / 分类 / AI）。</summary>
public sealed class MetaGroup
{
    public MetaGroup(string title) => Title = title;

    public string Title { get; }
    public ObservableCollection<MetaRow> Rows { get; } = new();
    public ObservableCollection<MetaRow> VisibleRows { get; } = new();

    /// <summary>默认展开：常用分组（文件 / 拍摄 / 分类）展开，其余折叠，保持面板密度。</summary>
    public bool IsExpanded { get; set; } = true;

    public Visibility Visibility { get; set; } = Visibility.Visible;

    public MetaGroup Add(string label, string? value)
    {
        Rows.Add(new MetaRow(label, value, null, null));
        return this;
    }

    /// <summary>带来源徽标的行：维度 + 素材（内部取 Provenance）。</summary>
    public MetaGroup Add(string label, string? value, ClassDimension dimension, MediaAsset? asset)
    {
        Rows.Add(new MetaRow(label, value, dimension, asset?.Prov(dimension)));
        return this;
    }
}

/// <summary>下拉选项（主题 / 影调 / 城市）。</summary>
public sealed record OptionItem(string Id, string Display)
{
    public override string ToString() => Display;
}