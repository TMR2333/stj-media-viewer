namespace StjMediaViewer.Core.Models;

/// <summary>
/// 素材库的"媒体类型范围"：设置里开启「照片 / 视频分开成两个库」后，
/// 侧边栏会给出两个库，各自只显示对应类型的素材。
/// </summary>
public enum LibraryScope
{
    /// <summary>不分库：一个素材库显示全部。</summary>
    All = 0,
    /// <summary>照片库：只显示图片。</summary>
    Photos = 1,
    /// <summary>视频库：只显示视频/音频等非图片素材。</summary>
    Videos = 2,
}

public static class LibraryScopeRules
{
    /// <summary>视图 id（侧边栏 Tag）→ 范围。</summary>
    public static LibraryScope FromViewId(string? viewId) => viewId?.ToLowerInvariant() switch
    {
        "library-photo" or "library-photos" => LibraryScope.Photos,
        "library-video" or "library-videos" => LibraryScope.Videos,
        _ => LibraryScope.All,
    };

    /// <summary>范围 → 视图 id。</summary>
    public static string ToViewId(LibraryScope scope) => scope switch
    {
        LibraryScope.Photos => "library-photo",
        LibraryScope.Videos => "library-video",
        _ => "library",
    };

    /// <summary>把范围写进查询（照片库=只有图片；视频库=除图片外的视频/音频）。</summary>
    public static void Apply(AssetQuery query, LibraryScope scope)
    {
        if (query is null) return;
        switch (scope)
        {
            case LibraryScope.Photos:
                query.Kinds.Clear();
                query.Kinds.Add(MediaKind.Image);
                break;
            case LibraryScope.Videos:
                query.Kinds.Clear();
                query.Kinds.Add(MediaKind.Video);
                query.Kinds.Add(MediaKind.Audio);
                break;
        }
    }

    public static string Title(LibraryScope scope) => scope switch
    {
        LibraryScope.Photos => "照片库",
        LibraryScope.Videos => "视频库",
        _ => "素材库",
    };

    /// <summary>某条素材按范围归到哪个库（导卡后切库 / 状态提示用）。</summary>
    public static LibraryScope ScopeOf(MediaKind kind) =>
        kind == MediaKind.Image ? LibraryScope.Photos : LibraryScope.Videos;
}
