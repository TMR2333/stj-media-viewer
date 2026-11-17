namespace StjMediaViewer.Core.Models;

/// <summary>
/// 合并组：把多重曝光 / 星轨 / 演示视频这类"一次拍摄出很多张"的素材合并成一个大文件夹，
/// 在素材库里只显示一条（封面 + 数量），点开才看到成员。
///
/// 只记录关系，不动磁盘文件；封面默认为第一张（按加入顺序），可自定义。
/// </summary>
public sealed class MergeGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>封面素材 id；为空时用组里第一张。</summary>
    public string? CoverAssetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int SortOrder { get; set; }

    /// <summary>成员数量（查询时带出）。</summary>
    public int AssetCount { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名合并组" : Name;
}
