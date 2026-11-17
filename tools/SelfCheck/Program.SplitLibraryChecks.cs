using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 照片 / 视频分库：侧边栏视图 id ↔ 范围映射、查询过滤（照片库只出图片、视频库只出视频/音频）。
    /// </summary>
    private static async Task CheckSplitLibraries(Services s, string tempData)
    {
        Check("分库：默认开启", new AppSettings().SplitMediaKindLibraries);

        Check("分库：侧边栏 id ↔ 范围",
              LibraryScopeRules.FromViewId("library-photo") == LibraryScope.Photos &&
              LibraryScopeRules.FromViewId("library-video") == LibraryScope.Videos &&
              LibraryScopeRules.FromViewId("library") == LibraryScope.All &&
              LibraryScopeRules.ToViewId(LibraryScope.Photos) == "library-photo");

        var photoQuery = new AssetQuery { Kinds = { MediaKind.Video } };
        LibraryScopeRules.Apply(photoQuery, LibraryScope.Photos);
        Check("分库：照片库查询只含图片",
              photoQuery.Kinds.Count == 1 && photoQuery.Kinds[0] == MediaKind.Image);

        var videoQuery = new AssetQuery();
        LibraryScopeRules.Apply(videoQuery, LibraryScope.Videos);
        Check("分库：视频库查询含视频+音频",
              videoQuery.Kinds.Contains(MediaKind.Video) && videoQuery.Kinds.Contains(MediaKind.Audio) &&
              !videoQuery.Kinds.Contains(MediaKind.Image));

        var allQuery = new AssetQuery { Kinds = { MediaKind.Image } };
        LibraryScopeRules.Apply(allQuery, LibraryScope.All);
        Check("分库：不分库时不动媒体类型", allQuery.Kinds.Count == 1 && allQuery.Kinds[0] == MediaKind.Image);

        Check("分库：库标题", LibraryScopeRules.Title(LibraryScope.Photos) == "照片库" &&
                            LibraryScopeRules.Title(LibraryScope.Videos) == "视频库");
        Check("分库：素材按类型归类",
              LibraryScopeRules.ScopeOf(MediaKind.Image) == LibraryScope.Photos &&
              LibraryScopeRules.ScopeOf(MediaKind.Video) == LibraryScope.Videos &&
              LibraryScopeRules.ScopeOf(MediaKind.Audio) == LibraryScope.Videos);

        // 真实库验证：照片库查询结果里没有视频，视频库没有图片
        var photos = await s.Store.QueryAsync(new AssetQuery
        {
            Kinds = { MediaKind.Image },
            Limit = 50,
        });
        Check("分库：查询结果类型纯净（照片）",
              photos.Count > 0 && photos.All(a => a.Kind == MediaKind.Image), $"{photos.Count} 张");

        var videos = await s.Store.QueryAsync(new AssetQuery
        {
            Kinds = { MediaKind.Video, MediaKind.Audio },
            Limit = 50,
        });
        Check("分库：查询结果类型纯净（视频）",
              videos.All(a => a.Kind != MediaKind.Image), $"{videos.Count} 个");
    }
}