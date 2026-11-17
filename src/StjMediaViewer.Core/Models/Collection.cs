namespace StjMediaViewer.Core.Models;

/// <summary>
/// 影像合集：用户自建的一组素材（与"媒体库/筛选"独立的多对多归类）。
/// 一张素材可以同时进多个合集，素材本身不会被移动或复制。
/// </summary>
public sealed class Collection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int SortOrder { get; set; }

    /// <summary>合集里的素材数量（查询时带出，便于界面显示）。</summary>
    public int AssetCount { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名合集" : Name;
}
