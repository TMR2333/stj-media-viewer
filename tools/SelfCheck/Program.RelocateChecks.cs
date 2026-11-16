using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 媒体库重新定位：删掉/移动了软件文件夹或库目录后，点「重新定位」应把已有素材指到新目录，
    /// 缩略图随浏览自动重生成；在新目录里确实找不到的文件要被如实计数。
    /// </summary>
    private static async Task CheckLibraryRelocate(Services s, string tempData)
    {
        var baseDir = Path.Combine(tempData, "relocate");
        var oldRoot = Path.Combine(baseDir, "old-root");
        Directory.CreateDirectory(Path.Combine(oldRoot, "2024", "上海"));
        File.WriteAllBytes(Path.Combine(oldRoot, "a.jpg"), new byte[128]);
        File.WriteAllBytes(Path.Combine(oldRoot, "2024", "上海", "b.jpg"), new byte[256]);
        File.WriteAllBytes(Path.Combine(oldRoot, "gone.jpg"), new byte[64]);

        var library = await s.Library.AddLibraryAsync(oldRoot);

        var assets = new List<MediaAsset>
        {
            NewAsset("reloc-a", library.Id, Path.Combine(oldRoot, "a.jpg"), "a.jpg"),
            NewAsset("reloc-b", library.Id, Path.Combine(oldRoot, "2024", "上海", "b.jpg"), "b.jpg"),
            NewAsset("reloc-gone", library.Id, Path.Combine(oldRoot, "gone.jpg"), "gone.jpg"),
        };
        await s.Store.UpsertManyAsync(assets);

        // 模拟"软件/库文件夹被移动"：old-root → new-root，并且其中一个文件在新目录里没了
        var newRoot = Path.Combine(baseDir, "new-root");
        Directory.Move(oldRoot, newRoot);
        File.Delete(Path.Combine(newRoot, "gone.jpg"));

        var result = await s.Library.RelocateLibraryAsync(library.Id, newRoot);
        Check("重定位：修复条数", result.Updated == 2, $"{result.Updated} 条");
        Check("重定位：缺失条数", result.Missing == 1, $"{result.Missing} 条");

        var roots = await s.Library.GetLibrariesAsync();
        var root = roots.FirstOrDefault(r => r.Id == library.Id);
        Check("重定位：库根路径已更新",
              root is not null && string.Equals(root.Path, Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase),
              root?.Path ?? "(无)");

        var reloaded = await s.Store.GetAsync("reloc-a");
        Check("重定位：素材路径改到新目录",
              reloaded is not null && File.Exists(reloaded.FullPath) &&
              reloaded.FullPath.StartsWith(Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase),
              reloaded?.FullPath ?? "(无)");

        var nested = await s.Store.GetAsync("reloc-b");
        Check("重定位：保留子目录相对结构",
              nested is not null && File.Exists(nested.FullPath) &&
              nested.RelativePath.Replace('\\', '/') == "2024/上海/b.jpg",
              nested?.RelativePath ?? "(无)");

        var missing = await s.Store.GetAsync("reloc-gone");
        Check("重定位：找不到的文件保留待清理",
              missing is not null && !File.Exists(missing.FullPath));

        // 同一个库重复定位到同一路径应是幂等的
        var again = await s.Library.RelocateLibraryAsync(library.Id, newRoot);
        Check("重定位：同路径幂等", again.Updated == 0 && again.Missing == 0);
    }

    private static MediaAsset NewAsset(string id, string libraryId, string fullPath, string fileName) => new()
    {
        Id = id,
        LibraryId = libraryId,
        FullPath = fullPath,
        RelativePath = fileName,
        FileName = fileName,
        SizeBytes = 128,
        Kind = MediaKind.Image,
        FormatCode = "JPG",
        MimeType = "image/jpeg",
        FileModified = DateTimeOffset.Now,
        FileCreated = DateTimeOffset.Now,
    };
}